using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DhtController : ControllerBase
{
    private readonly IDhtNodeService _dhtNodeService;
    private readonly DataStore _dataStore;
    private readonly ILogger<DhtController> _logger;

    public DhtController(
        IDhtNodeService dhtNodeService,
        DataStore dataStore,
        ILogger<DhtController> logger)
    {
        _dhtNodeService = dhtNodeService;
        _dataStore = dataStore;
        _logger = logger;
    }

    /// <summary>
    /// Called by DHT VMs to register their peerId and receive bootstrap peers.
    /// Replaces heartbeat-based peerId propagation with direct registration.
    /// </summary>
    [HttpPost("join")]
    public async Task<IActionResult> DhtJoin(
        [FromBody] DhtJoinRequest request,
        [FromHeader(Name = "X-DHT-Token")] string? token)
    {
        // =====================================================
        // STEP 1: Validate required fields
        // =====================================================
        if (string.IsNullOrEmpty(request.NodeId) ||
            string.IsNullOrEmpty(request.VmId) ||
            string.IsNullOrEmpty(request.PeerId))
        {
            return BadRequest("Missing required fields: nodeId, vmId, peerId");
        }

        // =====================================================
        // STEP 2: Verify token header is present
        // =====================================================
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning(
                "DHT join rejected: Missing X-DHT-Token header from {NodeId}/{VmId}",
                request.NodeId, request.VmId);
            return Unauthorized("Missing authentication token");
        }

        // =====================================================
        // STEP 3: Look up the node
        // =====================================================
        var node = await _dataStore.GetNodeAsync(request.NodeId);
        if (node == null)
        {
            _logger.LogWarning(
                "DHT join rejected: Node {NodeId} not found",
                request.NodeId);
            return NotFound("Node not found");
        }

        // =====================================================
        // STEP 4: Find the DHT obligation and its auth token
        // =====================================================
        var dhtObligation = node.SystemVmObligations
            .FirstOrDefault(o => o.Role == SystemVmRole.Dht && o.VmId == request.VmId);


        // Self-heal: VmId was lost (failed save, retry reset) but VM is live.
        // Find a Pending DHT obligation with no VmId and adopt this VM into it.
        // Step 5 will verify the token — no need to check it here.
        if (dhtObligation == null)
        {
            dhtObligation = node.SystemVmObligations
                .FirstOrDefault(o => o.Role == SystemVmRole.Dht
                                  && o.VmId == null
                                  && !string.IsNullOrEmpty(o.AuthToken));

            if (dhtObligation != null)
            {
                _logger.LogInformation(
                    "DHT join self-heal: adopting VM {VmId} into pending DHT obligation on node {NodeId}",
                    request.VmId, request.NodeId);
                dhtObligation.VmId = request.VmId;
                dhtObligation.Status = SystemVmStatus.Deploying;
            }
        }

        // Extended self-heal: obligation has a stale VmId from a replaced VM.
        // The old VM was deleted and a new one created — the orchestrator
        // learns the new VmId here via the join callback, not from the reconciler.
        // Token validation in step 5 confirms the new VM holds the same auth secret.
        if (dhtObligation == null)
        {
            dhtObligation = node.SystemVmObligations
                .FirstOrDefault(o => o.Role == SystemVmRole.Dht
                                  && !string.IsNullOrEmpty(o.AuthToken));

            if (dhtObligation != null)
            {
                _logger.LogInformation(
                    "DHT join self-heal: replacing stale VmId {OldVmId} with {NewVmId} on node {NodeId}",
                    dhtObligation.VmId, request.VmId, request.NodeId);
                dhtObligation.VmId = request.VmId;
                dhtObligation.Status = SystemVmStatus.Deploying;
            }
        }

        if (dhtObligation == null)
        {
            _logger.LogWarning(
                "DHT join rejected: No DHT obligation found on node {NodeId}",
                request.NodeId);
            return NotFound("DHT VM not found on this node");
        }

        var storedToken = dhtObligation.AuthToken;
        if (string.IsNullOrEmpty(storedToken))
        {
            _logger.LogError(
                "DHT join rejected: Auth token not set on DHT obligation for node {NodeId}",
                request.NodeId);
            return StatusCode(500, "Auth token not configured");
        }

        // =====================================================
        // STEP 5: Verify HMAC token
        // =====================================================
        var message = $"{request.NodeId}:{request.VmId}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(storedToken));
        var expectedHash = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(expectedHash)))
        {
            _logger.LogWarning(
                "DHT join rejected: Invalid token from {NodeId}/{VmId}",
                request.NodeId, request.VmId);
            return Forbid();
        }

        // =====================================================
        // STEP 6: Register peerId on the node
        // =====================================================
        var advertiseIp = !string.IsNullOrEmpty(request.AdvertiseIp)
            ? request.AdvertiseIp
            : DhtNodeService.GetAdvertiseIp(node);
        node.DhtInfo ??= new DhtNodeInfo();
        node.DhtInfo.DhtVmId = request.VmId;
        node.DhtInfo.PeerId = request.PeerId;
        node.DhtInfo.ListenAddress = $"{advertiseIp}:{DhtNodeService.DhtListenPort}";
        node.DhtInfo.ApiPort = DhtNodeService.DhtApiPort;
        node.DhtInfo.Status = DhtStatus.Active;
        node.DhtInfo.LastHealthCheck = DateTime.UtcNow;

        // Stamp obligation Active — mirrors RelayController.RegisterCallback pattern.
        // Without this, GetBootstrapPeersAsync() filters by Status == Active and
        // this node's DHT never appears in peer lists for other nodes.
        dhtObligation.Status = SystemVmStatus.Active;
        dhtObligation.ActiveAt ??= DateTime.UtcNow;

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "DHT peer ID registered via /api/dht/join for node {NodeId}: {PeerId}",
            request.NodeId, request.PeerId.Length > 12 ? request.PeerId[..12] : request.PeerId);

        // =====================================================
        // STEP 7: Return bootstrap peers (excluding the requesting node)
        // =====================================================
        var bootstrapPeers = await _dhtNodeService.GetBootstrapPeersAsync(
            excludeNodeId: request.NodeId);

        return Ok(new DhtJoinResponse(
            Success: true,
            BootstrapPeers: bootstrapPeers,
            PeerIdRegistered: true
        ));
    }
}

public record DhtJoinRequest(
    string NodeId,
    string VmId,
    string PeerId,
    string? AdvertiseIp = null
);

public record DhtJoinResponse(
    bool Success,
    List<string> BootstrapPeers,
    bool PeerIdRegistered
);
