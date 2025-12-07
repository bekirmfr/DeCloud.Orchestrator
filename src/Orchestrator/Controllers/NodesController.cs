using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NodesController : ControllerBase
{
    private readonly INodeService _nodeService;
    private readonly IVmService _vmService;
    private readonly ILogger<NodesController> _logger;

    public NodesController(
        INodeService nodeService,
        IVmService vmService,
        ILogger<NodesController> logger)
    {
        _nodeService = nodeService;
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
            return Ok(ApiResponse<NodeRegistrationResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering node");
            return BadRequest(ApiResponse<NodeRegistrationResponse>.Fail("REGISTRATION_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Node heartbeat - sent periodically by node agents
    /// </summary>
    [HttpPost("{nodeId}/heartbeat")]
    public async Task<ActionResult<ApiResponse<NodeHeartbeatResponse>>> Heartbeat(
        string nodeId,
        [FromBody] NodeHeartbeat heartbeat,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken)
    {
        // Validate node token
        if (string.IsNullOrEmpty(nodeToken) || 
            !await _nodeService.ValidateNodeTokenAsync(nodeId, nodeToken))
        {
            return Unauthorized(ApiResponse<NodeHeartbeatResponse>.Fail("UNAUTHORIZED", "Invalid node token"));
        }

        var response = await _nodeService.ProcessHeartbeatAsync(nodeId, heartbeat);
        
        if (!response.Acknowledged)
        {
            return NotFound(ApiResponse<NodeHeartbeatResponse>.Fail("NODE_NOT_FOUND", "Node not registered"));
        }

        return Ok(ApiResponse<NodeHeartbeatResponse>.Ok(response));
    }

    /// <summary>
    /// Node acknowledges command completion
    /// Called by node agent after executing a command
    /// </summary>
    [HttpPost("{nodeId}/commands/{commandId}/acknowledge")]
    public async Task<ActionResult<ApiResponse<bool>>> AcknowledgeCommand(
        string nodeId,
        string commandId,
        [FromBody] CommandAcknowledgment ack,
        [FromHeader(Name = "X-Node-Token")] string? nodeToken)
    {
        // Validate node token
        if (string.IsNullOrEmpty(nodeToken) ||
            !await _nodeService.ValidateNodeTokenAsync(nodeId, nodeToken))
        {
            return Unauthorized(ApiResponse<bool>.Fail("UNAUTHORIZED", "Invalid node token"));
        }

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
        if (!string.IsNullOrEmpty(status) && Enum.TryParse<NodeStatus>(status, true, out var parsed))
        {
            statusFilter = parsed;
        }

        var nodes = await _nodeService.GetAllNodesAsync(statusFilter);
        return Ok(ApiResponse<List<Node>>.Ok(nodes));
    }

    /// <summary>
    /// Get a specific node
    /// </summary>
    [HttpGet("{nodeId}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<Node>>> GetNode(string nodeId)
    {
        var node = await _nodeService.GetNodeAsync(nodeId);
        if (node == null)
        {
            return NotFound(ApiResponse<Node>.Fail("NOT_FOUND", "Node not found"));
        }

        return Ok(ApiResponse<Node>.Ok(node));
    }

    /// <summary>
    /// Get VMs running on a node
    /// </summary>
    [HttpGet("{nodeId}/vms")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<List<VirtualMachine>>>> GetNodeVms(string nodeId)
    {
        var node = await _nodeService.GetNodeAsync(nodeId);
        if (node == null)
        {
            return NotFound(ApiResponse<List<VirtualMachine>>.Fail("NOT_FOUND", "Node not found"));
        }

        var vms = await _vmService.GetVmsByNodeAsync(nodeId);
        return Ok(ApiResponse<List<VirtualMachine>>.Ok(vms));
    }

    /// <summary>
    /// Update node status (admin only)
    /// </summary>
    [HttpPatch("{nodeId}/status")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateStatus(
        string nodeId, 
        [FromBody] UpdateNodeStatusRequest request)
    {
        if (!Enum.TryParse<NodeStatus>(request.Status, true, out var status))
        {
            return BadRequest(ApiResponse<bool>.Fail("INVALID_STATUS", "Invalid status value"));
        }

        var success = await _nodeService.UpdateNodeStatusAsync(nodeId, status);
        if (!success)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Node not found"));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Remove a node from the network (admin only)
    /// </summary>
    [HttpDelete("{nodeId}")]
    [Authorize(Roles = "admin")]
    public async Task<ActionResult<ApiResponse<bool>>> RemoveNode(string nodeId)
    {
        // Check if node has running VMs
        var vms = await _vmService.GetVmsByNodeAsync(nodeId);
        var runningVms = vms.Where(v => v.Status == VmStatus.Running).ToList();
        
        if (runningVms.Any())
        {
            return BadRequest(ApiResponse<bool>.Fail(
                "HAS_RUNNING_VMS", 
                $"Node has {runningVms.Count} running VMs. Migrate or stop them first."));
        }

        var success = await _nodeService.RemoveNodeAsync(nodeId);
        if (!success)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "Node not found"));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }
}

public record UpdateNodeStatusRequest(string Status);
