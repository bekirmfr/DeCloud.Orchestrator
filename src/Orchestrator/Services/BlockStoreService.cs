using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Services;

// ════════════════════════════════════════════════════════════════════════════
// Support types (defined here; Phase D will persist ManifestRecord to MongoDB)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Manifest type determines block size and billing rate.
/// Block size is a protocol constant per type — enforced at write time
/// by the block store binary.
/// </summary>
public enum ManifestType
{
    /// <summary>
    /// VM overlay disk replication (lazysync).
    /// Block size: 1 MB. Optimised for sparse write patterns.
    /// </summary>
    VmOverlay = 0,

    /// <summary>
    /// Large language model weight shard for distributed inference.
    /// Block size: 64 MB. Aligned to transformer layer boundaries.
    /// Llama-3 70B Q4 ≈ 640 blocks, FP16 ≈ 2,240 blocks.
    /// </summary>
    ModelShard = 1,

    /// <summary>
    /// LoRA fine-tune adapter weights.
    /// Block size: 256 KB. Fine-grained deduplication across adapter variants.
    /// </summary>
    LoraAdapter = 2,

    /// <summary>
    /// Base OS image template (e.g., debian-12-generic).
    /// Block size: 4 MB. Clean chunk counts, good cross-image deduplication.
    /// </summary>
    ImageTemplate = 3,
}

/// <summary>
/// Protocol-level block size constants per manifest type.
/// These are immutable network constants — changing them requires a network migration.
/// The block store binary enforces these at write time.
/// </summary>
public static class BlockSizeConstants
{
    public const int VmOverlayKb = 1024;        // 1 MB
    public const int ModelShardKb = 64 * 1024;   // 64 MB
    public const int LoraAdapterKb = 256;          // 256 KB
    public const int ImageTemplateKb = 4 * 1024;    // 4 MB

    /// <summary>Returns the canonical block size in KB for a manifest type.</summary>
    public static int ForType(ManifestType type) => type switch
    {
        ManifestType.VmOverlay => VmOverlayKb,
        ManifestType.ModelShard => ModelShardKb,
        ManifestType.LoraAdapter => LoraAdapterKb,
        ManifestType.ImageTemplate => ImageTemplateKb,
        _ => VmOverlayKb,
    };
}

/// <summary>
/// Tracks the evolving overlay manifest for a VM's disk replication state.
/// Created by lazysync daemon (Phase D) and registered via POST /api/blockstore/manifest.
/// Persisted to MongoDB in Phase D (in-memory only in Phase A).
/// </summary>
[MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElements]
public class ManifestRecord
{
    [MongoDB.Bson.Serialization.Attributes.BsonId]
    [MongoDB.Bson.Serialization.Attributes.BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)]
    public string Id { get; set; } = MongoDB.Bson.ObjectId.GenerateNewId().ToString();

    public string VmId { get; set; } = string.Empty;

    /// <summary>Root CID of the manifest DAG for this version.</summary>
    public string RootCid { get; set; } = string.Empty;

    /// <summary>Monotonically increasing version counter. Incremented each lazysync cycle.</summary>
    public int Version { get; set; }

    /// <summary>CIDs of blocks that changed in this version (delta from previous).</summary>
    public List<string> ChangedBlockCids { get; set; } = [];

    /// <summary>
    /// Rolling sample of CIDs accumulated across all versions (capped at 200).
    /// Used by LazysyncManager to audit the full block set, not just the latest delta.
    /// New ChangedBlockCids are merged in on each version advance; oldest are dropped
    /// when the cap is exceeded to keep the sample representative over time.
    /// </summary>
    public List<string> CumulativeBlockCids { get; set; } = [];

    /// <summary>
    /// Total number of chunks in the current manifest (all chunks, not just changed ones).
    /// Used for exact block-count billing: cost = BlockCount × (BlockSizeKb/1024) × N × rate.
    /// Updated by LazysyncManager each audit cycle.
    /// </summary>
    public int BlockCount { get; set; }

    /// <summary>
    /// Block size in KB for this manifest type. Protocol constant per ManifestType.
    /// Set at manifest creation from BlockSizeConstants.ForType(ManifestType).
    /// Immutable after first registration.
    /// </summary>
    public int BlockSizeKb { get; set; } = BlockSizeConstants.VmOverlayKb;

    /// <summary>
    /// Manifest type — determines block size and content semantics.
    /// Defaults to VmOverlay (current primary use case).
    /// </summary>
    public ManifestType ManifestType { get; set; } = ManifestType.VmOverlay;

    /// <summary>Total overlay size in bytes at this version.</summary>
    public long TotalBytes { get; set; }

    /// <summary>When this manifest version was registered.</summary>
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Latest version where ALL chunks have ≥N providers in the DHT.
    /// Advances via the background audit loop (Phase D). Never exceeds Version.
    /// </summary>
    public int ConfirmedVersion { get; set; }

    /// <summary>Root CID of the confirmed version (used for migration/recovery).</summary>
    public string? ConfirmedRootCid { get; set; }

    /// <summary>Per-VM replication factor. 0 = ephemeral (no replication).</summary>
    public int ReplicationFactor { get; set; } = 3;
}

