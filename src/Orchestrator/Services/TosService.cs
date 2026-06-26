using MongoDB.Driver;
using Nethereum.Util;
using Orchestrator.Interfaces;
using Orchestrator.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Services;

/// <summary>
/// Loads the current ToS document at startup (version from config, hash computed
/// from the document bytes) and records/queries wallet-signed acceptances in the
/// "tos_acceptances" collection. Mirrors JwtRevocationService: own collection,
/// own positive cache, graceful no-Mongo fallback.
/// </summary>
public sealed class TosService : ITosService
{
    private readonly IMongoCollection<TosAcceptance>? _collection;
    private readonly ILogger<TosService> _logger;
    private readonly AddressUtil _addr = new();

    // Positive-acceptance cache. Key includes version+hash, so a ToS bump
    // (new hash) automatically invalidates prior cached "accepted" results.
    private readonly ConcurrentDictionary<string, bool> _accepted = new();

    private readonly string _version;
    private readonly string _text;
    private readonly string _hash;

    public TosService(IMongoDatabase? database, IConfiguration config, ILogger<TosService> logger)
    {
        _collection = database?.GetCollection<TosAcceptance>("tos_acceptances");
        _logger = logger;

        _version = config["Tos:Version"] ?? "unversioned";
        _text = LoadEmbeddedDocument();
        _hash = ComputeHash(_text);

        if (string.IsNullOrEmpty(_text))
        {
            _logger.LogWarning(
                "ToS document is empty — every tenant VM creation will be blocked until " +
                "the embedded 'terms-of-service.md' resource is present and non-empty. " +
                "Version={Version}", _version);
        }
        else
        {
            _logger.LogInformation(
                "ToS loaded: version={Version} hash={HashPrefix}… ({Bytes} bytes)",
                _version, _hash[..Math.Min(12, _hash.Length)], _text.Length);
        }
    }

    public TosDocument GetCurrent() => new(_version, _hash, _text);

    public string BuildAcceptanceMessage(string walletAddress, long timestamp)
    {
        var wallet = Normalize(walletAddress);
        // Canonical, deterministic. The frontend MUST reproduce this byte-for-byte.
        return
            "DeCloud Terms of Service Acceptance\n" +
            $"Wallet: {wallet}\n" +
            $"Version: {_version}\n" +
            $"Hash: {_hash}\n" +
            $"Timestamp: {timestamp}";
    }

    public async Task<bool> HasAcceptedCurrentAsync(string walletAddress, CancellationToken ct = default)
    {
        var wallet = Normalize(walletAddress);
        var cacheKey = CacheKey(wallet);
        if (_accepted.ContainsKey(cacheKey)) return true;

        // No persistence configured (dev/in-memory): fail closed for tenant VMs.
        if (_collection == null) return false;

        var id = AcceptanceId(wallet);
        var found = await _collection.Find(a => a.Id == id && a.TosHash == _hash).AnyAsync(ct);
        if (found) _accepted.TryAdd(cacheKey, true);
        return found;
    }

    public async Task RecordAcceptanceAsync(
        string walletAddress, string signature, long timestamp, CancellationToken ct = default)
    {
        var wallet = Normalize(walletAddress);

        var record = new TosAcceptance
        {
            Id = AcceptanceId(wallet),
            WalletAddress = wallet,
            TosVersion = _version,
            TosHash = _hash,
            Signature = signature,
            SignedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime,
        };

        if (_collection != null)
        {
            await _collection.ReplaceOneAsync(
                a => a.Id == record.Id, record,
                new ReplaceOptions { IsUpsert = true }, ct);
        }

        _accepted.TryAdd(CacheKey(wallet), true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Decision: addresses are checksum-normalized on both write and read so an
    // acceptance stored under one casing is never missed by a lookup in another.
    private string Normalize(string w) => _addr.ConvertToChecksumAddress(w);

    private string AcceptanceId(string wallet) => $"{wallet}:{_version}";
    private string CacheKey(string wallet) => $"{wallet}:{_version}:{_hash}";

    // Embedded as a resource (LogicalName "terms-of-service.md") in
    // Orchestrator.csproj — same pattern as the DeCloudEscrow ABI and the locality
    // JSON. Pins the document's SHA-256 to the build; no disk-path dependency.
    private string LoadEmbeddedDocument()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("terms-of-service.md");
        if (stream == null)
        {
            _logger.LogError(
                "Embedded resource 'terms-of-service.md' not found. Ensure " +
                "compliance/terms-of-service.md is included as EmbeddedResource with " +
                "<LogicalName>terms-of-service.md</LogicalName> in Orchestrator.csproj.");
            return string.Empty;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}