using System.Diagnostics;
using System.Text.Json;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Resolves system VM binary artifacts from a cosign-signed GitHub release manifest.
///
/// Replaces hardcoded SHA256 constants in SystemVmTemplateSeeder. The only
/// configuration input is the release tag (e.g. "binaries/v1.1.0"). Everything
/// else — SHA256, file size, download URL — is derived from the verified manifest.
///
/// Trust chain:
///   1. Signed git tag gates the CI build (verify-tag job).
///   2. CI builds binaries, generates manifest.json with SHA256 + sizes + URLs.
///   3. CI signs manifest.json with cosign keyless (GitHub OIDC).
///   4. This service fetches manifest + signature, verifies with cosign,
///      then extracts artifact metadata from the trusted manifest.
///   5. Node agent verifies SHA256 on artifact prefetch (defense in depth).
///
/// COSIGN DEPENDENCY: requires the cosign binary on PATH. Install via:
///   go install github.com/sigstore/cosign/v2/cmd/cosign@v2.4.1
///   — or — download from https://github.com/sigstore/cosign/releases
/// </summary>
public sealed class BinaryReleaseResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BinaryReleaseResolver> _logger;

    /// <summary>
    /// GitHub repository that hosts the system VM binary releases.
    /// </summary>
    private const string Repository = "bekirmfr/DeCloud.Builds";

    /// <summary>
    /// Cosign identity pattern — must match the workflow that signed the manifest.
    /// Pinned to this specific workflow file in the DeCloud.Builds repo.
    /// </summary>
    private const string CosignIdentityRegexp =
        @"^https://github\.com/bekirmfr/DeCloud\.Builds/\.github/workflows/release-binaries\.yml@";

    private const string CosignOidcIssuer =
        "https://token.actions.githubusercontent.com";

    // Cache: resolved once per release tag, reused across seed calls.
    private readonly Dictionary<string, BinaryManifest> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BinaryReleaseResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<BinaryReleaseResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetch, verify, and parse the binary release manifest for the given tag.
    /// Results are cached in memory — safe to call multiple times per tag.
    /// </summary>
    /// <param name="releaseTag">e.g. "binaries/v1.1.0"</param>
    public async Task<BinaryManifest> ResolveAsync(
        string releaseTag, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(releaseTag, out var cached))
                return cached;

            var manifest = await FetchAndVerifyAsync(releaseTag, ct);
            _cache[releaseTag] = manifest;
            return manifest;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<BinaryManifest> FetchAndVerifyAsync(
        string releaseTag, CancellationToken ct)
    {
        var encodedTag = releaseTag.Replace("/", "%2F");
        var baseUrl = $"https://github.com/{Repository}/releases/download/{encodedTag}";

        var client = _httpClientFactory.CreateClient("github-releases");

        _logger.LogInformation(
            "BinaryReleaseResolver: fetching manifest for {Tag}...", releaseTag);

        // ── 1. Fetch manifest + signature + certificate ─────────────────
        var manifestJson = await client.GetStringAsync($"{baseUrl}/manifest.json", ct);
        var signatureBytes = await client.GetByteArrayAsync($"{baseUrl}/manifest.json.sig", ct);
        var certBytes = await client.GetByteArrayAsync($"{baseUrl}/manifest.json.pem", ct);

        // ── 2. Write to temp files for cosign verify-blob ───────────────
        var tempDir = Path.Combine(Path.GetTempPath(), $"decloud-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var manifestPath = Path.Combine(tempDir, "manifest.json");
            var sigPath = Path.Combine(tempDir, "manifest.json.sig");
            var certPath = Path.Combine(tempDir, "manifest.json.pem");

            await File.WriteAllTextAsync(manifestPath, manifestJson, ct);
            await File.WriteAllBytesAsync(sigPath, signatureBytes, ct);
            await File.WriteAllBytesAsync(certPath, certBytes, ct);

            // ── 3. Verify cosign signature ──────────────────────────────
            await VerifyCosignAsync(manifestPath, sigPath, certPath, ct);

            // ── 4. Parse the verified manifest ──────────────────────────
            var manifest = ParseManifest(manifestJson, releaseTag);

            _logger.LogInformation(
                "BinaryReleaseResolver: verified manifest for {Tag} — " +
                "{Count} artifacts, built at {BuiltAt}",
                releaseTag, manifest.Artifacts.Count, manifest.BuiltAt);

            return manifest;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private async Task VerifyCosignAsync(
        string manifestPath, string sigPath, string certPath,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cosign",
            ArgumentList =
            {
                "verify-blob",
                "--certificate-identity-regexp", CosignIdentityRegexp,
                "--certificate-oidc-issuer", CosignOidcIssuer,
                "--signature", sigPath,
                "--certificate", certPath,
                manifestPath,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Failed to start cosign. Is it installed and on PATH?");

        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogError(
                "BinaryReleaseResolver: cosign verification FAILED.\n{Stderr}", stderr);

            throw new InvalidOperationException(
                $"Cosign manifest verification failed (exit {process.ExitCode}). " +
                $"The release manifest signature could not be verified. " +
                $"Refusing to use unverified artifact hashes. stderr: {stderr}");
        }

        _logger.LogInformation("BinaryReleaseResolver: cosign signature verified OK");
    }

    private static BinaryManifest ParseManifest(string json, string expectedTag)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = root.GetProperty("version").GetString()
            ?? throw new InvalidOperationException("Manifest missing 'version' field");

        var gitSha = root.GetProperty("git_sha").GetString() ?? "";
        var builtAt = root.GetProperty("built_at").GetString() ?? "";

        var artifacts = new Dictionary<string, BinaryArtifactInfo>();

        foreach (var prop in root.GetProperty("artifacts").EnumerateObject())
        {
            var name = prop.Name;
            var sha256 = prop.Value.GetProperty("sha256").GetString()
                ?? throw new InvalidOperationException($"Artifact '{name}' missing sha256");
            var size = prop.Value.GetProperty("size").GetInt64();
            var url = prop.Value.GetProperty("url").GetString()
                ?? throw new InvalidOperationException($"Artifact '{name}' missing url");

            artifacts[name] = new BinaryArtifactInfo(sha256, size, url);
        }

        return new BinaryManifest(version, gitSha, builtAt, artifacts);
    }
}

/// <summary>
/// Parsed and verified binary release manifest.
/// All fields are trusted — the cosign signature was verified before parsing.
/// </summary>
public sealed record BinaryManifest(
    string Version,
    string GitSha,
    string BuiltAt,
    IReadOnlyDictionary<string, BinaryArtifactInfo> Artifacts)
{
    /// <summary>
    /// Look up a binary artifact by its release asset name (e.g. "dht-node-amd64").
    /// Throws if not found — a missing artifact in a verified manifest is a build error.
    /// </summary>
    public BinaryArtifactInfo GetArtifact(string assetName) =>
        Artifacts.TryGetValue(assetName, out var info)
            ? info
            : throw new KeyNotFoundException(
                $"Binary artifact '{assetName}' not found in manifest {Version}. " +
                $"Available: {string.Join(", ", Artifacts.Keys)}");
}

/// <summary>
/// SHA256, size, and download URL for a single binary artifact.
/// </summary>
public sealed record BinaryArtifactInfo(string Sha256, long SizeBytes, string Url);
