using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Services;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NodesController : ControllerBase
{
    private readonly INodeService _nodeService;
    private readonly DataStore _dataStore;
    private readonly IVmService _vmService;
    private readonly ILogger<NodesController> _logger;

    public NodesController(
        INodeService nodeService,
        DataStore dataStore,
        IVmService vmService,
        ILogger<NodesController> logger)
    {
        _nodeService = nodeService;
        _dataStore = dataStore;
        _vmService = vmService;
        _logger = logger;
    }

    /// <summary>
    /// Register a new node in the network
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<NodeRegistrationResponse>>> Register(
        [FromBody] NodeRegistrationRequest request)
    {
        try
        {
            var response = await _nodeService.RegisterNodeAsync(request);

            // ✅ NO TOKEN RETURNED! Just acknowledgment
            // Node will authenticate future requests with wallet signatures
            return Ok(ApiResponse<NodeRegistrationResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering node");
            return BadRequest(ApiResponse<NodeRegistrationResponse>.Fail("REGISTRATION_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Node heartbeat - sent periodically by node agents.
    /// Authenticated via wallet signature (stateless!)
    /// </summary>
    [HttpPost("{nodeId}/heartbeat")]
    public async Task<ActionResult<ApiResponse<NodeHeartbeatResponse>>> Heartbeat(
    string nodeId,
    [FromBody] NodeHeartbeat heartbeat)
    {
        // Get node_id from JWT claims (already validated by middleware!)
        var claimNodeId = User.FindFirst("node_id")?.Value;

        if (claimNodeId != nodeId)
        {
            return Unauthorized(ApiResponse<NodeHeartbeatResponse>.Fail(
                "NODE_MISMATCH",
                "Node ID in URL doesn't match authenticated node"));
        }

        // Process heartbeat
        var response = await _nodeService.ProcessHeartbeatAsync(nodeId, heartbeat);

        if (!response.Acknowledged)
        {
            return NotFound(ApiResponse<NodeHeartbeatResponse>.Fail(
                "NODE_NOT_FOUND",
                "Node not registered"));
        }

        return Ok(ApiResponse<NodeHeartbeatResponse>.Ok(response));
    }

    /// <summary>
    /// Node acknowledges command completion.
    /// Authenticated via wallet signature (stateless!)
    /// </summary>
    [HttpPost("{nodeId}/commands/{commandId}/acknowledge")]
    public async Task<ActionResult<ApiResponse<bool>>> AcknowledgeCommand(
        string nodeId,
        string commandId,
        [FromBody] CommandAcknowledgment ack,
        [FromHeader(Name = "Authorization")] string? authorization)
    {
        // =====================================================
        // Validate API Key (same as heartbeat)
        // =====================================================
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Bearer "))
        {
            return Unauthorized(ApiResponse<bool>.Fail(
                "MISSING_API_KEY", "API key required in Authorization header"));
        }

        var apiKey = authorization.Substring(7); // Remove "Bearer "

        // Hash the API key
        var keyHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(apiKey)));

        // Get node
        var node = await _nodeService.GetNodeAsync(nodeId);

        if (node == null || node.ApiKeyHash != keyHash)
        {
            return Unauthorized(ApiResponse<bool>.Fail(
                "INVALID_API_KEY", "Invalid API key"));
        }

        // Update last used
        node.ApiKeyLastUsedAt = DateTime.UtcNow;
        await _dataStore.SaveNodeAsync(node);

        // =====================================================
        // Process Command Acknowledgment
        // =====================================================
        try
        {
            var result = await _nodeService.ProcessCommandAcknowledgmentAsync(
                nodeId, commandId, ack);

            if (!result)
            {
                return NotFound(ApiResponse<bool>.Fail(
                    "COMMAND_NOT_FOUND",
                    "Command not found or already processed"));
            }

            return Ok(ApiResponse<bool>.Ok(true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing acknowledgment for command {CommandId} from node {NodeId}",
                commandId, nodeId);

            return StatusCode(500, ApiResponse<bool>.Fail(
                "PROCESSING_ERROR",
                "Failed to process acknowledgment"));
        }
    }

    /// <summary>
    /// List all nodes
    /// </summary>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<ApiResponse<List<Node>>>> GetAll([FromQuery] string? status)
    {
        NodeStatus? statusFilter = null;
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<NodeStatus>(status, true, out var parsedStatus))
        {
            statusFilter = parsedStatus;
        }

        var nodes = await _nodeService.GetAllNodesAsync(statusFilter);
        return Ok(ApiResponse<List<Node>>.Ok(nodes));
    }

    /// <summary>
    /// Get a specific node
    /// </summary>
    [HttpGet("{nodeId}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<Node>>> Get(string nodeId)
    {
        var node = await _nodeService.GetNodeAsync(nodeId);

        if (node == null)
        {
            return NotFound(ApiResponse<Node>.Fail("NOT_FOUND", "Node not found"));
        }

        return Ok(ApiResponse<Node>.Ok(node));
    }

    /// <summary>
    /// Remove a node from the network
    /// </summary>
    [HttpDelete("{nodeId}")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ApiResponse<bool>>> Remove(string nodeId)
    {
        var result = await _nodeService.RemoveNodeAsync(nodeId);

        if (!result)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Node not found"));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }
}