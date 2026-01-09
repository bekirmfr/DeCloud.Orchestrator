using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Background;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RelayController : ControllerBase
{
    private readonly IWireGuardManager _wireGuardManager;
    private readonly INodeService _nodeService;
    private readonly DataStore _dataStore;
    private readonly ILogger<RelayController> _logger;

    public RelayController(
        IWireGuardManager wireGuardManager,
        INodeService nodeService,
        DataStore dataStore,
        ILogger<RelayController> logger)
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

    [HttpGet("{relayId}/routing-map")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<RelayRoutingMapResponse>>> GetRoutingMap(
    string relayId,
    [FromHeader(Name = "X-Relay-Token")] string? relayToken)
    {
        // Validate relay token
        if (!await ValidateRelayTokenAsync(relayId, relayToken))
        {
            return Unauthorized(ApiResponse<RelayRoutingMapResponse>.Fail(
                "INVALID_TOKEN", "Invalid relay authentication"));
        }

        var routes = new List<VmRouteInfo>();

        // Find all CGNAT nodes assigned to this relay
        var cgnatNodes = _dataStore.Nodes.Values
            .Where(n => n.CgnatInfo != null &&
                        n.CgnatInfo.AssignedRelayNodeId == relayId &&
                        n.CgnatInfo.TunnelStatus == TunnelStatus.Connected)
            .ToList();

        foreach (var cgnatNode in cgnatNodes)
        {
            // Find all VMs on this CGNAT node
            var vms = _dataStore.VirtualMachines.Values
                .Where(vm => vm.NodeId == cgnatNode.Id &&
                             vm.Status == VmStatus.Running &&
                             vm.IngressConfig?.DefaultSubdomainEnabled == true)
                .ToList();

            foreach (var vm in vms)
            {
                routes.Add(new VmRouteInfo
                (
                    VmId: vm.Id,
                    VmName: vm.Name,
                    Subdomain: vm.IngressConfig.DefaultSubdomain,
                    TargetPort:vm.IngressConfig.DefaultPort,
                    CgnatNodeTunnelIp: cgnatNode.CgnatInfo.TunnelIp,
                    VmPrivateIp: vm.NetworkConfig?.PrivateIp,
                    NodeAgentPort: cgnatNode.AgentPort
                ));
            }
        }

        return Ok(ApiResponse<RelayRoutingMapResponse>.Ok(new RelayRoutingMapResponse
        (
            RelayId: relayId,
            TotalRoutes: routes.Count,
            LastUpdated: DateTime.UtcNow,
            Routes: routes
        )));
    }

    public record RelayRoutingMapResponse(
        string RelayId,
        int TotalRoutes,
        DateTime LastUpdated,
        List<VmRouteInfo> Routes
    );

    public record VmRouteInfo(
        string VmId,
        string VmName,
        string Subdomain,
        int TargetPort,
        string CgnatNodeTunnelIp,
        string? VmPrivateIp,
        int NodeAgentPort
    );

    /// <summary>
    /// Validate relay authentication token
    /// </summary>
    private async Task<bool> ValidateRelayTokenAsync(string relayId, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Relay token validation failed: Missing token for {RelayId}", relayId);
            return false;
        }

        // Get the relay node
        if (!_dataStore.Nodes.TryGetValue(relayId, out var relayNode))
        {
            _logger.LogWarning("Relay token validation failed: Node {RelayId} not found", relayId);
            return false;
        }

        if (relayNode.RelayInfo == null || string.IsNullOrEmpty(relayNode.RelayInfo.WireGuardPrivateKey))
        {
            _logger.LogWarning("Relay token validation failed: WireGuard key missing for {RelayId}", relayId);
            return false;
        }

        // Compute expected token using relay's WireGuard private key
        var message = $"{relayId}:relay-http-proxy";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(relayNode.RelayInfo.WireGuardPrivateKey.Trim()));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var expectedToken = Convert.ToBase64String(hash);

        // Compare tokens (timing-safe)
        var result = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(expectedToken));

        if (!result)
        {
            _logger.LogWarning("Relay token validation failed: Invalid token for {RelayId}", relayId);
        }

        return result;
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