/// <summary>
/// Result of a replication audit for a specific VM manifest version.
/// Returned by GET /api/blockstore/audit/{vmId} (stub in Phase A).
/// </summary>
public class ReplicationAudit
{
    public string VmId { get; set; } = string.Empty;
    public int Version { get; set; }
    public int TotalChunks { get; set; }
    public int ChunksWithSufficientProviders { get; set; }
    public List<UnderReplicatedChunk> UnderReplicatedChunks { get; set; } = [];
}

public class UnderReplicatedChunk
{
    public string Cid { get; set; } = string.Empty;
    public int ProviderCount { get; set; }
}

/// <summary>
/// Migration plan for a VM: target node ranked by scheduling fit + resource headroom.
/// Block locality is not a factor — the target fetches overlay blocks via bitswap.
/// </summary>
public class MigrationPlan
{
    public string VmId { get; set; } = string.Empty;
    public string? TargetNodeId { get; set; }
    public string? ConfirmedManifestRootCid { get; set; }
    public int ConfirmedVersion { get; set; }
    public string MigrationStatus { get; set; } = "NotStarted";
    public string? Reason { get; set; }
}

/// <summary>Network-wide block store statistics.</summary>
public class BlockStoreStats
{
    public int TotalNodes { get; set; }
    public long TotalCapacityBytes { get; set; }
    public long TotalUsedBytes { get; set; }
    public int TotalBlocks { get; set; }
    public int ManifestCount { get; set; }
    public double AvgReplication { get; set; }
    public long TotalReplicatedBytes { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
// Interface
// ════════════════════════════════════════════════════════════════════════════

public interface IBlockStoreService
{
    // Deployment
    Task<string?> DeployBlockStoreVmAsync(Node node, IVmService vmService, CancellationToken ct = default);
    Task<List<string>> GetBootstrapPeersAsync(string? excludeNodeId = null);

    // CID index (secondary index — DHT is primary source of truth)
    void IndexCids(string nodeId, IEnumerable<string> cids);
    void UnindexCids(string nodeId, IEnumerable<string> cids);
    IReadOnlyCollection<string> LocateCid(string cid);

    // Manifest lifecycle (Phase D implementation; interface defined now)
    Task<ManifestRecord> RegisterManifestAsync(
        string vmId,
        string rootCid,
        int version,
        List<string> changedBlockCids,
        int blockCount,
        int blockSizeKb,
        ManifestType manifestType,
        long totalBytes,
        int replicationFactor = 3,
        CancellationToken ct = default);

    // Replication audit (Phase D implementation; interface defined now)
    Task<ReplicationAudit> AuditManifestReplicationAsync(string vmId, CancellationToken ct = default);

    // Migration support (Phase D implementation; interface defined now)
    Task<MigrationPlan> PlanMigrationAsync(string vmId, List<string> candidateNodeIds, CancellationToken ct = default);

    // Stats
    Task<BlockStoreStats> GetNetworkStatsAsync();
}

// ════════════════════════════════════════════════════════════════════════════
// Implementation
// ════════════════════════════════════════════════════════════════════════════

public class BlockStoreService : IBlockStoreService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<BlockStoreService> _logger;

    // Secondary CID index: CID → set of nodeIds claiming to have it.
    // In-memory only, not persisted. The DHT is the primary source of truth for
    // provider records. This is a fast lookup cache for /locate queries.
    // Key: CID string, InnerKey: nodeId, InnerValue: unused (ConcurrentDictionary as HashSet).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _cidIndex = new();

    // Manifest registry — Phase D: persisted to MongoDB via DataStore.
    // In-memory fallback: if MongoDB is unavailable, manifests are held in this cache.
    // LazysyncManager reads from DataStore.GetPendingAuditManifestsAsync() directly.
    private readonly ConcurrentDictionary<string, ManifestRecord> _manifestCache = new();

    private readonly IVmSchedulingService _schedulingService;

    public BlockStoreService(DataStore dataStore, IVmSchedulingService schedulingService, ILogger<BlockStoreService> logger)
    {
        _dataStore = dataStore;
        _schedulingService = schedulingService;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Deployment
    // ════════════════════════════════════════════════════════════════════════

    public async Task<string?> DeployBlockStoreVmAsync(
        Node node, IVmService vmService, CancellationToken ct = default)
    {
        try
        {
            // ================================================================
            // STEP 1: Calculate storage allocation — 5% of node total storage
            // ================================================================
            var totalStorage = node.HardwareInventory.Storage.Sum(s => s.TotalBytes);
            if (totalStorage < BlockStoreVmSpec.MinNodeStorageBytes)
            {
                _logger.LogWarning(
                    "Node {NodeId} has insufficient storage ({GB} GB < 100 GB) — skipping block store deployment",
                    node.Id, totalStorage / (1024 * 1024 * 1024));
                return null;
            }

            var vmSpec = BlockStoreVmSpec.Create(totalStorage);

            // ================================================================
            // STEP 2: Resolve advertise IP — prefer WireGuard tunnel IP
            // ================================================================
            var allNodes = await _dataStore.GetAllNodesAsync();
            var wgLabels = ResolveWireGuardLabels(node, allNodes, _logger);

            var advertiseIp = DhtNodeService.GetAdvertiseIp(node);
            if (wgLabels.TryGetValue("wg-tunnel-ip", out var wgTunnelIp))
            {
                // Strip CIDR notation: "10.20.1.202/24" → "10.20.1.202"
                advertiseIp = wgTunnelIp.Split('/')[0];
                _logger.LogInformation(
                    "Block store VM on node {NodeId} will advertise WireGuard tunnel IP {TunnelIp}",
                    node.Id, advertiseIp);
            }

            // ================================================================
            // STEP 3: Bootstrap peer — co-located DHT VM via virbr0
            // ================================================================
            // The blockstore only ever dials out to its co-located DHT VM.
            // All other connectivity (bitswap from remote peers) is inbound.
            // We use the DHT VM's virbr0 IP (192.168.122.x) rather than its
            // WireGuard IP because:
            //   - Both VMs share the same virbr0 bridge → direct, zero hops
            //   - WireGuard IP requires relay hairpin that races against enrollment
            //     and fails consistently on CGNAT nodes
            //   - virbr0 IP is assigned by libvirt at VM creation, always available
            var bootstrapPeers = new List<string>();

            if (node.DhtInfo != null &&
                !string.IsNullOrEmpty(node.DhtInfo.DhtVmId) &&
                !string.IsNullOrEmpty(node.DhtInfo.PeerId))
            {
                var dhtVm = await _dataStore.GetVmAsync(node.DhtInfo.DhtVmId);
                var dhtLocalIp = dhtVm?.NetworkConfig?.PrivateIp;

                if (!string.IsNullOrEmpty(dhtLocalIp))
                {
                    bootstrapPeers.Add($"/ip4/{dhtLocalIp}/tcp/4001/p2p/{node.DhtInfo.PeerId}");
                    _logger.LogInformation(
                        "Block store bootstrap peer: DHT VM {DhtVmId} at virbr0 {Ip}",
                        node.DhtInfo.DhtVmId[..8], dhtLocalIp);
                }
                else
                {
                    _logger.LogWarning(
                        "DHT VM {DhtVmId} has no IpAddress yet — blockstore deploys with no " +
                        "bootstrap peers. Bootstrap poll will connect via /api/blockstore/join.",
                        node.DhtInfo.DhtVmId[..8]);
                }
            }

            // ================================================================
            // STEP 4: Generate auth token for VM → orchestrator authentication
            // ================================================================
            var authToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var vmName = $"blockstore-{node.Region ?? "default"}-{node.Id[..8]}";

            // ================================================================
            // STEP 5: Build labels (orchestrator → node agent → cloud-init)
            // ================================================================
            var labels = new Dictionary<string, string>
            {
                { "role",                     "blockstore" },
                { "blockstore-listen-port",   BlockStoreVmSpec.BitswapPort.ToString() },
                { "blockstore-api-port",      BlockStoreVmSpec.ApiPort.ToString() },
                { "blockstore-storage-bytes", vmSpec.DiskBytes.ToString() },
                { "blockstore-auth-token",    authToken },
                { "blockstore-advertise-ip",  advertiseIp },
                { "blockstore-bootstrap-peers", string.Join(",", bootstrapPeers) },
                { "node-region",              node.Region ?? "default" },
                { "node-id",                  node.Id },
                { "architecture",             node.Architecture ?? "x86_64" },
            };

            // Merge WireGuard labels for mesh enrollment
            foreach (var (key, value) in wgLabels)
                labels[key] = value;

            // ================================================================
            // STEP 6: Deploy VM (NodeAgent owns the cloud-init template)
            // ================================================================
            var blockStoreVm = await vmService.CreateVmAsync(
                userId: "system",
                request: new CreateVmRequest(
                    Name: vmName,
                    Spec: vmSpec,
                    VmType: VmType.BlockStore,
                    NodeId: node.Id,
                    Labels: labels
                ),
                node.Id
            );

            // ================================================================
            // STEP 7: Store BlockStoreInfo on the node
            // ================================================================
            node.BlockStoreInfo = new BlockStoreInfo
            {
                BlockStoreVmId = blockStoreVm.VmId,
                ListenAddress = $"{advertiseIp}:{BlockStoreVmSpec.BitswapPort}",
                ApiPort = BlockStoreVmSpec.ApiPort,
                CapacityBytes = vmSpec.DiskBytes,
                UsedBytes = 0,
                Status = BlockStoreStatus.Initializing,
            };

            // Store auth token on the obligation for /api/blockstore/join verification
            var obligation = node.SystemVmObligations
                .FirstOrDefault(o => o.Role == SystemVmRole.BlockStore);
            if (obligation != null)
                obligation.AuthToken = authToken;

            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "Block store VM {VmId} deployed on node {NodeId} " +
                "(advertise: {Addr}, storage: {GB} GB, bootstrap peers: {Count}, arch: {Arch})",
                blockStoreVm.VmId, node.Id,
                node.BlockStoreInfo.ListenAddress,
                vmSpec.DiskBytes / (1024 * 1024 * 1024),
                bootstrapPeers.Count,
                node.Architecture ?? "x86_64");

            return blockStoreVm.VmId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy block store VM on node {NodeId}", node.Id);
            return null;
        }
    }

    /// <summary>
    /// Returns libp2p multiaddrs for all active block store nodes (excluding the given node).
    /// Format: /ip4/{ip}/tcp/5001/p2p/{peerId}
    /// </summary>
    public async Task<List<string>> GetBootstrapPeersAsync(string? excludeNodeId = null)
    {
        var nodes = await _dataStore.GetAllNodesAsync();
        return CollectBootstrapPeers(nodes, excludeNodeId);
    }

    private static List<string> CollectBootstrapPeers(IEnumerable<Node> nodes, string? excludeNodeId)
    {
        return nodes
            .Where(n =>
                n.Status == NodeStatus.Online &&
                n.Id != excludeNodeId &&
                n.BlockStoreInfo != null &&
                n.BlockStoreInfo.Status == BlockStoreStatus.Active &&
                !string.IsNullOrEmpty(n.BlockStoreInfo.PeerId) &&
                !string.IsNullOrEmpty(n.BlockStoreInfo.ListenAddress))
            .Select(n =>
            {
                // ListenAddress is "ip:port" — extract the IP for the multiaddr
                var ip = n.BlockStoreInfo!.ListenAddress.Split(':')[0];
                return $"/ip4/{ip}/tcp/{BlockStoreVmSpec.BitswapPort}/p2p/{n.BlockStoreInfo.PeerId}";
            })
            .ToList();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Join — called by BlockStoreController on POST /api/blockstore/join
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verify the HMAC token from a joining block store VM.
    /// HMAC-SHA256(authToken, nodeId:vmId) — same pattern as DhtController.
    /// </summary>
    public static bool VerifyJoinToken(string token, string storedAuthToken, string nodeId, string vmId)
    {
        var message = $"{nodeId}:{vmId}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(storedAuthToken));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(expected));
    }

    // ════════════════════════════════════════════════════════════════════════
    // CID index — updated by BlockStoreController on POST /api/blockstore/announce
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Add CIDs to the secondary index for a node (announce action).
    /// </summary>
    public void IndexCids(string nodeId, IEnumerable<string> cids)
    {
        foreach (var cid in cids)
        {
            var nodeSet = _cidIndex.GetOrAdd(cid, _ => new ConcurrentDictionary<string, bool>());
            nodeSet[nodeId] = true;
        }
    }

    /// <summary>
    /// Remove CIDs from the secondary index for a node (remove action).
    /// </summary>
    public void UnindexCids(string nodeId, IEnumerable<string> cids)
    {
        foreach (var cid in cids)
        {
            if (_cidIndex.TryGetValue(cid, out var nodeSet))
            {
                nodeSet.TryRemove(nodeId, out _);
                // Clean up empty entries
                if (nodeSet.IsEmpty)
                    _cidIndex.TryRemove(cid, out _);
            }
        }
    }

    /// <summary>
    /// Look up which nodeIds claim to have a given CID in the secondary index.
    /// </summary>
    public IReadOnlyCollection<string> LocateCid(string cid)
    {
        if (_cidIndex.TryGetValue(cid, out var nodeSet))
            return nodeSet.Keys.ToList();
        return [];
    }

    // ════════════════════════════════════════════════════════════════════════
    // Manifest lifecycle — Phase D implementation (stub returns in Phase A)
    // ════════════════════════════════════════════════════════════════════════

    public async Task<ManifestRecord> RegisterManifestAsync(
            string vmId,
            string rootCid,
            int version,
            List<string> changedBlockCids,
            int blockCount,
            int blockSizeKb,
            ManifestType manifestType,
            long totalBytes,
            int replicationFactor = 3,
            CancellationToken ct = default)
    {
        // Try to load existing from MongoDB to preserve immutable fields
        var existing = await _dataStore.GetManifestAsync(vmId)
            ?? _manifestCache.GetValueOrDefault(vmId);

        ManifestRecord record;

        if (existing == null)
        {
            // First registration for this VM
            record = new ManifestRecord
            {
                Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                VmId = vmId,
                RootCid = rootCid,
                Version = version,
                ChangedBlockCids = changedBlockCids,
                CumulativeBlockCids = changedBlockCids.Take(200).ToList(),
                BlockCount = blockCount,
                BlockSizeKb = blockSizeKb > 0 ? blockSizeKb : BlockSizeConstants.ForType(manifestType),
                ManifestType = manifestType,
                TotalBytes = totalBytes,
                RegisteredAt = DateTime.UtcNow,
                ReplicationFactor = replicationFactor
            };
        }
        else if (version > existing.Version)
        {
            // Advance to newer version — BlockSizeKb and ManifestType are immutable
            existing.RootCid = rootCid;
            existing.Version = version;
            existing.ChangedBlockCids = changedBlockCids;
            existing.BlockCount = blockCount;
            existing.TotalBytes = totalBytes;
            existing.RegisteredAt = DateTime.UtcNow;

            // Accumulate changed CIDs into the rolling sample so the audit
            // covers the full block set across all versions, not just the delta.
            const int CumulativeCap = 200;
            existing.CumulativeBlockCids.AddRange(
                changedBlockCids.Except(existing.CumulativeBlockCids));
            if (existing.CumulativeBlockCids.Count > CumulativeCap)
                existing.CumulativeBlockCids =
                    existing.CumulativeBlockCids[^CumulativeCap..];

            record = existing;
        }
        else
        {
            // Duplicate or stale — return existing without modification
            _logger.LogDebug(
                "Skipping manifest registration for VM {VmId}: incoming v{IncomingVersion} " +
                "≤ current v{CurrentVersion}",
                vmId, version, existing.Version);
            return existing;
        }

        // Persist to MongoDB
        await _dataStore.SaveManifestAsync(record);

        // Update in-memory cache for fast lookups
        _manifestCache[vmId] = record;

        _logger.LogDebug(
            "Manifest registered for VM {VmId}: v{Version}, root={RootCid}, " +
            "blocks={BlockCount} × {BlockSizeKb} KB ({Type})",
            vmId, version, rootCid[..Math.Min(12, rootCid.Length)],
            blockCount, record.BlockSizeKb, record.ManifestType);

        return record;
    }

    public async Task<ReplicationAudit> AuditManifestReplicationAsync(
        string vmId, CancellationToken ct = default)
    {
        // Phase D stub — LazysyncManager calls DHT FindProviders for each chunk CID
        // and calls this indirectly by updating ConfirmedVersion on the manifest.
        // This endpoint returns the last known audit state from MongoDB.
        var manifest = await _dataStore.GetManifestAsync(vmId)
            ?? _manifestCache.GetValueOrDefault(vmId);

        return new ReplicationAudit
        {
            VmId = vmId,
            Version = manifest?.Version ?? 0,
            TotalChunks = manifest?.BlockCount ?? 0,
            ChunksWithSufficientProviders = manifest?.ConfirmedVersion == manifest?.Version
                                            ? manifest?.BlockCount ?? 0
                                            : 0,
            UnderReplicatedChunks = [],
            // Phase D: LazysyncManager populates UnderReplicatedChunks from DHT audit
        };
    }

    /// <summary>
    /// Select the best migration target for a VM whose host node went offline.
    ///
    /// Steps:
    ///   1. Fetch the VM — provides spec (CPU/RAM/disk/GPU/tier) and source node ID.
    ///   2. Fetch the manifest — confirms a recoverable confirmed version exists.
    ///   3. Score all online nodes via the existing scheduling service (reuses all
    ///      hard filters: Online, BlockStore Active, resource headroom, architecture,
    ///      GPU, region). Exclude the offline source node.
    ///   4. Return the highest-scoring eligible node.
    ///
    /// Block locality is not considered — the target fetches overlay blocks from
    /// scattered DHT providers via bitswap after boot.
    /// </summary>
    public async Task<MigrationPlan> PlanMigrationAsync(
        string vmId, List<string> candidateNodeIds, CancellationToken ct = default)
    {
        // ── Step 1: Fetch VM ────────────────────────────────────────────────
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning("PlanMigrationAsync: VM {VmId} not found", vmId);
            return new MigrationPlan
            {
                VmId = vmId,
                MigrationStatus = "Error",
                Reason = "VM not found"
            };
        }

        var sourceNodeId = vm.NodeId;

        // ── Step 2: Fetch manifest ──────────────────────────────────────────
        var manifest = await _dataStore.GetManifestAsync(vmId)
            ?? _manifestCache.GetValueOrDefault(vmId);

        if (manifest == null || manifest.ConfirmedVersion == 0)
        {
            _logger.LogWarning(
                "PlanMigrationAsync: VM {VmId} has no confirmed replica — cannot migrate",
                vmId);
            return new MigrationPlan
            {
                VmId = vmId,
                MigrationStatus = "Unrecoverable",
                Reason = manifest == null
                    ? "No manifest exists — lazysync never completed initial seeding"
                    : "ConfirmedVersion is 0 — seeding started but no version was confirmed yet"
            };
        }

        // ── Step 3: Derive scheduling hints from source node ────────────────
        // Source node is offline but still in the DB — use its region and
        // architecture so the replacement lands on compatible hardware.
        var sourceNode = await _dataStore.GetNodeAsync(sourceNodeId ?? "");
        var architecture = sourceNode?.Architecture;
        var preferredRegion = sourceNode?.Region;

        // ── Step 4: Score all online nodes ──────────────────────────────────
        // GetScoredNodesForVmAsync enforces all hard filters internally:
        //   NodeStatus.Online, BlockStore Active (when replicationFactor > 0),
        //   CPU/RAM/disk headroom, architecture, GPU, load limits.
        // We only need to additionally exclude the source node itself.
        var tier = vm.Spec.QualityTier;

        List<ScoredNode> scored;
        try
        {
            scored = await _schedulingService.GetScoredNodesForVmAsync(
                vm.Spec,
                tier,
                preferredRegion: preferredRegion,
                preferredZone: null,
                requiredArchitecture: architecture,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PlanMigrationAsync: scheduling service failed for VM {VmId}", vmId);
            return new MigrationPlan
            {
                VmId = vmId,
                ConfirmedManifestRootCid = manifest.ConfirmedRootCid,
                ConfirmedVersion = manifest.ConfirmedVersion,
                MigrationStatus = "Error",
                Reason = $"Scheduling service error: {ex.Message}"
            };
        }

        var target = scored
            .Where(sn => sn.RejectionReason == null && sn.Node.Id != sourceNodeId)
            .OrderByDescending(sn => sn.TotalScore)
            .FirstOrDefault();

        // ── Step 5: Return plan ─────────────────────────────────────────────
        if (target == null)
        {
            var rejectionSummary = scored
                .Where(sn => sn.Node.Id != sourceNodeId)
                .GroupBy(sn => sn.RejectionReason)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => $"{g.Key} ({g.Count()}×)")
                .ToList();

            _logger.LogWarning(
                "PlanMigrationAsync: no eligible target for VM {VmId}. " +
                "Top rejection reasons: {Reasons}",
                vmId, string.Join("; ", rejectionSummary));

            return new MigrationPlan
            {
                VmId = vmId,
                ConfirmedManifestRootCid = manifest.ConfirmedRootCid,
                ConfirmedVersion = manifest.ConfirmedVersion,
                MigrationStatus = "NoTarget",
                Reason = rejectionSummary.Count > 0
                    ? $"No eligible node. Top rejections: {string.Join("; ", rejectionSummary)}"
                    : "No other online nodes available"
            };
        }

        _logger.LogInformation(
            "PlanMigrationAsync: VM {VmId} → node {TargetNodeId} " +
            "(score {Score:F2}, region {Region}, confirmedV={ConfirmedVersion}/{CurrentVersion})",
            vmId, target.Node.Id, target.TotalScore,
            target.Node.Region ?? "any",
            manifest.ConfirmedVersion, manifest.Version);

        return new MigrationPlan
        {
            VmId = vmId,
            TargetNodeId = target.Node.Id,
            ConfirmedManifestRootCid = manifest.ConfirmedRootCid,
            ConfirmedVersion = manifest.ConfirmedVersion,
            MigrationStatus = manifest.ConfirmedVersion == manifest.Version
                ? "Ready"       // fully caught up — no data loss
                : "ReadyWithDataLoss",  // confirmed lags current — minor loss possible
            Reason = $"Node {target.Node.Id} score {target.TotalScore:F2}, " +
                     $"region {target.Node.Region ?? "any"}" +
                     (manifest.ConfirmedVersion < manifest.Version
                         ? $"; {manifest.Version - manifest.ConfirmedVersion} unconfirmed " +
                           $"version(s) will be lost"
                         : "")
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Stats
    // ════════════════════════════════════════════════════════════════════════

    public async Task<BlockStoreStats> GetNetworkStatsAsync()
    {
        var nodes = await _dataStore.GetAllNodesAsync();
        var blockStoreNodes = nodes
            .Where(n => n.BlockStoreInfo != null)
            .Select(n => n.BlockStoreInfo!)
            .ToList();

        return new BlockStoreStats
        {
            TotalNodes = blockStoreNodes.Count(i => i.Status == BlockStoreStatus.Active),
            TotalCapacityBytes = blockStoreNodes.Sum(i => i.CapacityBytes),
            TotalUsedBytes = blockStoreNodes.Sum(i => i.UsedBytes),
            TotalBlocks = blockStoreNodes.Sum(i => i.BlockCount),
            ManifestCount = (int)await _dataStore.GetManifestCountAsync(),
            AvgReplication = 0, // Phase D: compute from DHT provider records
            TotalReplicatedBytes = blockStoreNodes.Sum(i => i.UsedBytes),
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // WireGuard label resolution (mirrors DhtNodeService pattern)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve WireGuard mesh labels for a block store VM.
    /// Follows the same logic as DhtNodeService.ResolveWireGuardLabels:
    ///   - CGNAT nodes: use assigned relay's WG endpoint + node's tunnel IP
    ///   - Relay nodes: use own relay info (VM joins its own mesh)
    ///   - Public nodes without relay: return empty (no WG mesh enrollment)
    /// </summary>
    private static Dictionary<string, string> ResolveWireGuardLabels(
        Node node, IEnumerable<Node> allNodes, ILogger<BlockStoreService> logger)
    {
        var labels = new Dictionary<string, string>();

        // CGNAT node: get WG labels from the assigned relay
        if (node.IsBehindCgnat && node.CgnatInfo != null &&
            !string.IsNullOrEmpty(node.CgnatInfo.TunnelIp) &&
            !string.IsNullOrEmpty(node.CgnatInfo.AssignedRelayNodeId))
        {
            var relayNode = allNodes.FirstOrDefault(n =>
                n.Id == node.CgnatInfo.AssignedRelayNodeId);

            if (relayNode?.RelayInfo != null &&
                relayNode.RelayInfo.Status == RelayStatus.Active)
            {
                var relayTunnelIp = relayNode.RelayInfo.TunnelIp;

                // Derive a unique tunnel IP for the BlockStore VM from the host's
                // last octet — same pattern as DhtNodeService (host .2 → DHT .202).
                // BlockStore uses offset 210: host .2 → BlockStore .212
                // This gives the VM its own peer slot in the relay's WG table,
                // making port 5001 directly routable via the mesh without DNAT.
                //
                // Offset summary (relay subnet 10.20.{subnet}.x):
                //   .254       relay gateway
                //   .199       DHT VM on relay node
                //   .202       BlockStore VM on relay node
                //   .200+N     DHT VM on CGNAT node (N = host last octet)
                //   .210+N     BlockStore VM on CGNAT node (N = host last octet)
                var hostTunnelIp = node.CgnatInfo.TunnelIp;
                var parts = hostTunnelIp.Split('.');
                if (parts.Length == 4 && int.TryParse(parts[3], out var hostOctet))
                {
                    var bsOctet = 210 + hostOctet;
                    if (bsOctet <= 253)
                    {
                        var bsTunnelIp = $"{parts[0]}.{parts[1]}.{parts[2]}.{bsOctet}";
                        labels["wg-tunnel-ip"] = $"{bsTunnelIp}/24";
                        labels["wg-relay-endpoint"] = $"{relayNode.PublicIp}:51820";
                        labels["wg-relay-pubkey"] = relayNode.RelayInfo.WireGuardPublicKey ?? "";
                        labels["wg-relay-api"] = $"http://{relayTunnelIp}:8080/api/relay";

                        logger.LogInformation(
                            "BlockStore VM on CGNAT node {NodeId}: tunnel IP {TunnelIp} " +
                            "(derived from host {HostIp})",
                            node.Id, bsTunnelIp, hostTunnelIp);
                    }
                    else
                    {
                        logger.LogWarning(
                            "BlockStore VM on CGNAT node {NodeId}: host octet {HostOctet} " +
                            "produces BS octet {BsOctet} (>253) — skipping WG mesh enrollment",
                            node.Id, hostOctet, bsOctet);
                    }
                }
            }

            return labels;
        }

        // Relay node: the block store VM joins the relay's own WG mesh.
        // Uses the same static IP convention as DhtNodeService:
        //   DHT VM  → 10.20.{subnet}.199
        //   BlockStore VM → 10.20.{subnet}.202
        // The wg-mesh-enroll.sh script handles peer registration via
        // NodeAgent proxy at http://192.168.122.1:5100/api/relay/wg-mesh-enroll.
        // The relay API deduplicates by AllowedIPs — if a stale peer already
        // holds this IP, wg-mesh-enroll.sh must remove it first.
        // See: blockstore-vm/wg-mesh-enroll.sh stale peer cleanup logic.
        if (node.RelayInfo != null && node.RelayInfo.Status == RelayStatus.Active)
        {
            var subnet = node.RelayInfo.RelaySubnet;
            var relayTunnelIp = node.RelayInfo.TunnelIp;
            labels["wg-tunnel-ip"] = $"10.20.{subnet}.202/24";
            labels["wg-relay-endpoint"] = $"{node.PublicIp}:51820";
            labels["wg-relay-pubkey"] = node.RelayInfo.WireGuardPublicKey ?? "";
            labels["wg-relay-api"] = $"http://{relayTunnelIp}:8080/api/relay";
        }

        // Public node without relay: return empty labels (no WG enrollment)
        return labels;
    }
}