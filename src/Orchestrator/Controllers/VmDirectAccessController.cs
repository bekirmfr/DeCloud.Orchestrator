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
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "User not authenticated"));

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "VM not found"));

        if (vm.OwnerId != userId && !User.IsInRole("Admin"))
            return Forbid();

        return null;
    }

    /// <summary>
    /// Get direct access information for a VM
    /// </summary>
    /// <param name="vmId">VM ID</param>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<DirectAccessInfoResponse>>> GetDirectAccessInfo(string vmId)
    {
        if (await AuthorizeVmAccessAsync(vmId) is { } denied) return denied;

        var info = await _directAccessService.GetDirectAccessInfoAsync(vmId);
        if (info == null)
        {
            return NotFound(ApiResponse<DirectAccessInfoResponse>.Fail("NOT_FOUND", "VM not found or direct access not configured"));
        }

        return Ok(ApiResponse<DirectAccessInfoResponse>.Ok(info));
    }

    /// <summary>
    /// Allocate a port for direct access to a VM
    /// </summary>
    /// <param name="vmId">VM ID</param>
    /// <param name="request">Port allocation request</param>
    [HttpPost("ports")]
    public async Task<ActionResult<ApiResponse<AllocatePortResponse>>> AllocatePort(
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
            return BadRequest(ApiResponse<AllocatePortResponse>.Fail("PORT_ALLOCATION_FAILED", result.Error ?? "Port allocation failed"));
        }

        return Ok(ApiResponse<AllocatePortResponse>.Ok(result));
    }

    /// <summary>
    /// Remove a port mapping from a VM
    /// </summary>
    /// <param name="vmId">VM ID</param>
    /// <param name="vmPort">VM port to remove</param>
    [HttpDelete("ports/{vmPort}")]
    public async Task<ActionResult<ApiResponse<object>>> RemovePort(string vmId, int vmPort)
    {
        if (await AuthorizeVmAccessAsync(vmId) is { } denied) return denied;

        _logger.LogInformation("Removing port {VmPort} from VM {VmId}", vmPort, vmId);

        var success = await _directAccessService.RemovePortAsync(vmId, vmPort);
        if (!success)
        {
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Port mapping not found"));
        }

        return NoContent();
    }

    /// <summary>
    /// Quick-add a common service (SSH, MySQL, etc.)
    /// </summary>
    /// <param name="vmId">VM ID</param>
    /// <param name="request">Service name (e.g., "ssh", "mysql", "minecraft")</param>
    [HttpPost("quick-add")]
    public async Task<ActionResult<ApiResponse<AllocatePortResponse>>> QuickAddService(
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
            return BadRequest(ApiResponse<AllocatePortResponse>.Fail("QUICK_ADD_FAILED", result.Error ?? "Quick-add failed"));
        }

        return Ok(ApiResponse<AllocatePortResponse>.Ok(result));
    }

    /// <summary>
    /// Get list of available quick-add services
    /// </summary>
    [HttpGet("services")]
    public ActionResult<ApiResponse<IReadOnlyList<DirectAccessServiceInfo>>> GetAvailableServices()
    {
        var services = CommonServices.Templates.Select(kvp => new DirectAccessServiceInfo(
            kvp.Key,
            kvp.Value.Port,
            kvp.Value.Protocol.ToString(),
            kvp.Value.Label)).ToList();

        return Ok(ApiResponse<IReadOnlyList<DirectAccessServiceInfo>>.Ok(services));
    }

    /// <summary>One quick-add service template exposed by GET .../services.</summary>
    public sealed record DirectAccessServiceInfo(string Name, int Port, string Protocol, string Label);
}