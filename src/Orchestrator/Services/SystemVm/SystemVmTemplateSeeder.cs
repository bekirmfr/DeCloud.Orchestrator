using DeCloud.Orchestrator.Services;
using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Seeds system VM templates (relay, dht, blockstore) into MongoDB at startup.
///
/// Uses the same unified artifact pipeline as community templates:
///   - Compiled binaries → HTTPS artifact (hosted on GitHub Releases in DeCloud.Builds)
///   - Scripts, dashboards, images → inline data: URI artifact (stored in template document)
///   - Static config (systemd units, nginx, env files) → embedded in CloudInitTemplate
///
/// UPDATING TEMPLATES
/// When any script or asset changes in DeCloud.Builds:
///   1. cd DeCloud.Builds
///   2. bash compute-artifact-constants.sh
///   3. Copy the updated constants for changed artifacts into this file
///   4. Bump the affected TemplateRevision constant (e.g., DhtTemplateRevision)
///   5. Commit to the Orchestrator repo
///
/// When a new binary release is cut:
///   1. Update BinaryReleaseTag, the HTTPS URLs, and the SHA256 constants
///   2. Bump the affected TemplateRevision constant
///   3. Commit
///
/// On next orchestrator startup, SeedAsync detects the higher revision and
/// updates MongoDB. Nodes pick up the new template on next registration
/// via the P9 systemTemplatesPending channel.
/// </summary>
public sealed partial class SystemVmTemplateSeeder
{
    // ── Binary release (HTTPS artifacts) ────────────────────────────────
    // Update these when a new binary release is cut in DeCloud.Builds.

    /// <summary>
    /// The only constant to update when cutting a new binary release.
    /// Everything else — SHA256, size, URL — is resolved from the
    /// cosign-verified manifest at seed time.
    /// </summary>
    private const string BinaryReleaseTag = "binaries/v1.1.5";

    /// <summary>
    /// Git ref (branch, tag, or commit SHA) used to fetch cloud-init YAML files
    /// from DeCloud.Builds at seed time. Pin this to the same logical version as
    /// BinaryBaseUrl. Update when the YAML changes and bump the affected
    /// TemplateRevision constant.
    /// </summary>
    private const string CloudInitRef = "main";

    private const string CloudInitRawBase = $"https://raw.githubusercontent.com/bekirmfr/DeCloud.Builds/{CloudInitRef}/system-vms";

    /// <summary>
    /// Repo root URL — used for base-template fetches. Distinct from
    /// <see cref="CloudInitRawBase"/> which points at <c>system-vms/</c>.
    /// </summary>
    private const string CloudInitRepoBase =
        $"https://raw.githubusercontent.com/bekirmfr/DeCloud.Builds/{CloudInitRef}";

    /// <summary>
    /// Base layer for relay (NOT a mesh participant — relay is the mesh hub).
    /// </summary>
    private const string BaseSystemUrl =
        $"{CloudInitRepoBase}/base-templates/base-system.yaml";

    /// <summary>
    /// Base layer for mesh-participant system VMs (DHT, BlockStore).
    /// Includes wg-mesh-watchdog units and wg-quick@wg-mesh override.
    /// </summary>
    private const string BaseSystemMeshUrl =
        $"{CloudInitRepoBase}/base-templates/base-system-mesh.yaml";

    // ── Inline artifact constants (data: URIs) ───────────────────────────
    // Supplied by the partial class in Services/TemplateConstants/
    // SystemVmTemplateSeeder.Artifacts.cs (auto-generated, do not edit).
    //
    // To regenerate after editing a script in DeCloud.Builds:
    //   cd DeCloud.Builds/system-vms
    //   bash compute-artifact-constants.sh
    //   (auto-copies to Services/TemplateConstants/)
    //   Bump the affected TemplateRevision constant below.

    // ── Infrastructure ───────────────────────────────────────────────────

    private readonly DataStore _dataStore;
    private readonly HttpClient _httpClient;
    private readonly BinaryReleaseResolver _releaseResolver;
    private readonly ILogger<SystemVmTemplateSeeder> _logger;

    public SystemVmTemplateSeeder(
        DataStore dataStore,
        HttpClient httpClient,
        BinaryReleaseResolver releaseResolver,
        ILogger<SystemVmTemplateSeeder> logger)
    {
        _dataStore = dataStore;
        _httpClient = httpClient;
        _releaseResolver = releaseResolver;
        _logger = logger;
    }

    // ── Entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Seed all three system VM templates. Called from TemplateSeederService.SeedAsync.
    /// Idempotent: skips if the stored revision is already current; replaces if lower.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Resolve binary artifact metadata from the cosign-verified release manifest.
        // Called once per seed; cached in memory for the process lifetime.
        var binaries = await _releaseResolver.ResolveAsync(BinaryReleaseTag, ct);

