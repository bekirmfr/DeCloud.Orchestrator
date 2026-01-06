using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Orchestrator.Persistence;
using Orchestrator.Models;
using Orchestrator.Background;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/relay")]
public class RelayCallbackController : ControllerBase
{
    private readonly IWireGuardManager _wireGuardManager;
    private readonly INodeService _nodeService;
    private readonly DataStore _dataStore;
    private readonly ILogger<RelayCallbackController> _logger;

    public RelayCallbackController(
        IWireGuardManager wireGuardManager,
        INodeService nodeService,
        DataStore dataStore,
        ILogger<RelayCallbackController> logger)
    {
        _wireGuardManager = wireGuardManager;
        this._nodeService = nodeService;
        _dataStore = dataStore;
        _logger = logger;
    }

    [HttpPost("register-callback")]
    public async Task<IActionResult> RegisterCallback(
        [FromBody] RelayReadyNotification notification,
        [FromHeader(Name = "X-Relay-Token")] string? token)
    {
        _logger.LogInformation(
            "Received relay registration callback from {NodeId}/{RelayVmId}",
            notification.NodeId, notification.RelayVmId);

        // =====================================================
        // STEP 1: Verify relay VM exists
        // =====================================================
        var node = await _nodeService.GetNodeAsync(notification.NodeId);

        if (node?.RelayInfo?.RelayVmId != notification.RelayVmId)
        {
            _logger.LogWarning(
                "Invalid relay callback: Node {NodeId} has no relay VM {RelayVmId}",
                notification.NodeId, notification.RelayVmId);
            return BadRequest("Invalid relay VM");
        }

        // =====================================================
        // STEP 2: Verify callback token using WireGuard private key
        // =====================================================
        if (string.IsNullOrEmpty(node.RelayInfo.WireGuardPrivateKey))
        {
            _logger.LogError(
                "Cannot verify relay callback: WireGuard private key not found for {NodeId}",
                notification.NodeId);
            return StatusCode(500, "Relay private key not available");
        }

        var expectedToken = ComputeCallbackToken(
            notification.NodeId,
            notification.RelayVmId,
            node.RelayInfo.WireGuardPrivateKey);

        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning(
                "Relay callback rejected: Missing X-Relay-Token header from {NodeId}",
                notification.NodeId);
            return Unauthorized("Missing authentication token");
        }

        // Constant-time comparison to prevent timing attacks
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(expectedToken)))
        {
            _logger.LogWarning(
                "Relay callback rejected: Invalid token from {NodeId}/{RelayVmId}",
                notification.NodeId, notification.RelayVmId);
            return Unauthorized("Invalid authentication token");
        }

        _logger.LogInformation(
            "✓ Relay callback authenticated successfully for {NodeId} using WireGuard private key",
            notification.NodeId);

        // =====================================================
        // STEP 3: Add relay as WireGuard peer on orchestrator
        // =====================================================
        var success = await _wireGuardManager.AddRelayPeerAsync(node);

        if (success)
        {
            _logger.LogInformation(
                "✓ Relay {NodeId} registered successfully via callback - " +
                "CGNAT nodes can now connect",
                node.Id);

            // Update relay status
            node.RelayInfo.IsActive = true;
            node.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            node.RelayInfo.Status = RelayStatus.Active;
            await _dataStore.SaveNodeAsync(node);

            return Ok(new
            {
                success = true,
                message = "Relay registered successfully",
                relay_id = node.Id,
                orchestrator_peer_added = true
            });
        }

        _logger.LogError(
            "Failed to add relay {NodeId} as WireGuard peer via callback",
            node.Id);

        return StatusCode(500, "Failed to add relay peer");
    }

    /// <summary>
    /// Compute HMAC-SHA256 callback token using relay's WireGuard private key
    /// </summary>
    private string ComputeCallbackToken(string nodeId, string vmId, string wireGuardPrivateKey)
    {
        // Message: nodeId:vmId
        var message = $"{nodeId}:{vmId}";

        // Secret: relay's WireGuard private key (unique per relay)
        var secret = wireGuardPrivateKey.Trim();

        // HMAC-SHA256
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));

        return Convert.ToBase64String(hash);
    }
}

public record RelayReadyNotification(
    string NodeId,
    string RelayVmId,
    string WireGuardPublicKey,
    string WireGuardEndpoint
);