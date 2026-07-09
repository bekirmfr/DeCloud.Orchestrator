using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services;

namespace Orchestrator.Controllers;

/// <summary>
/// API endpoints for Smart Port Allocation (direct TCP/UDP access to VMs)
/// </summary>
[ApiController]
[Authorize]
[Route("api/vms/{vmId}/direct-access")]
public class VmDirectAccessController : ControllerBase
{
    private readonly DirectAccessService _directAccessService;
    private readonly DataStore _dataStore;
    private readonly ILogger<VmDirectAccessController> _logger;

    public VmDirectAccessController(
        DirectAccessService directAccessService,
        DataStore dataStore,
        ILogger<VmDirectAccessController> logger)
    {
        _directAccessService = directAccessService;
        _dataStore = dataStore;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    /// <summary>
    /// Verify the authenticated caller owns the VM (admins bypass).
    /// Returns null when authorized, otherwise the ActionResult to return.
    /// </summary>
    private async Task<ActionResult?> AuthorizeVmAccessAsync(string vmId)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { error = "User not authenticated" });

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
            return NotFound(new { error = "VM not found" });

        if (vm.OwnerId != userId && !User.IsInRole("Admin"))
            return Forbid();

        return null;
    }

    /// <summary>
    /// Get direct access information for a VM
    /// </summary>
    /// <param name="vmId">VM ID</param>
    [HttpGet]
    public async Task<ActionResult<DirectAccessInfoResponse>> GetDirectAccessInfo(string vmId)
    {
        if (await AuthorizeVmAccessAsync(vmId) is { } denied) return denied;

        var info = await _directAccessService.GetDirectAccessInfoAsync(vmId);
        if (info == null)
        {
            return NotFound(new { error = "VM not found or direct access not configured" });
        }

        return Ok(info);
    }

    /// <summary>
    /// Allocate a port for direct access to a VM
    /// </summary>
    /// <param name="vmId">VM ID</param>
    /// <param name="request">Port allocation request</param>
    [HttpPost("ports")]
    public async Task<ActionResult<AllocatePortResponse>> AllocatePort(
        string vmId,
        [FromBody] AllocatePortRequest request)
    {
        if (await AuthorizeVmAccessAsync(vmId) is { } denied) return denied;

        _logger.LogInformation(
            "Allocating port {VmPort} ({Protocol}) for VM {VmId}",
            request.VmPort, request.Protocol, vmId);

        var result = await _directAccessService.AllocatePortAsync(
            vmId,
            request.VmPort,
            request.Protocol,
            request.Label);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Remove a port mapping from a VM
    /// </summary>
    /// <param name="vmId">VM ID</param>
    /// <param name="vmPort">VM port to remove</param>
    [HttpDelete("ports/{vmPort}")]
    public async Task<ActionResult> RemovePort(string vmId, int vmPort)
    {
        if (await AuthorizeVmAccessAsync(vmId) is { } denied) return denied;

        _logger.LogInformation("Removing port {VmPort} from VM {VmId}", vmPort, vmId);

        var success = await _directAccessService.RemovePortAsync(vmId, vmPort);
        if (!success)
        {
            return NotFound(new { error = "Port mapping not found" });
        }

        return NoContent();
    }

    /// <summary>
    /// Quick-add a common service (SSH, MySQL, etc.)
    /// </summary>
    /// <param name="vmId">VM ID</param>
    /// <param name="request">Service name (e.g., "ssh", "mysql", "minecraft")</param>
    [HttpPost("quick-add")]
    public async Task<ActionResult<AllocatePortResponse>> QuickAddService(
        string vmId,
        [FromBody] QuickAddServiceRequest request)
    {
        if (await AuthorizeVmAccessAsync(vmId) is { } denied) return denied;

        _logger.LogInformation(
            "Quick-adding service {ServiceName} for VM {VmId}",
            request.ServiceName, vmId);

        var result = await _directAccessService.QuickAddServiceAsync(
            vmId,
            request.ServiceName);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Get list of available quick-add services
    /// </summary>
    [HttpGet("services")]
    public ActionResult<Dictionary<string, object>> GetAvailableServices()
    {
        var services = CommonServices.Templates.Select(kvp => new
        {
            name = kvp.Key,
            port = kvp.Value.Port,
            protocol = kvp.Value.Protocol.ToString(),
            label = kvp.Value.Label
        }).ToList();

        return Ok(new { services });
    }
}