        await SeedTemplateAsync(await BuildRelayTemplateAsync(ct), ct);
        await SeedTemplateAsync(await BuildDhtTemplateAsync(binaries, ct), ct);
        await SeedTemplateAsync(await BuildBlockstoreTemplateAsync(binaries, ct), ct);
    }

    // ── Template builders ─────────────────────────────────────────────────

    private async Task<VmTemplate> BuildDhtTemplateAsync(BinaryManifest binaries, CancellationToken ct = default)
    {
        var amd64 = binaries.GetArtifact("dht-node-amd64");
        var arm64 = binaries.GetArtifact("dht-node-arm64");

        return new VmTemplate
        {
            Slug = "system-dht",
            Name = "DHT Peer Node",
            Description = "libp2p Kademlia DHT node for peer discovery, key-value storage, and GossipSub event propagation.",
            Version = "1.0.0",
            // Revision is derived from content hash in SeedTemplateAsync.
            Category = "infrastructure",
            AuthorId = "system",
            IsCommunity = false,
            IsVerified = true,
            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            PricingModel = TemplatePricingModel.Free,
            CloudInitTemplate = await FetchCloudInitAsync("dht", ct),
            Variables = BuildDhtVariables(),
            Artifacts = new List<TemplateArtifact>
            {
                // ── Binary (HTTPS — DeCloud.Builds release) ──────────────────
                TemplateArtifact.Artifact("dht-node", "DHT node binary (libp2p Kademlia)",
                    ArtifactType.Binary, arch: "amd64",
                    sha256: amd64.Sha256, sizeBytes: amd64.SizeBytes,
                    sourceUrl: amd64.Url),

                TemplateArtifact.Artifact("dht-node", "DHT node binary — ARM64",
                    ArtifactType.Binary, arch: "arm64",
                    sha256: arm64.Sha256, sizeBytes: arm64.SizeBytes,
                    sourceUrl: arm64.Url),

                // ── Shared scripts (data: URI inline) ────────────────────────
                TemplateArtifact.Artifact("wg-mesh-enroll", "WireGuard mesh enrollment script",
                    ArtifactType.Script,
                    sha256: WgMeshEnrollSha256, sourceUrl: WgMeshEnrollDataUri),

                TemplateArtifact.Artifact("wg-config-fetch", "WireGuard config fetch script",
                    ArtifactType.Script,
                    sha256: WgConfigFetchSha256, sourceUrl: WgConfigFetchDataUri),

                // ── DHT-specific scripts (data: URI inline) ───────────────────
                TemplateArtifact.Artifact("dht-health-check", "DHT health check script",
                    ArtifactType.Script,
                    sha256: DhtHealthCheckSha256, sourceUrl: DhtHealthCheckDataUri),

                TemplateArtifact.Artifact("dht-notify-ready", "DHT ready callback script",
                    ArtifactType.Script,
                    sha256: DhtNotifyReadySha256, sourceUrl: DhtNotifyReadyDataUri),

                TemplateArtifact.Artifact("dht-bootstrap-poll", "DHT bootstrap peer polling script",
                    ArtifactType.Script,
                    sha256: DhtBootstrapPollSha256, sourceUrl: DhtBootstrapPollDataUri),

                // ── Dashboard (data: URI inline) ──────────────────────────────
                TemplateArtifact.Artifact("dht-dashboard", "DHT dashboard server (Python)",
                    ArtifactType.WebAsset,
                    sha256: DhtDashboardPySha256, sourceUrl: DhtDashboardPyDataUri),

                TemplateArtifact.Artifact("dht-dashboard-html", "DHT dashboard HTML",
                    ArtifactType.WebAsset,
                    sha256: DhtDashboardHtmlSha256, sourceUrl: DhtDashboardHtmlDataUri),

                TemplateArtifact.Artifact("dht-dashboard-css", "DHT dashboard CSS",
                    ArtifactType.WebAsset,
                    sha256: DhtDashboardCssSha256, sourceUrl: DhtDashboardCssDataUri),

                TemplateArtifact.Artifact("dht-dashboard-js", "DHT dashboard JS",
                    ArtifactType.WebAsset,
                    sha256: DhtDashboardJsSha256, sourceUrl: DhtDashboardJsDataUri),

                // ── Shared watcher artifacts ─────────────────────────────────────
                TemplateArtifact.Artifact("decloud-env-watcher", "In-VM environment watcher script",
                    ArtifactType.Script,
                    sha256: DecloudEnvWatcherSha256, sourceUrl: DecloudEnvWatcherDataUri),
                TemplateArtifact.Artifact("decloud-env-watcher-service", "Environment watcher systemd service template",
                    ArtifactType.Config,
                    sha256: DecloudEnvWatcherServiceSha256, sourceUrl: DecloudEnvWatcherServiceDataUri),
                TemplateArtifact.Artifact("decloud-env-watcher-timer", "Environment watcher systemd timer template",
                    ArtifactType.Config,
                    sha256: DecloudEnvWatcherTimerSha256, sourceUrl: DecloudEnvWatcherTimerDataUri),
            },
            ExposedPorts = new List<TemplatePort>
            {
                new() { Port = 5080, Protocol = "http", Description = "DHT API",
                    ReadinessCheck = new ServiceCheck { Strategy = CheckStrategy.HttpGet, HttpPath = "/health", LivenessCheck = true, TimeoutSeconds = 300 } },
                new() { Port = 5080, Protocol = "http", Description = "DHT mesh health",
                    ReadinessCheck = new ServiceCheck { Strategy = CheckStrategy.HttpGet, HttpPath = "/health/mesh", TimeoutSeconds = 600 } },
                new() { Port = 4001, Protocol = "tcp", Description = "DHT libp2p", IsPublic = true },
            },
            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 1,
                MemoryBytes = 512L * 1024 * 1024,
                DiskBytes = 2L * 1024 * 1024 * 1024,
                QualityTier = QualityTier.Burstable,
                ComputePointCost = 1,
                ImageId = DhtVmSpec.Standard.ImageId,
            },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        }

    private async Task<VmTemplate> BuildBlockstoreTemplateAsync(BinaryManifest binaries, CancellationToken ct = default)
    {
        var amd64 = binaries.GetArtifact("blockstore-node-amd64");
        var arm64 = binaries.GetArtifact("blockstore-node-arm64");

        return new VmTemplate
        {
            Slug = "system-blockstore",
            Name = "Block Store Node",
            Description = "libp2p BitSwap block store for content-addressed storage and data replication.",
            Version = "1.0.0",
            // Revision is derived from content hash in SeedTemplateAsync.
            Category = "infrastructure",
            AuthorId = "system",
            IsCommunity = false,
            IsVerified = true,
            Status = TemplateStatus.Published,
            Visibility = TemplateVisibility.Public,
            PricingModel = TemplatePricingModel.Free,
            CloudInitTemplate = await FetchCloudInitAsync("blockstore", ct),
            Variables = BuildBlockStoreVariables(),
            Artifacts = new List<TemplateArtifact>
            {
                // ── Binary (HTTPS — DeCloud.Builds release) ──────────────────
                TemplateArtifact.Artifact("blockstore-node", "BlockStore node binary",
                    ArtifactType.Binary, arch: "amd64",
                    sha256: amd64.Sha256, sizeBytes: amd64.SizeBytes,
                    sourceUrl: amd64.Url),

                TemplateArtifact.Artifact("blockstore-node", "BlockStore node binary — ARM64",
                    ArtifactType.Binary, arch: "arm64",
                    sha256: arm64.Sha256, sizeBytes: arm64.SizeBytes,
                    sourceUrl: arm64.Url),

                // ── Shared scripts (data: URI inline) ────────────────────────
                TemplateArtifact.Artifact("wg-mesh-enroll", "WireGuard mesh enrollment script",
                    ArtifactType.Script,
                    sha256: WgMeshEnrollSha256, sourceUrl: WgMeshEnrollDataUri),

                TemplateArtifact.Artifact("wg-config-fetch", "WireGuard config fetch script",
                    ArtifactType.Script,
                    sha256: WgConfigFetchSha256, sourceUrl: WgConfigFetchDataUri),

                // ── BlockStore-specific scripts (data: URI inline) ───────────────────
                TemplateArtifact.Artifact("blockstore-health-check", "BlockStore health check script",
                    ArtifactType.Script,
                    sha256: BlockstoreHealthCheckSha256, sourceUrl: BlockstoreHealthCheckDataUri),

                TemplateArtifact.Artifact("blockstore-notify-ready", "BlockStore ready callback script",
                    ArtifactType.Script,
                    sha256: BlockstoreNotifyReadySha256, sourceUrl: BlockstoreNotifyReadyDataUri),

                TemplateArtifact.Artifact("blockstore-bootstrap-poll", "BlockStore bootstrap peer polling script",
                    ArtifactType.Script,
                    sha256: BlockstoreBootstrapPollSha256, sourceUrl: BlockstoreBootstrapPollDataUri),

                // ── Dashboard (data: URI inline) ──────────────────────────────
                TemplateArtifact.Artifact("blockstore-dashboard", "BlockStore dashboard server (Python)",
                    ArtifactType.WebAsset,
                    sha256: BlockstoreDashboardPySha256, sourceUrl: BlockstoreDashboardPyDataUri),

                TemplateArtifact.Artifact("blockstore-dashboard-html", "BlockStore dashboard HTML",
                    ArtifactType.WebAsset,
                    sha256: BlockstoreDashboardHtmlSha256, sourceUrl: BlockstoreDashboardHtmlDataUri),

                TemplateArtifact.Artifact("blockstore-dashboard-css", "BlockStore dashboard CSS",
                    ArtifactType.WebAsset,
                    sha256: BlockstoreDashboardCssSha256, sourceUrl: BlockstoreDashboardCssDataUri),

                TemplateArtifact.Artifact("blockstore-dashboard-js", "BlockStore dashboard JS",
                    ArtifactType.WebAsset,
                    sha256: BlockstoreDashboardJsSha256, sourceUrl: BlockstoreDashboardJsDataUri),

                // ── Shared watcher artifacts ─────────────────────────────────────
                TemplateArtifact.Artifact("decloud-env-watcher", "In-VM environment watcher script",
                    ArtifactType.Script,
                    sha256: DecloudEnvWatcherSha256, sourceUrl: DecloudEnvWatcherDataUri),
                TemplateArtifact.Artifact("decloud-env-watcher-service", "Environment watcher systemd service template",
                    ArtifactType.Config,
                    sha256: DecloudEnvWatcherServiceSha256, sourceUrl: DecloudEnvWatcherServiceDataUri),
                TemplateArtifact.Artifact("decloud-env-watcher-timer", "Environment watcher systemd timer template",
                    ArtifactType.Config,
                    sha256: DecloudEnvWatcherTimerSha256, sourceUrl: DecloudEnvWatcherTimerDataUri),
            },

            ExposedPorts = new List<TemplatePort>
            {
                new() { Port = 5090,
                    Protocol = "http",
                    Description = "BlockStore API",
                    ReadinessCheck = new ServiceCheck {
                        Strategy = CheckStrategy.HttpGet,
                        HttpPath = "/health",
                        LivenessCheck = true,
                        TimeoutSeconds = 300 } },
                new() { Port = 5090,
                    Protocol = "http",
                    Description = "BlockStore mesh health",
                    ReadinessCheck = new ServiceCheck {
                        Strategy = CheckStrategy.HttpGet,
                        HttpPath = "/health/mesh",
                        TimeoutSeconds = 600 } },
                new() { Port = 5001,
                    Protocol = "tcp",
                    Description = "BlockStore bitswap",
                    IsPublic = true },
            },

            RecommendedSpec = new VmSpec
            {
                VirtualCpuCores = 1,
                MemoryBytes = 512L * 1024 * 1024,
                DiskBytes = 10L * 1024 * 1024 * 1024,
                QualityTier = QualityTier.Burstable,
                ComputePointCost = 1,
                ImageId = BlockStoreVmSpec.Create(0).ImageId,
            },

            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private async Task<VmTemplate> BuildRelayTemplateAsync(CancellationToken ct = default) => new()
    {
        Slug = "system-relay",
        Name = "Relay Node",
        Description = "WireGuard relay for CGNAT traversal and peer-to-peer overlay routing.",
        Version = "1.0.0",
        // Revision is derived from content hash in SeedTemplateAsync.
        Category = "infrastructure",
        AuthorId = "system",
        IsCommunity = false,
        IsVerified = true,
        Status = TemplateStatus.Published,
        Visibility = TemplateVisibility.Public,
        PricingModel = TemplatePricingModel.Free,
        CloudInitTemplate = await FetchCloudInitAsync("relay", ct),
        // Full static set for the v6.1 composed relay cloud-init (base-system + relay role).
        // Relay has no dynamics in Phase 3 — RELAY_REGION and RELAY_CAPACITY are Static
        // (baked into relay-metadata.json at render time; relay-api.py reads from that file).
        // See design §2.3 ("nearly all Static") and P3.1.4 note ("keep that pattern").
        // RelayWireGuardPrivateKeyResolver intentionally absent — __WIREGUARD_PRIVATE_KEY__
        // was eliminated from the relay cloud-init in P0.9 (§2 decision log, 2026-05-03/P0.9).
        Variables = BuildRelayVariables(),
        Artifacts = new List<TemplateArtifact>
        {
            // Relay has no compiled binary — WireGuard is a kernel module.
            TemplateArtifact.Artifact("relay-api", "Relay API server (Python)",
                ArtifactType.Script,
                sha256: RelayApiPySha256, sourceUrl: RelayApiPyDataUri),

            TemplateArtifact.Artifact("relay-http-proxy", "Relay HTTP proxy (Python)",
                ArtifactType.Script,
                sha256: RelayHttpProxyPySha256, sourceUrl: RelayHttpProxyPyDataUri),

            TemplateArtifact.Artifact("notify-nat-ready", "NAT ready callback script",
                ArtifactType.Script,
                sha256: RelayNotifyNatReadySha256, sourceUrl: RelayNotifyNatReadyDataUri),

            TemplateArtifact.Artifact("relay-dashboard-html", "Relay dashboard HTML",
                ArtifactType.WebAsset,
                sha256: RelayDashboardHtmlSha256, sourceUrl: RelayDashboardHtmlDataUri),

            TemplateArtifact.Artifact("relay-dashboard-css", "Relay dashboard CSS",
                ArtifactType.WebAsset,
                sha256: RelayDashboardCssSha256, sourceUrl: RelayDashboardCssDataUri),

            TemplateArtifact.Artifact("relay-dashboard-js", "Relay dashboard JS",
            ArtifactType.WebAsset,
            sha256: RelayDashboardJsSha256, sourceUrl: RelayDashboardJsDataUri),

            // ── Shared watcher artifacts (P3.1.4) ────────────────────────────────
            TemplateArtifact.Artifact("decloud-env-watcher", "In-VM environment watcher script",
                ArtifactType.Script,
                sha256: DecloudEnvWatcherSha256, sourceUrl: DecloudEnvWatcherDataUri),

            TemplateArtifact.Artifact("decloud-env-watcher-service", "Environment watcher systemd service template",
                ArtifactType.Config,
                sha256: DecloudEnvWatcherServiceSha256, sourceUrl: DecloudEnvWatcherServiceDataUri),

            TemplateArtifact.Artifact("decloud-env-watcher-timer", "Environment watcher systemd timer template",
                ArtifactType.Config,
                sha256: DecloudEnvWatcherTimerSha256, sourceUrl: DecloudEnvWatcherTimerDataUri),
        },

        ExposedPorts = new List<TemplatePort>
        {
            new() { Port = 8080,
                Protocol = "http",
                Description = "Relay API",
                ReadinessCheck = new ServiceCheck {
                    Strategy = CheckStrategy.HttpGet,
                    HttpPath = "/health",
                    TimeoutSeconds = 300 } },
            new() { Port = 51820,
                Protocol = "udp",
                Description = "WireGuard",
                IsPublic = true },
        },

        RecommendedSpec = new VmSpec
        {
            VirtualCpuCores = 1,
            MemoryBytes = 512L * 1024 * 1024,
            DiskBytes = 2L * 1024 * 1024 * 1024,
            QualityTier = QualityTier.Burstable,
            ComputePointCost = 1,
            ImageId = RelayVmSpec.Basic.ImageId,
        },

        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── Seed helper ───────────────────────────────────────────────────────

    private async Task SeedTemplateAsync(VmTemplate template, CancellationToken ct)
    {
        var existing = await _dataStore.GetTemplateBySlugAsync(template.Slug);
        var newHash = ComputeContentHash(template);

        if (existing != null)
        {
            var existingHash = ComputeContentHash(existing);
            if (newHash == existingHash)
            {
                _logger.LogDebug(
                    "Template '{Slug}' content unchanged (hash {Hash}) — skipping",
                    template.Slug, newHash[..12]);
                return;
            }

            // Content changed. New revision must be strictly greater than existing.
            // Use Unix seconds — always increasing, always greater than any
            // previous manually-assigned revision (which topped out in the hundreds).
            var nextRevision = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (nextRevision <= existing.Revision)
                nextRevision = existing.Revision + 1; // safety: clock skew guard

            template.Revision = nextRevision;
            template.Id = existing.Id; // preserve MongoDB _id across updates

            _logger.LogInformation(
                "Template '{Slug}' content changed — updating r{OldRev} → r{NewRev} " +
                "(hash {OldHash} → {NewHash})",
                template.Slug, existing.Revision, template.Revision,
                ComputeContentHash(existing)[..12], newHash[..12]);
        }
        else
        {
            // First seed. Use Unix seconds as the initial revision.
            template.Revision = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            _logger.LogInformation(
                "Seeding new template '{Slug}' r{Rev} (hash {Hash})",
                template.Slug, template.Revision, newHash[..12]);
        }

        await _dataStore.SaveTemplateAsync(template);
    }

    /// <summary>
    /// SHA256 of all deployment-affecting fields. Any change to cloud-init,
    /// variables, artifacts, version, or resource spec produces a different
    /// hash, which triggers a revision bump in SeedTemplateAsync.
    ///
    /// Fields excluded: Revision (derived), Id (MongoDB), timestamps,
    /// display metadata (Name, Description, Category, ratings).
    /// </summary>
    private static string ComputeContentHash(VmTemplate template)
    {
        var content = new
        {
            template.Version,
            template.CloudInitTemplate,
            Variables = template.Variables
                .OrderBy(v => v.Name)
                .Select(v => new { v.Name, v.DefaultValue, v.Scope, v.Kind }),
            Artifacts = template.Artifacts
                .OrderBy(a => a.Name).ThenBy(a => a.Architecture)
                .Select(a => new { a.Name, a.Sha256, a.SourceUrl, a.SizeBytes, a.Type, a.Architecture }),
            template.DefaultEnvironmentVariables,
            // Resource spec included so changes to QualityTier or ComputePointCost
            // trigger a revision bump and node-side redeployment.
            // Resource specs included so changes to either trigger a revision
            // bump and node-side redeployment.
            MinimumSpec = template.MinimumSpec == null ? null : new
            {
                template.MinimumSpec.VirtualCpuCores,
                template.MinimumSpec.MemoryBytes,
                template.MinimumSpec.DiskBytes,
                template.MinimumSpec.QualityTier,
                template.MinimumSpec.ComputePointCost,
            },
            RecommendedSpec = template.RecommendedSpec == null ? null : new
            {
                template.RecommendedSpec.VirtualCpuCores,
                template.RecommendedSpec.MemoryBytes,
                template.RecommendedSpec.DiskBytes,
                template.RecommendedSpec.QualityTier,
                template.RecommendedSpec.ComputePointCost,
            },
        };

        var json = JsonSerializer.Serialize(content,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Maps a system VM role to its base layer URL.
    /// Relay composes against base-system.yaml because it is the mesh hub for its
    /// subnet, not a peer in the inter-system-VM mesh — it runs
    /// wg-quick@wg-relay-server, not wg-quick@wg-mesh, and does not need the
    /// mesh watchdog units in base-system-mesh.yaml.
    /// DHT and BlockStore are mesh participants and compose against base-system-mesh.yaml.
    /// </summary>
    private static string BaseUrlFor(string role) => role switch
    {
        "relay" => BaseSystemUrl,
        "dht" => BaseSystemMeshUrl,
        "blockstore" => BaseSystemMeshUrl,
        _ => throw new InvalidOperationException(
            $"SystemVmTemplateSeeder: unknown system VM role '{role}' — " +
            "no base-layer mapping. Add to BaseUrlFor() if a new role is introduced."),
    };

    /// <summary>
    /// Fetch a URL as text, with debug logging. Mirrors TenantVmTemplateSeeder.FetchAsync.
    /// </summary>
    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        _logger.LogDebug("Fetching {Url}", url);
        return await _httpClient.GetStringAsync(url, ct);
    }

    /// <summary>
    /// Fetch the role layer AND its base layer from DeCloud.Builds, then compose them
    /// into a single cloud-init document via TemplateComposer.
    ///
    /// <para>
    /// This is the system-VM equivalent of TenantVmTemplateSeeder.BuildGeneralTemplateAsync's
    /// fetch+compose step. P0.3a–c stripped base content from the role YAMLs on the assumption
    /// composition would happen at seed time; this method is what makes that assumption true.
    /// </para>
    /// </summary>
    private async Task<string> FetchCloudInitAsync(string role, CancellationToken ct)
    {
        var baseUrl = BaseUrlFor(role);
        var roleUrl = $"{CloudInitRawBase}/{role}/cloud-init.yaml";

        var baseLayer = await FetchAsync(baseUrl, ct);
        var roleLayer = await FetchAsync(roleUrl, ct);

        // Display names for the composer's generated header — useful when debugging
        // composed output ("which base did this come from?").
        var baseName = baseUrl.EndsWith("base-system-mesh.yaml")
            ? "base-system-mesh.yaml"
            : "base-system.yaml";
        var roleName = $"{role}/cloud-init.yaml";

        return TemplateComposer.Compose(baseLayer, roleLayer, baseName, roleName);
    }

    /// <summary>
    /// F4: declared statics for mesh-participant system VMs (DHT, BlockStore).
    /// These are the placeholders in <c>base-system-mesh.yaml</c>'s
    /// <c>/etc/decloud/wg-mesh.env</c> that <see cref="LibvirtVmManager"/>'s
    /// per-VmType branches do NOT substitute on the node side. The renderer
    /// fills them at orchestrator render time (<see cref="WgDescriptionResolver"/>,
    /// <see cref="DeCloudRoleResolver"/>).
    ///
    /// <para>
    /// Other placeholders in the composed template (<c>__VM_ID__</c>,
    /// <c>__NODE_ID__</c>, <c>__WG_RELAY_*__</c>, etc.) remain undeclared
    /// here — they're substituted by the legacy node-side path until full
    /// Phase 3 cutover. The renderer is called with
    /// <c>strictValidation: false</c> so the validator doesn't reject these
    /// undeclared placeholders.
    /// </para>
    ///
    /// <para>
    /// Relay is not mesh-participant (composes against base-system.yaml, not
    /// base-system-mesh.yaml) — does not use this list.
    /// </para>
    /// </summary>
    private static List<TemplateVariable> BuildMeshSystemVmVariables() => new()
    {
        new TemplateVariable
        {
            Name = "WG_DESCRIPTION",
            Kind = VariableKind.Static,
            // ResolverKey omitted — defaults to Name, matching WgDescriptionResolver.ResolverKey
        },
        new TemplateVariable
        {
            Name = "DECLOUD_ROLE",
            Kind = VariableKind.Static,
            // ResolverKey omitted — defaults to Name, matching DeCloudRoleResolver.ResolverKey
        },
    };

    /// <summary>
    /// Full declared variable list for the DHT cloud-init template.
    /// Every __VARNAME__ placeholder in the composed cloud-init must appear here
    /// as Static, and every $VARNAME shell reference as Dynamic.
    ///
    /// Phase 3.2 note: the 7 dynamics currently appear as __VARNAME__ placeholders
    /// in the live cloud-init (LibvirtVmManager STEP 5.6 substitutes them on the
    /// node side). P3.2.4 switches them to $VARNAME shell references and removes
    /// the legacy substitution. Until then, strictValidation: false prevents the
    /// renderer from rejecting undeclared placeholders.
    ///
    /// DHT_LISTEN_PORT and DHT_API_PORT are declared here even though the current
    /// cloud-init has them as hardcoded literals (4001, 5080). P3.2.4 introduces
    /// __DHT_LISTEN_PORT__ and __DHT_API_PORT__ placeholders; declaring them here
    /// first means P3.2.4's YAML change requires no seeder amendment.
    /// </summary>
    private static List<TemplateVariable> BuildDhtVariables() => new()
    {
        // ── Identity / VM context (platform-common resolvers) ────────────────
        new() { Name = "VM_ID",       Kind = VariableKind.Static, Required = true,
                Description = "VM unique identifier." },
        new() { Name = "VM_NAME",     Kind = VariableKind.Static, Required = true,
                Description = "VM display name (used in dht-metadata.json and final_message)." },
        new() { Name = "HOSTNAME",    Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname (base-system-mesh.yaml scalar)." },
        new() { Name = "NODE_ID",     Kind = VariableKind.Static, Required = true,
                Description = "Node unique identifier (DECLOUD_NODE_ID, WG_PARENT_NODE_ID, node_id in metadata)." },
        new() { Name = "ORCHESTRATOR_URL", Kind = VariableKind.Static, Required = true,
                Description = "Orchestrator HTTP URL for dht.env ORCHESTRATOR_URL." },
        new() { Name = "HOST_MACHINE_ID",  Kind = VariableKind.Static, Required = true,
                Description = "Host machine UUID for dht.env HOST_MACHINE_ID." },
        new() { Name = "TIMESTAMP",   Kind = VariableKind.Static, Required = false,
                DefaultValue = "",
                Description = "UTC ISO8601 render timestamp for dht-metadata.json created_at." },

        // ── SSH / CA (platform-common, base-system-mesh.yaml) ────────────────
        new() { Name = "CA_PUBLIC_KEY", Kind = VariableKind.Static, Required = true,
                Description = "SSH certificate authority public key (resolved from Node.SshCaPublicKey)." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No SSH keys provided",
                Description = "YAML chunk for cloud-init ssh_authorized_keys." },
        new() { Name = "PASSWORD_CONFIG_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No password authentication",
                Description = "YAML chunk for cloud-init chpasswd.users." },
        new() { Name = "SSH_PASSWORD_AUTH", Kind = VariableKind.Static,
                DefaultValue = "false",
                Description = "'true' or 'false' for cloud-init ssh_pwauth." },
        new() { Name = "ADMIN_PASSWORD", Kind = VariableKind.Static,
                DefaultValue = "",
                Description = "Plaintext root password. Empty for SSH-only deploys (system VMs default)." },

        // ── DHT-specific statics ─────────────────────────────────────────────
        new() { Name = "WG_DESCRIPTION", Kind = VariableKind.Static, Required = true,
                Description = "WireGuard peer label for this VM (dht-<obligation-id>). " +
                              "Resolved by WgDescriptionResolver." },
        new() { Name = "DECLOUD_ROLE", Kind = VariableKind.Static,
                DefaultValue = "dht",
                Description = "Role name injected into wg-mesh.env WG_ROLE and wg-config-fetch.sh DECLOUD_ROLE." },
        new() { Name = "DHT_LISTEN_PORT", Kind = VariableKind.Static,
                DefaultValue = "4001",
                Description = "libp2p listen port. Baked into dht.env. " +
                              "Currently a literal in cloud-init; __DHT_LISTEN_PORT__ placeholder lands in P3.2.4." },
        new() { Name = "DHT_API_PORT", Kind = VariableKind.Static,
                DefaultValue = "5080",
                Description = "DHT HTTP API port. Baked into dht.env. " +
                              "Currently a literal in cloud-init; __DHT_API_PORT__ placeholder lands in P3.2.4." },

        // ── Watcher infra ────────────────────────────────────────────────────
        new() { Name = "VARIABLE_SCOPES_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No dynamic variables declared",
                Description = "Rendered scope policy file for decloud-env-watcher. " +
                              "Contains VARNAME=scope lines for each Dynamic variable. " +
                              "Placeholder __VARIABLE_SCOPES_BLOCK__ lands in P3.2.4." },

        // ── Mesh statics (filled at boot by wg-config-fetch.sh) ──────────────
        // Originally classified Dynamic in design §2.3 but the existing
        // wg-mesh.env path (wg-config-fetch.sh polls /api/obligations/{role}/
        // wg-config and overwrites the file at boot) supersedes any
        // orchestrator-render value. Declared Static with empty default so
        // the renderer substitutes placeholders cleanly; the running binary
        // observes the real values via the post-render boot-time fill.
        // Future P4 work may unify through the watcher; not a Phase 3 blocker
        // (relay reassignment without redeploy is not a working feature today).
        new() { Name = "WG_RELAY_ENDPOINT", Kind = VariableKind.Static,
                DefaultValue = "",
                Description = "WireGuard relay UDP endpoint (host:port). Filled at boot by wg-config-fetch.sh." },
        new() { Name = "WG_RELAY_PUBKEY", Kind = VariableKind.Static,
                DefaultValue = "",
                Description = "WireGuard relay public key. Filled at boot by wg-config-fetch.sh." },
        new() { Name = "WG_RELAY_API", Kind = VariableKind.Static,
                DefaultValue = "",
                Description = "Relay HTTP API base URL. Filled at boot by wg-config-fetch.sh." },
        new() { Name = "WG_TUNNEL_IP", Kind = VariableKind.Static,
                DefaultValue = "",
                Description = "WireGuard tunnel IP for this VM. Filled at boot by wg-config-fetch.sh." },

        // ── DHT_REGION — demoted from Dynamic per scope audit (P3.2.6 finding) ──
        // Cosmetic; consumed only by dht-metadata.json written once at boot.
        // No mechanism in the binary observes runtime changes. Resolver reads
        // ctx.Node.Region.
        new() { Name = "DHT_REGION", Kind = VariableKind.Static, Required = false,
                DefaultValue = "default",
                Description = "Node region label. Baked once into dht-metadata.json at deploy time. " +
                              "Resolver: DhtRegionResolver reads ctx.Node.Region." },

        // ── True Dynamics (2) — managed via the new env-watcher pipeline ─────
        new() { Name = "DHT_ADVERTISE_IP",
                Kind = VariableKind.Dynamic, Scope = WatcherScope.Restart,
                Description = "libp2p advertise IP (WireGuard tunnel IP bare address). " +
                              "Restart: libp2p multiaddr is bound at startup; can't rebind without restart." },

        new() { Name = "DHT_BOOTSTRAP_PEERS",
                Kind = VariableKind.Dynamic, Scope = WatcherScope.Noop,
                Description = "Comma-separated bootstrap peer multiaddrs. " +
                              "Noop: dht-bootstrap-poll.sh discovers peers via /api/dht/join " +
                              "regardless of env value; the env var is currently advisory." },
    };

    /// <summary>
    /// Variables for the BlockStore system VM template — mirror of
    /// <see cref="BuildDhtVariables"/> with role-specific names substituted.
    ///
    /// <para>
    /// Composition (24 entries):
    /// </para>
    /// <list type="bullet">
    ///   <item><description>12 platform-common statics (VM identity, SSH/password machinery,
    ///                       orchestrator URL, timestamp) — covered by P1.6 platform-common
    ///                       resolvers.</description></item>
    ///   <item><description>5 BlockStore-specific statics (mesh description, role label,
    ///                       listen/API ports, variable-scopes block) — covered by
    ///                       <c>WgDescriptionResolver</c>, <c>DeCloudRoleResolver</c>,
    ///                       <c>DefaultValue</c> for ports, <c>VariableScopesBlockResolver</c>.
    ///                       </description></item>
    ///   <item><description>4 WG_* statics with <c>DefaultValue=""</c> — runtime-managed by
    ///                       <c>wg-config-fetch.sh</c> at boot, not by the watcher pipeline.
    ///                       See P3.2.4 decision for full rationale.</description></item>
    ///   <item><description>1 region static (<c>BLOCKSTORE_REGION</c>) with
    ///                       <c>DefaultValue="default"</c> — baked into
    ///                       <c>blockstore-metadata.json</c> at deploy time.</description></item>
    ///   <item><description>2 true dynamics (<c>BLOCKSTORE_ADVERTISE_IP</c> Restart,
    ///                       <c>BLOCKSTORE_BOOTSTRAP_PEERS</c> Noop) — managed via the
    ///                       env-watcher pipeline; node-side resolved by inline switch
    ///                       in <c>ObligationEnvironmentController</c> (P3.3.3).</description></item>
    /// </list>
    /// </summary>
    private static List<TemplateVariable> BuildBlockStoreVariables() => new()
    {
        // ── Platform-common statics (12) ─────────────────────────────────────
        // Identity, SSH, password, orchestrator URL, timestamp.
        // All covered by P1.6 platform-common resolvers.
        new() { Name = "VM_ID",                      Kind = VariableKind.Static, Required = true,
                Description = "Stable VM UUID assigned by the orchestrator at obligation creation." },
        new() { Name = "VM_NAME",                    Kind = VariableKind.Static, Required = true,
                Description = "Human-readable VM name (e.g. blockstore-3b983464)." },
        new() { Name = "HOSTNAME",                   Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname (typically equal to VM_NAME)." },
        new() { Name = "NODE_ID",                    Kind = VariableKind.Static, Required = true,
                Description = "Stable node UUID hosting this VM." },
        new() { Name = "ORCHESTRATOR_URL",           Kind = VariableKind.Static, Required = true,
                Description = "Public orchestrator base URL (e.g. https://decloud.stackfi.tech)." },
        new() { Name = "HOST_MACHINE_ID",            Kind = VariableKind.Static, Required = true,
                Description = "Linux /etc/machine-id of the host node — used for attestation." },
        new() { Name = "TIMESTAMP",                  Kind = VariableKind.Static,
                Description = "Render timestamp (ISO-8601 UTC). Useful for cache-busting." },
        new() { Name = "CA_PUBLIC_KEY",              Kind = VariableKind.Static, Required = true,
                Description = "Platform SSH CA public key, written into sshd config." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK",  Kind = VariableKind.Static,
                Description = "Optional users:authorized_keys YAML block (empty if not configured)." },
        new() { Name = "PASSWORD_CONFIG_BLOCK",      Kind = VariableKind.Static,
                Description = "Optional users:passwd YAML block (empty if password auth disabled)." },
        new() { Name = "SSH_PASSWORD_AUTH",          Kind = VariableKind.Static, DefaultValue = "no",
                Description = "sshd PasswordAuthentication directive value (yes/no)." },
        new() { Name = "ADMIN_PASSWORD",             Kind = VariableKind.Static,
                Description = "Optional admin password (empty when password auth disabled)." },

        // ── BlockStore-specific statics (5) ──────────────────────────────────
        new() { Name = "WG_DESCRIPTION",             Kind = VariableKind.Static,
                Description = "WireGuard interface description for this VM (cosmetic — appears in wg-mesh.conf)." },
        new() { Name = "DECLOUD_ROLE",               Kind = VariableKind.Static, DefaultValue = "blockstore",
                Description = "Canonical role name. Used in log prefixes and labels." },
        new() { Name = "BLOCKSTORE_LISTEN_PORT",     Kind = VariableKind.Static, DefaultValue = "5001",
                Description = "libp2p listen port for BitSwap. Static — port change requires restart " +
                              "but is a deploy-time decision, not a runtime concern." },
        new() { Name = "BLOCKSTORE_API_PORT",        Kind = VariableKind.Static, DefaultValue = "5090",
                Description = "BlockStore HTTP API port (consumed by nginx proxy and dashboard)." },
        new() { Name = "VARIABLE_SCOPES_BLOCK",      Kind = VariableKind.Static,
                Description = "Rendered scope policy lines for /etc/decloud-blockstore/variable-scopes.conf — " +
                              "produced by VariableScopesBlockResolver iterating template.Variables for Dynamics." },

        // ── Mesh statics (4 — filled at boot by wg-config-fetch.sh) ──────────
        // Originally classified Dynamic in design §2.3 but the existing
        // wg-mesh.env path (wg-config-fetch.sh polls /api/obligations/{role}/
        // wg-config and overwrites the file at boot) supersedes any
        // orchestrator-render value. Declared Static with empty default so the
        // renderer substitutes placeholders cleanly; the running binary
        // observes the real values via the post-render boot-time fill.
        // Future P4 work may unify through the watcher; not a Phase 3 blocker.
        // See P3.2.4 / DHT for full rationale (same conclusion applies here).
        new() { Name = "WG_RELAY_ENDPOINT", Kind = VariableKind.Static, DefaultValue = "",
                Description = "WireGuard relay UDP endpoint. Filled at boot by wg-config-fetch.sh." },
        new() { Name = "WG_RELAY_PUBKEY",   Kind = VariableKind.Static, DefaultValue = "",
                Description = "WireGuard relay public key. Filled at boot by wg-config-fetch.sh." },
        new() { Name = "WG_RELAY_API",      Kind = VariableKind.Static, DefaultValue = "",
                Description = "Relay HTTP API base URL. Filled at boot by wg-config-fetch.sh." },
        new() { Name = "WG_TUNNEL_IP",      Kind = VariableKind.Static, DefaultValue = "",
                Description = "WireGuard tunnel IP for this VM. Filled at boot by wg-config-fetch.sh." },

        // ── BLOCKSTORE_REGION — Static per P3.2.6 scope audit logic ──────────
        // Cosmetic; consumed only by blockstore-metadata.json written once at
        // boot. No mechanism in the binary observes runtime changes. Resolver
        // reads ctx.Node.Region.
        new() { Name = "BLOCKSTORE_REGION", Kind = VariableKind.Static, Required = false,
                DefaultValue = "default",
                Description = "Node region label. Baked once into blockstore-metadata.json at deploy time. " +
                              "Resolver: BlockStoreRegionResolver reads ctx.Node.Region." },

        // ── True Dynamics (2) — managed via the new env-watcher pipeline ─────
        new() { Name = "BLOCKSTORE_ADVERTISE_IP",
                Kind = VariableKind.Dynamic, Scope = WatcherScope.Restart,
                Description = "libp2p advertise IP (WireGuard tunnel IP bare address). " +
                              "Restart: libp2p multiaddr is bound at startup; can't rebind without restart." },

        new() { Name = "BLOCKSTORE_BOOTSTRAP_PEERS",
                Kind = VariableKind.Dynamic, Scope = WatcherScope.Noop,
                Description = "Comma-separated bootstrap peer multiaddrs. " +
                              "Noop: BlockStore discovers peers via /api/blockstore/announce regardless of " +
                              "env value; the env var is currently advisory." },
    };

    /// <summary>
    /// Full declared-statics list for the v6.1 relay cloud-init template.
    /// Every <c>__VARNAME__</c> placeholder in the composed cloud-init must appear here.
    /// Relay has no dynamics in Phase 3 — RELAY_REGION and RELAY_CAPACITY remain Static
    /// (substituted at render time into relay-metadata.json; relay-api.py reads that file).
    /// </summary>
    private static List<TemplateVariable> BuildRelayVariables() => new()
    {
        // ── Identity / VM context (platform-common resolvers) ────────────────────
        new() { Name = "VM_ID",       Kind = VariableKind.Static, Required = true,
                Description = "VM unique identifier." },
        new() { Name = "VM_NAME",     Kind = VariableKind.Static, Required = true,
                Description = "VM display name / relay_name in relay-metadata.json." },
        new() { Name = "HOSTNAME",    Kind = VariableKind.Static, Required = true,
                Description = "Linux hostname (base-system.yaml scalar)." },
        new() { Name = "NODE_ID",     Kind = VariableKind.Static, Required = true,
                Description = "Node unique identifier." },
        new() { Name = "ORCHESTRATOR_URL", Kind = VariableKind.Static, Required = true,
                Description = "Orchestrator HTTP URL for relay-register-poll.sh." },
        new() { Name = "PUBLIC_IP",   Kind = VariableKind.Static, Required = true,
                Description = "Node public IP — used in wireguard_endpoint field of relay-metadata.json." },
        new() { Name = "HOST_MACHINE_ID", Kind = VariableKind.Static, Required = true,
                Description = "Host /etc/machine-id — injected into decloud-relay-nat-callback.service." },
        new() { Name = "TIMESTAMP",   Kind = VariableKind.Static, Required = false,
                DefaultValue = "",
                Description = "UTC ISO8601 deploy timestamp for relay-metadata.json." },

        // ── SSH / CA (platform-common, base-system.yaml) ─────────────────────────
        new() { Name = "CA_PUBLIC_KEY",            Kind = VariableKind.Static, Required = true,
                Description = "SSH certificate authority public key." },
        new() { Name = "SSH_AUTHORIZED_KEYS_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No SSH keys provided",
                Description = "YAML chunk for cloud-init ssh_authorized_keys." },
        new() { Name = "PASSWORD_CONFIG_BLOCK",     Kind = VariableKind.Static,
                DefaultValue = "# No password authentication",
                Description = "YAML chunk for cloud-init chpasswd.users." },
        new() { Name = "SSH_PASSWORD_AUTH",         Kind = VariableKind.Static,
                DefaultValue = "false",
                Description = "'true' or 'false' for cloud-init ssh_pwauth." },
        new() { Name = "ADMIN_PASSWORD",            Kind = VariableKind.Static,
                DefaultValue = "",
                Description = "Plaintext root password. Empty for SSH-only deploys." },

        // ── WireGuard identity (WgPublicKeyResolver — existing) ──────────────────
        new() { Name = "WIREGUARD_PUBLIC_KEY", Kind = VariableKind.Static, Required = true,
                Description = "Relay WireGuard public key (base64). " +
                              "Documentary field in relay-metadata.json; " +
                              "runtime authoritative source is /etc/wireguard/public.key." },

        // ── Relay-specific statics (new resolvers — Files 2–5 below) ─────────────
        new() { Name = "ORCHESTRATOR_PUBLIC_KEY", Kind = VariableKind.Static, Required = true,
                Description = "Orchestrator WireGuard public key. " +
                              "Pre-configured as a [Peer] in wg-relay-server.conf " +
                              "so the orchestrator can reach the relay over WireGuard. " +
                              "Read from IConfiguration[\"WireGuard:OrchestratorPublicKey\"] " +
                              "on the orchestrator host." },
        new() { Name = "ORCHESTRATOR_IP",  Kind = VariableKind.Static, Required = true,
                Description = "Orchestrator host IP/hostname for the WireGuard [Peer] Endpoint. " +
                              "Derived from OrchestratorUrl; localhost substituted with node PublicIp." },
        new() { Name = "ORCHESTRATOR_PORT", Kind = VariableKind.Static, Required = false,
                DefaultValue = "51821",
                Description = "Orchestrator WireGuard listen port. Hardcoded 51821 unless " +
                              "overridden in IConfiguration[\"WireGuard:OrchestratorPort\"]." },
        new() { Name = "RELAY_API_TOKEN", Kind = VariableKind.Static, Required = true,
                Description = "Per-relay control-plane Bearer token. Enforced by " +
                              "relay-api.py on all mutating endpoints. Source: " +
                              "RelayObligationState.AuthToken." },
        new() { Name = "RELAY_SUBNET",    Kind = VariableKind.Static, Required = true,
                Description = "Relay subnet octet (e.g. '10' for 10.20.10.0/24). " +
                              "Extracted from RelayObligationState.RelaySubnet." },
        new() { Name = "RELAY_REGION",    Kind = VariableKind.Static, Required = false,
                DefaultValue = "default",
                Description = "Node region label. Written into relay-metadata.json for relay-api.py." },
        new() { Name = "RELAY_CAPACITY",  Kind = VariableKind.Static, Required = false,
                DefaultValue = "10",
                Description = "Max CGNAT node connections. Written into relay-metadata.json." },

        // ── Watcher infra (P3.1.4) ───────────────────────────────────────────
        new() { Name = "VARIABLE_SCOPES_BLOCK", Kind = VariableKind.Static,
                DefaultValue = "# No dynamic variables declared",
                Description = "Rendered scope policy file for decloud-env-watcher. " +
                              "Contains VARNAME=scope lines for each Dynamic variable. " +
                              "Empty comment for relay (no dynamics in Phase 3)." },
    };
}