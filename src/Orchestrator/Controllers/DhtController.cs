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

        if (dhtObligation == null)
        {
            _logger.LogWarning(
                "DHT join rejected: No DHT obligation with VmId {VmId} found on node {NodeId}",
                request.VmId, request.NodeId);
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
        node.DhtInfo ??= new DhtNodeInfo();
        node.DhtInfo.PeerId = request.PeerId;
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
    string PeerId
);

public record DhtJoinResponse(
    bool Success,
    List<string> BootstrapPeers,
    bool PeerIdRegistered
);
