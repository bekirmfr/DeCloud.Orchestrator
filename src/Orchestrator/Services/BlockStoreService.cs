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

    /// <summary>
    /// Full offset→CID map for the current manifest version.
    /// Sent by LazysyncDaemon each cycle alongside ChangedBlockCids.
    /// Replaced on every version advance (not accumulated).
    /// Key: byte offset in the overlay. Value: CIDv1 string.
    /// </summary>
    [MongoDB.Bson.Serialization.Attributes.BsonDictionaryOptions(
        MongoDB.Bson.Serialization.Options.DictionaryRepresentation.ArrayOfArrays)]
    public Dictionary<long, string> CurrentChunkMap { get; set; } = new();

    /// <summary>
    /// Snapshot of CurrentChunkMap at the moment ConfirmedVersion advanced.
    /// Written by LazysyncManager when all chunks are confirmed ≥N providers.
    /// This is what migration uses: target node fetches each CID from the block
    /// store network and writes it at the correct byte offset in the new overlay.
    /// </summary>
    [MongoDB.Bson.Serialization.Attributes.BsonDictionaryOptions(
        MongoDB.Bson.Serialization.Options.DictionaryRepresentation.ArrayOfArrays)]
    public Dictionary<long, string> ConfirmedChunkMap { get; set; } = new();

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
/// Blockstore data needed to migrate a VM. Contains only what BlockStoreService
/// knows — manifest CID, confirmed version, and chunk map. Node selection is
/// handled by VmSchedulerService via IVmSchedulingService.
/// Returns null when no confirmed replica exists (Unrecoverable case).
/// </summary>
public class MigrationManifest
{
    public string VmId { get; set; } = string.Empty;
    public string ConfirmedRootCid { get; set; } = string.Empty;
    public int ConfirmedVersion { get; set; }
    public int CurrentVersion { get; set; }
    public Dictionary<long, string> ChunkMap { get; set; } = new();
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
        Dictionary<long, string>? chunkMap = null,
        CancellationToken ct = default);

    // Replication audit (Phase D implementation; interface defined now)
    Task<ReplicationAudit> AuditManifestReplicationAsync(string vmId, CancellationToken ct = default);

    // Migration support — returns only blockstore data; node selection is the scheduler's concern
    Task<MigrationManifest?> GetMigrationManifestAsync(string vmId, CancellationToken ct = default);

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

    public BlockStoreService(DataStore dataStore, ILogger<BlockStoreService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
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
            Dictionary<long, string>? chunkMap = null,
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
                CurrentChunkMap = chunkMap ?? new(),
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
            existing.CurrentChunkMap = chunkMap ?? existing.CurrentChunkMap;

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

    public async Task<MigrationManifest?> GetMigrationManifestAsync(
        string vmId, CancellationToken ct = default)
    {
        // ── Step 1: Fetch VM ────────────────────────────────────────────────
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning("GetMigrationManifestAsync: VM {VmId} not found", vmId);
            return null;
        }

        // ── Step 2: Fetch manifest ──────────────────────────────────────────
        var manifest = await _dataStore.GetManifestAsync(vmId)
            ?? _manifestCache.GetValueOrDefault(vmId);

        if (manifest == null || manifest.ConfirmedVersion == 0)
        {
            _logger.LogWarning(
                "GetMigrationManifestAsync: VM {VmId} has no confirmed replica — cannot migrate. " +
                "Reason: {Reason}",
                vmId,
                manifest == null
                    ? "No manifest exists — lazysync never completed initial seeding"
                    : "ConfirmedVersion is 0 — seeding started but no version was confirmed yet");
            return null;
        }

        _logger.LogInformation(
            "GetMigrationManifestAsync: VM {VmId} confirmedV={ConfirmedVersion}/{CurrentVersion}, " +
            "{ChunkCount} chunks",
            vmId, manifest.ConfirmedVersion, manifest.Version,
            manifest.ConfirmedChunkMap?.Count ?? 0);

        return new MigrationManifest
        {
            VmId = vmId,
            ConfirmedRootCid = manifest.ConfirmedRootCid ?? string.Empty,
            ConfirmedVersion = manifest.ConfirmedVersion,
            CurrentVersion = manifest.Version,
            ChunkMap = manifest.ConfirmedChunkMap ?? new()
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
}