using Microsoft.AspNetCore.Mvc;
using Orchestrator.Services;
using Orchestrator.Data;
using Microsoft.Extensions.Logging;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/relay")]
public class RelayCallbackController : ControllerBase
{
    private readonly IRelayNodeService _relayService;
    private readonly IWireGuardManager _wireGuardManager;
    private readonly INodeService _nodeService;
    private readonly DataStore _dataStore;
    private readonly ILogger<RelayCallbackController> _logger;

    public RelayCallbackController(
        IRelayNodeService relayService,
        IWireGuardManager wireGuardManager,
        INodeService nodeService,
        DataStore dataStore,
        ILogger<RelayCallbackController> logger)
    {
        _relayService = relayService;
        _wireGuardManager = wireGuardManager;
        _nodeService = nodeService;
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

        // Verify relay VM exists
        var node = await _nodeService.GetNodeAsync(notification.NodeId);
        if (node?.RelayInfo?.RelayVmId != notification.RelayVmId)
        {
            _logger.LogWarning(
                "Invalid relay callback: Node {NodeId} has no relay VM {RelayVmId}",
                notification.NodeId, notification.RelayVmId);
            return BadRequest("Invalid relay VM");
        }

        // Verify callback token (security)
        var expectedToken = ComputeRelayToken(notification.NodeId, notification.RelayVmId);
        if (string.IsNullOrEmpty(token) || token != expectedToken)
        {
            _logger.LogWarning(
                "Invalid relay callback token from {NodeId}/{RelayVmId}",
                notification.NodeId, notification.RelayVmId);
            return Unauthorized();
        }

        // Add relay as WireGuard peer on orchestrator
        _logger.LogInformation(
            "Adding relay {NodeId} as WireGuard peer (callback-triggered)",
            node.Id);

        var success = await _wireGuardManager.AddRelayPeerAsync(node);

        if (success)
        {
            _logger.LogInformation(
                "✓ Relay {NodeId} registered successfully via callback - " +
                "CGNAT nodes can now connect",
                node.Id);

            // Update relay status
            if (node.RelayInfo != null)
            {
                node.RelayInfo.IsActive = true;
                node.RelayInfo.LastHealthCheck = DateTime.UtcNow;
                await _dataStore.SaveNodeAsync(node);
            }

            return Ok(new { success = true, message = "Relay registered successfully" });
        }

        _logger.LogError(
            "Failed to add relay {NodeId} as WireGuard peer via callback",
            node.Id);

        return StatusCode(500, "Failed to add relay peer");
    }

    private string ComputeRelayToken(string nodeId, string vmId)
    {
        // HMAC-SHA256 token for security
        var message = $"{nodeId}:{vmId}";
        var secret = Environment.GetEnvironmentVariable("RELAY_CALLBACK_SECRET")
                  ?? "default-secret-change-in-production";

        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(message));
        return Convert.ToBase64String(hash);
    }
}

public record RelayReadyNotification(
    string NodeId,
    string RelayVmId,
    string WireGuardPublicKey,
    string WireGuardEndpoint
);