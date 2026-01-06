using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Background;
using Orchestrator.Models;
using Orchestrator.Services;
using Orchestrator.Services.Auth;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NodesController : ControllerBase
{
    private readonly INodeService _nodeService;
    private readonly IVmService _vmService;
    private readonly INodeSignatureValidator _signatureValidator;
    private readonly ILogger<NodesController> _logger;

    public NodesController(
        INodeService nodeService,
        IVmService vmService,
        INodeSignatureValidator signatureValidator,
        ILogger<NodesController> logger)
    {
        _nodeService = nodeService;
        _vmService = vmService;
        _signatureValidator = signatureValidator;
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
        [FromBody] NodeHeartbeat heartbeat,
        [FromHeader(Name = "X-Node-Signature")] string? signature,
        [FromHeader(Name = "X-Node-Timestamp")] long? timestamp)
    {
        // =====================================================
        // Validate Wallet Signature (Stateless Authentication!)
        // =====================================================
        var requestPath = $"/api/nodes/{nodeId}/heartbeat";

        if (!await _signatureValidator.ValidateNodeSignatureAsync(
            nodeId, signature, timestamp, requestPath))
        {
            return Unauthorized(ApiResponse<NodeHeartbeatResponse>.Fail(
                "INVALID_SIGNATURE",
                "Invalid wallet signature or expired timestamp"));
        }

        // =====================================================
        // Process Heartbeat
        // =====================================================
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
        [FromHeader(Name = "X-Node-Signature")] string? signature,
        [FromHeader(Name = "X-Node-Timestamp")] long? timestamp)
    {
        // =====================================================
        // Validate Wallet Signature (Stateless Authentication!)
        // =====================================================
        var requestPath = $"/api/nodes/{nodeId}/commands/{commandId}/acknowledge";

        if (!await _signatureValidator.ValidateNodeSignatureAsync(
            nodeId, signature, timestamp, requestPath))
        {
            return Unauthorized(ApiResponse<bool>.Fail(
                "INVALID_SIGNATURE",
                "Invalid wallet signature"));
        }

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