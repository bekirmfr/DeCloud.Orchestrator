using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Services;

// ════════════════════════════════════════════════════════════════════════════
// Support types (defined here; Phase D will persist ManifestRecord to MongoDB)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Tracks the evolving overlay manifest for a VM's disk replication state.
/// Created by lazysync daemon (Phase D) and registered via POST /api/blockstore/manifest.
/// </summary>
public class ManifestRecord
{
    public string VmId { get; set; } = string.Empty;

    /// <summary>Root CID of the manifest DAG for this version.</summary>
    public string RootCid { get; set; } = string.Empty;

    /// <summary>Monotonically increasing version counter. Incremented each lazysync cycle.</summary>
    public int Version { get; set; }

    /// <summary>CIDs of blocks that changed in this version (delta from previous).</summary>
    public List<string> ChangedBlockCids { get; set; } = [];

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

    // Manifest lifecycle (Phase D implementation; interface defined now)
    Task<ManifestRecord> RegisterManifestAsync(string vmId, string rootCid, int version,
        List<string> changedBlockCids, long totalBytes, CancellationToken ct = default);

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

    // Manifest registry (in-memory for Phase A; Phase D persists to MongoDB).
    // Key: vmId
    private readonly ConcurrentDictionary<string, ManifestRecord> _manifests = new();

    public BlockStoreService(DataStore dataStore, ILogger<BlockStoreService> logger)
    {
        _dataStore = dataStore;
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
            var wgLabels = ResolveWireGuardLabels(node, allNodes);

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
            // STEP 3: Collect bootstrap peers
            // ================================================================
            var bootstrapPeers = CollectBootstrapPeers(allNodes, excludeNodeId: node.Id);

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

    public Task<ManifestRecord> RegisterManifestAsync(
        string vmId, string rootCid, int version,
        List<string> changedBlockCids, long totalBytes,
        CancellationToken ct = default)
    {
        // Phase A stub — Phase D wires up the lazysync daemon and MongoDB persistence
        var record = _manifests.AddOrUpdate(
            vmId,
            _ => new ManifestRecord
            {
                VmId = vmId,
                RootCid = rootCid,
                Version = version,
                ChangedBlockCids = changedBlockCids,
                TotalBytes = totalBytes,
                RegisteredAt = DateTime.UtcNow,
            },
            (_, existing) =>
            {
                // Only advance version forward
                if (version > existing.Version)
                {
                    existing.RootCid = rootCid;
                    existing.Version = version;
                    existing.ChangedBlockCids = changedBlockCids;
                    existing.TotalBytes = totalBytes;
                    existing.RegisteredAt = DateTime.UtcNow;
                }
                return existing;
            });

        _logger.LogDebug(
            "Manifest registered for VM {VmId}: v{Version}, root={RootCid}, changed={Count} blocks",
            vmId, version, rootCid[..Math.Min(12, rootCid.Length)], changedBlockCids.Count);

        return Task.FromResult(record);
    }

    public Task<ReplicationAudit> AuditManifestReplicationAsync(
        string vmId, CancellationToken ct = default)
    {
        // Phase A stub — Phase D queries DHT FindProviders for each chunk CID
        _logger.LogDebug("AuditManifestReplicationAsync called for VM {VmId} — stub in Phase A", vmId);

        _manifests.TryGetValue(vmId, out var manifest);
        return Task.FromResult(new ReplicationAudit
        {
            VmId = vmId,
            Version = manifest?.Version ?? 0,
            TotalChunks = manifest?.ChangedBlockCids.Count ?? 0,
            ChunksWithSufficientProviders = 0,
            UnderReplicatedChunks = [],
        });
    }

    public Task<MigrationPlan> PlanMigrationAsync(
        string vmId, List<string> candidateNodeIds, CancellationToken ct = default)
    {
        // Phase A stub — Phase D implements scheduling fit + resource headroom ranking
        _logger.LogDebug("PlanMigrationAsync called for VM {VmId} — stub in Phase A", vmId);

        _manifests.TryGetValue(vmId, out var manifest);
        return Task.FromResult(new MigrationPlan
        {
            VmId = vmId,
            TargetNodeId = candidateNodeIds.FirstOrDefault(),
            ConfirmedManifestRootCid = manifest?.ConfirmedRootCid,
            ConfirmedVersion = manifest?.ConfirmedVersion ?? 0,
            MigrationStatus = "NotStarted",
            Reason = "Phase A stub — Phase D implements full migration planner",
        });
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
            ManifestCount = _manifests.Count,
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
        Node node, IEnumerable<Node> allNodes)
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
                labels["wg-tunnel-ip"] = $"{node.CgnatInfo.TunnelIp}/24";
                labels["wg-relay-endpoint"] = $"{relayNode.PublicIp}:51820";
                labels["wg-relay-pubkey"] = relayNode.RelayInfo.WireGuardPublicKey ?? "";
                labels["wg-relay-api"] = $"http://{relayTunnelIp}:8080/api/relay";
            }

            return labels;
        }

        // Relay node: the block store VM joins the relay's own WG mesh
        // The relay VM's tunnel IP is the gateway for VMs on this node
        if (node.RelayInfo != null && node.RelayInfo.Status == RelayStatus.Active)
        {
            var relayTunnelIp = node.RelayInfo.TunnelIp;
            // Assign a stable tunnel IP within the relay's subnet for this block store VM
            // Convention: relay subnet base + .202 (DHT uses .201, BlockStore uses .202)
            var subnet = node.RelayInfo.RelaySubnet;
            labels["wg-tunnel-ip"] = $"10.20.{subnet}.202/24";
            labels["wg-relay-endpoint"] = $"{node.PublicIp}:51820";
            labels["wg-relay-pubkey"] = node.RelayInfo.WireGuardPublicKey ?? "";
            labels["wg-relay-api"] = $"http://{relayTunnelIp}:8080/api/relay";
        }

        // Public node without relay: return empty labels (no WG enrollment)
        return labels;
    }
}
