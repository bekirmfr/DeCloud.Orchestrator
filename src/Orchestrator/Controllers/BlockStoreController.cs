using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Controllers;

/// <summary>
/// REST API for block store VMs to register, announce blocks, and query the network.
///
/// Authentication: HMAC-SHA256(authToken, nodeId:vmId) via X-BlockStore-Token header.
/// The authToken is generated at deploy time and stored on the node's BlockStore obligation.
/// This is the same pattern as DhtController.
///
/// Endpoints:
///   POST /api/blockstore/join      — register peer ID, get bootstrap peers (REAL)
///   POST /api/blockstore/announce  — batch announce/withdraw CIDs (REAL)
///   GET  /api/blockstore/locate/{cid}  — find nodes with a block (STUB — Phase D: DHT query)
///   POST /api/blockstore/manifest      — register VM manifest from lazysync (STUB — Phase D)
///   GET  /api/blockstore/audit/{vmId}  — replication health for a VM (STUB — Phase D)
///   GET  /api/blockstore/stats         — network-wide storage metrics (REAL)
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class BlockStoreController : ControllerBase
{
    private readonly IBlockStoreService _blockStoreService;
    private readonly DataStore _dataStore;
    private readonly ILogger<BlockStoreController> _logger;

    public BlockStoreController(
        IBlockStoreService blockStoreService,
        DataStore dataStore,
        ILogger<BlockStoreController> logger)
    {
        _blockStoreService = blockStoreService;
        _dataStore = dataStore;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════════════
    // POST /api/blockstore/join (REAL)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by block store VMs on boot to register their peer ID and receive
    /// bootstrap peers for DHT/bitswap network discovery.
    /// </summary>
    [HttpPost("join")]
    public async Task<IActionResult> Join(
        [FromBody] BlockStoreJoinRequest request,
        [FromHeader(Name = "X-BlockStore-Token")] string? token)
    {
        if (string.IsNullOrEmpty(request.NodeId) ||
            string.IsNullOrEmpty(request.VmId) ||
            string.IsNullOrEmpty(request.PeerId))
        {
            return BadRequest("Missing required fields: nodeId, vmId, peerId");
        }

        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning(
                "Block store join rejected: missing X-BlockStore-Token from {NodeId}/{VmId}",
                request.NodeId, request.VmId);
            return Unauthorized("Missing authentication token");
        }

        var node = await _dataStore.GetNodeAsync(request.NodeId);
        if (node == null)
        {
            _logger.LogWarning("Block store join rejected: node {NodeId} not found", request.NodeId);
            return NotFound("Node not found");
        }

        var obligation = node.SystemVmObligations
            .FirstOrDefault(o => o.Role == SystemVmRole.BlockStore && o.VmId == request.VmId);

        if (obligation == null)
        {
            _logger.LogWarning(
                "Block store join rejected: no BlockStore obligation with VmId {VmId} on node {NodeId}",
                request.VmId, request.NodeId);
            return NotFound("Block store VM not found on this node");
        }

        var storedToken = obligation.AuthToken;
        if (string.IsNullOrEmpty(storedToken))
        {
            _logger.LogError(
                "Block store join rejected: auth token not set on obligation for node {NodeId}",
                request.NodeId);
            return StatusCode(500, "Auth token not configured");
        }

        if (!BlockStoreService.VerifyJoinToken(token, storedToken, request.NodeId, request.VmId))
        {
            _logger.LogWarning(
                "Block store join rejected: invalid token from {NodeId}/{VmId}",
                request.NodeId, request.VmId);
            return Forbid();
        }

        // Register peer ID and update block store info
        node.BlockStoreInfo ??= new BlockStoreInfo
        {
            BlockStoreVmId = request.VmId,
            // Prefer request IP; fall back to the VM label baked at deploy time.
            // Never produce ":5001" with an empty IP — that breaks bootstrap peer generation.
            ListenAddress = !string.IsNullOrEmpty(request.AdvertiseIp)
                ? $"{request.AdvertiseIp}:{BlockStoreVmSpec.BitswapPort}"
                : (await _dataStore.GetVmAsync(request.VmId))
                      ?.Labels?.GetValueOrDefault("blockstore-advertise-ip") is { } labelIp
                      && !string.IsNullOrEmpty(labelIp)
                  ? $"{labelIp}:{BlockStoreVmSpec.BitswapPort}"
                  : string.Empty,
            ApiPort = BlockStoreVmSpec.ApiPort,
            CapacityBytes = request.CapacityBytes,
        };

        node.BlockStoreInfo.BlockStoreVmId = request.VmId; // always overwrite
        node.BlockStoreInfo.PeerId = request.PeerId;
        node.BlockStoreInfo.CapacityBytes = request.CapacityBytes;
        node.BlockStoreInfo.UsedBytes = request.UsedBytes;
        node.BlockStoreInfo.Status = BlockStoreStatus.Active;
        node.BlockStoreInfo.LastHealthCheck = DateTime.UtcNow;

        if (string.IsNullOrEmpty(node.BlockStoreInfo.ListenAddress))
        {
            _logger.LogWarning(
                "Block store join from {NodeId}/{VmId}: could not resolve advertise IP " +
                "— ListenAddress not set. Peer will not appear in bootstrap lists.",
                request.NodeId, request.VmId);
        }

        if (!string.IsNullOrEmpty(request.AdvertiseIp))
            node.BlockStoreInfo.ListenAddress = $"{request.AdvertiseIp}:{BlockStoreVmSpec.BitswapPort}";

        await _dataStore.SaveNodeAsync(node);

        var bootstrapPeers = await _blockStoreService.GetBootstrapPeersAsync(
            excludeNodeId: request.NodeId);

        _logger.LogInformation(
            "Block store VM {VmId} joined on node {NodeId}: peerId={PeerId}, capacity={GB} GB, bootstrap peers={Count}",
            request.VmId, request.NodeId,
            request.PeerId.Length > 12 ? request.PeerId[..12] + "..." : request.PeerId,
            request.CapacityBytes / (1024 * 1024 * 1024),
            bootstrapPeers.Count);

        return Ok(new
        {
            success = true,
            bootstrapPeers,
            peerIdRegistered = request.PeerId,
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // POST /api/blockstore/announce (REAL)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by block store VMs to batch announce or withdraw CIDs they hold.
    /// Updates the orchestrator's secondary CID index.
    /// The DHT itself remains the primary source of truth for provider records.
    /// </summary>
    [HttpPost("announce")]
    public async Task<IActionResult> Announce(
        [FromBody] BlockStoreAnnounceRequest request,
        [FromHeader(Name = "X-BlockStore-Token")] string? token)
    {
        if (string.IsNullOrEmpty(request.NodeId) ||
            string.IsNullOrEmpty(request.VmId) ||
            request.Cids == null || request.Cids.Count == 0)
        {
            return BadRequest("Missing required fields: nodeId, vmId, cids");
        }

        if (string.IsNullOrEmpty(token))
            return Unauthorized("Missing authentication token");

        // Auth: reuse the join token verification (same HMAC pattern)
        var node = await _dataStore.GetNodeAsync(request.NodeId);
        if (node == null)
            return NotFound("Node not found");

        var obligation = node.SystemVmObligations
            .FirstOrDefault(o => o.Role == SystemVmRole.BlockStore && o.VmId == request.VmId);

        if (obligation?.AuthToken == null)
            return Unauthorized("Invalid VM credentials");

        if (!BlockStoreService.VerifyJoinToken(token, obligation.AuthToken, request.NodeId, request.VmId))
            return Forbid();

        // Update secondary index
        var action = request.Action?.ToLowerInvariant() ?? "add";
        if (action == "add")
        {
            _blockStoreService.IndexCids(request.NodeId, request.Cids);

            // Update block count on node info
            if (node.BlockStoreInfo != null)
            {
                node.BlockStoreInfo.BlockCount += request.Cids.Count;
                node.BlockStoreInfo.UsedBytes = request.UsedBytes ?? node.BlockStoreInfo.UsedBytes;
                node.BlockStoreInfo.LastHealthCheck = DateTime.UtcNow;
                await _dataStore.SaveNodeAsync(node);
            }
        }
        else if (action == "remove")
        {
            _blockStoreService.UnindexCids(request.NodeId, request.Cids);

            if (node.BlockStoreInfo != null)
            {
                node.BlockStoreInfo.BlockCount = Math.Max(0,
                    node.BlockStoreInfo.BlockCount - request.Cids.Count);
                await _dataStore.SaveNodeAsync(node);
            }
        }
        else
        {
            return BadRequest($"Unknown action '{action}': expected 'add' or 'remove'");
        }

        _logger.LogDebug(
            "Block store announce from node {NodeId}: {Action} {Count} CIDs",
            request.NodeId, action, request.Cids.Count);

        return Ok(new { success = true, recorded = request.Cids.Count });
    }

    // ════════════════════════════════════════════════════════════════════════
    // GET /api/blockstore/locate/{cid} (STUB — Phase D: DHT query)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Find which nodes have a specific block.
    /// Phase A: queries the in-memory secondary index only.
    /// Phase D: queries the DHT's provider records for authoritative results.
    /// </summary>
    [HttpGet("locate/{cid}")]
    public async Task<IActionResult> Locate(string cid)
    {
        if (string.IsNullOrEmpty(cid))
            return BadRequest("CID is required");

        var nodeIds = _blockStoreService.LocateCid(cid);

        // Resolve node info for each provider
        var providers = new List<object>();
        foreach (var nodeId in nodeIds)
        {
            var node = await _dataStore.GetNodeAsync(nodeId);
            if (node?.BlockStoreInfo != null)
            {
                providers.Add(new
                {
                    nodeId = node.Id,
                    peerId = node.BlockStoreInfo.PeerId,
                    multiaddr = string.IsNullOrEmpty(node.BlockStoreInfo.PeerId)
                        ? null
                        : $"/ip4/{node.BlockStoreInfo.ListenAddress.Split(':')[0]}" +
                          $"/tcp/{BlockStoreVmSpec.BitswapPort}" +
                          $"/p2p/{node.BlockStoreInfo.PeerId}",
                });
            }
        }

        return Ok(new
        {
            cid,
            providers,
            replication = providers.Count,
            note = "Phase A: secondary index only. Phase D: DHT FindProviders.",
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // POST /api/blockstore/manifest (STUB — Phase D: lazysync)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Register an updated VM overlay manifest from a lazysync cycle.
    /// Phase A: stores in-memory. Phase D: persists to MongoDB, triggers audit loop.
    /// </summary>
    [HttpPost("manifest")]
    public async Task<IActionResult> RegisterManifest(
        [FromBody] BlockStoreManifestRequest request)
    {
        if (string.IsNullOrEmpty(request.VmId) || string.IsNullOrEmpty(request.RootCid))
            return BadRequest("Missing required fields: vmId, rootCid");

        var manifest = await _blockStoreService.RegisterManifestAsync(
            request.VmId,
            request.RootCid,
            request.Version,
            request.ChangedBlockCids ?? [],
            request.BlockCount,
            request.BlockSizeKb,
            request.ManifestType,
            request.TotalBytes);

        return Ok(new { success = true, manifestVersion = manifest.Version });
    }

    // ════════════════════════════════════════════════════════════════════════
    // GET /api/blockstore/audit/{vmId} (STUB — Phase D: DHT provider query)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Replication health for a specific VM's manifest.
    /// Phase A: stub. Phase D: queries DHT FindProviders for each chunk CID.
    /// </summary>
    [HttpGet("audit/{vmId}")]
    public async Task<IActionResult> AuditVm(string vmId)
    {
        if (string.IsNullOrEmpty(vmId))
            return BadRequest("vmId is required");

        var audit = await _blockStoreService.AuditManifestReplicationAsync(vmId);
        return Ok(audit);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GET /api/blockstore/stats (REAL)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Network-wide block store statistics.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var stats = await _blockStoreService.GetNetworkStatsAsync();
        return Ok(stats);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// Request DTOs
// ════════════════════════════════════════════════════════════════════════════

public class BlockStoreJoinRequest
{
    public string NodeId { get; set; } = string.Empty;
    public string VmId { get; set; } = string.Empty;

    /// <summary>libp2p peer ID (e.g., "12D3Koo..." or "QmXyz...")</summary>
    public string PeerId { get; set; } = string.Empty;

    /// <summary>IP address the block store VM advertises for bitswap connections.</summary>
    public string? AdvertiseIp { get; set; }

    public long CapacityBytes { get; set; }
    public long UsedBytes { get; set; }
}

public class BlockStoreAnnounceRequest
{
    public string NodeId { get; set; } = string.Empty;
    public string VmId { get; set; } = string.Empty;

    /// <summary>CIDs being announced or withdrawn.</summary>
    public List<string> Cids { get; set; } = [];

    /// <summary>"add" or "remove"</summary>
    public string Action { get; set; } = "add";

    /// <summary>Current storage used (optional — updates BlockStoreInfo).</summary>
    public long? UsedBytes { get; set; }
}

public class BlockStoreManifestRequest
{
    public string VmId { get; set; } = string.Empty;
    public string RootCid { get; set; } = string.Empty;
    public int Version { get; set; }
    public List<string>? ChangedBlockCids { get; set; }
    public int BlockCount { get; set; }
    public int BlockSizeKb { get; set; } = BlockSizeConstants.VmOverlayKb;
    public ManifestType ManifestType { get; set; } = ManifestType.VmOverlay;
    public long TotalBytes { get; set; }
}
