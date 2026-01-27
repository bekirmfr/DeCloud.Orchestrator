using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Persistence;
using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Controllers;

/// <summary>
/// API controller for central ingress management.
/// Handles automatic subdomain assignment and routing for *.{baseDomain}
/// </summary>
[ApiController]
[Route("api/central-ingress")]
public class CentralIngressController : ControllerBase
{
    private readonly ICentralIngressService _ingressService;
    private readonly DataStore _dataStore;
    private readonly ILogger<CentralIngressController> _logger;

    public CentralIngressController(
        ICentralIngressService ingressService,
        DataStore dataStore,
        ILogger<CentralIngressController> logger)
    {
        _ingressService = ingressService;
        _dataStore = dataStore;
        _logger = logger;
    }

    /// <summary>
    /// Get central ingress status
    /// </summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<CentralIngressStatusResponse>>> GetStatus()
    {
        var status = await _ingressService.GetStatusAsync();
        return Ok(ApiResponse<CentralIngressStatusResponse>.Ok(status));
    }

    /// <summary>
    /// Get ingress configuration for a VM
    /// </summary>
    [HttpGet("vm/{vmId}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<VmIngressResponse>>> GetVmIngress(string vmId)
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;

        // Get VM and verify ownership
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            return NotFound(ApiResponse<VmIngressResponse>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        // Get route info
        var route = await _ingressService.GetRouteAsync(vmId);
        string? nodePublicIp = null;

        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        if (!string.IsNullOrEmpty(vm.NodeId) && node != null)
        {
            nodePublicIp = node.PublicIp;
        }

        var response = new VmIngressResponse(
            VmId: vm.Id,
            VmName: vm.Name,
            DefaultUrl: route?.Status == CentralRouteStatus.Active ? route.PublicUrl : _ingressService.GetVmUrl(vm),
            DefaultPort: route?.TargetPort ?? vm.IngressConfig?.DefaultPort ?? 80,
            DefaultEnabled: route?.Status == CentralRouteStatus.Active,
            CustomDomains: vm.IngressConfig?.CustomDomains ?? new List<string>(),
            NodePublicIp: nodePublicIp
        );

        return Ok(ApiResponse<VmIngressResponse>.Ok(response));
    }

    /// <summary>
    /// Enable/register the default subdomain for a VM
    /// </summary>
    [HttpPost("vm/{vmId}/enable")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<VmIngressResponse>>> EnableVmIngress(
        string vmId,
        [FromBody] SetVmIngressPortRequest? request = null)
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;

        // Validate
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            return NotFound(ApiResponse<VmIngressResponse>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (!_ingressService.IsEnabled)
        {
            return BadRequest(ApiResponse<VmIngressResponse>.Fail(
                "INGRESS_DISABLED", "Central ingress is not enabled on this platform"));
        }

        if (vm.Status != VmStatus.Running)
        {
            return BadRequest(ApiResponse<VmIngressResponse>.Fail(
                "VM_NOT_RUNNING", "VM must be running to enable ingress"));
        }

        _logger.LogInformation("Enabling central ingress for VM {VmId}", vmId);

        var route = await _ingressService.RegisterVmAsync(vmId, request?.Port);

        if (route == null)
        {
            return StatusCode(500, ApiResponse<VmIngressResponse>.Fail(
                "REGISTRATION_FAILED", "Failed to register VM for ingress"));
        }

        string? nodePublicIp = null;
        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        if (!string.IsNullOrEmpty(vm.NodeId) && node != null)
        {
            nodePublicIp = node.PublicIp;
        }

        var response = new VmIngressResponse(
            VmId: vm.Id,
            VmName: vm.Name,
            DefaultUrl: route.PublicUrl,
            DefaultPort: route.TargetPort,
            DefaultEnabled: true,
            CustomDomains: vm.IngressConfig?.CustomDomains ?? new List<string>(),
            NodePublicIp: nodePublicIp
        );

        return Ok(ApiResponse<VmIngressResponse>.Ok(response));
    }

    /// <summary>
    /// Disable the default subdomain for a VM
    /// </summary>
    [HttpPost("vm/{vmId}/disable")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<VmIngressResponse>>> DisableVmIngress(string vmId)
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            return NotFound(ApiResponse<VmIngressResponse>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        _logger.LogInformation("Disabling central ingress for VM {VmId}", vmId);

        await _ingressService.UnregisterVmAsync(vmId);

        var response = new VmIngressResponse(
            VmId: vm.Id,
            VmName: vm.Name,
            DefaultUrl: null,
            DefaultPort: vm.IngressConfig?.DefaultPort ?? 80,
            DefaultEnabled: false,
            CustomDomains: vm.IngressConfig?.CustomDomains ?? new List<string>(),
            NodePublicIp: null
        );

        return Ok(ApiResponse<VmIngressResponse>.Ok(response));
    }

    /// <summary>
    /// Update the target port for a VM's default subdomain
    /// </summary>
    [HttpPatch("vm/{vmId}/port")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<VmIngressResponse>>> UpdateVmIngressPort(
        string vmId,
        [FromBody] SetVmIngressPortRequest request)
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            return NotFound(ApiResponse<VmIngressResponse>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (request.Port < 1 || request.Port > 65535)
        {
            return BadRequest(ApiResponse<VmIngressResponse>.Fail(
                "INVALID_PORT", "Port must be between 1 and 65535"));
        }

        _logger.LogInformation("Updating ingress port for VM {VmId} to {Port}", vmId, request.Port);

        var success = await _ingressService.UpdatePortAsync(vmId, request.Port);
        var route = await _ingressService.GetRouteAsync(vmId);

        var response = new VmIngressResponse(
            VmId: vm.Id,
            VmName: vm.Name,
            DefaultUrl: route?.PublicUrl,
            DefaultPort: request.Port,
            DefaultEnabled: route?.Status == CentralRouteStatus.Active,
            CustomDomains: vm.IngressConfig?.CustomDomains ?? new List<string>(),
            NodePublicIp: null
        );

        return Ok(ApiResponse<VmIngressResponse>.Ok(response));
    }

    /// <summary>
    /// Get all active routes (admin only or user's own)
    /// </summary>
    [HttpGet("routes")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<List<CentralIngressRoute>>>> GetAllRoutes()
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;
        var isAdmin = User.IsInRole("Admin");

        var routes = await _ingressService.GetAllRoutesAsync();

        // Filter to user's routes unless admin
        if (!isAdmin)
        {
            routes = routes.Where(r =>
                r.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return Ok(ApiResponse<List<CentralIngressRoute>>.Ok(routes));
    }

    /// <summary>
    /// Force reload all routes (admin only)
    /// </summary>
    [HttpPost("reload")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ApiResponse<bool>>> ReloadAll()
    {
        _logger.LogInformation("Admin triggered central ingress reload");

        var success = await _ingressService.ReloadAllAsync();

        if (!success)
        {
            return StatusCode(500, ApiResponse<bool>.Fail("RELOAD_FAILED", "Failed to reload routes"));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Preview what subdomain a VM would get (without registering)
    /// </summary>
    [HttpGet("preview/{vmId}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<object>>> PreviewSubdomain(string vmId)
    {
        if (!_ingressService.IsEnabled)
        {
            return BadRequest(ApiResponse<object>.Fail("INGRESS_DISABLED", "Central ingress not enabled"));
        }

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            return NotFound(ApiResponse<object>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        var subdomain = _ingressService.GenerateSubdomain(vm);

        return Ok(ApiResponse<object>.Ok(new
        {
            vmId = vm.Id,
            vmName = vm.Name,
            subdomain = subdomain,
            url = $"https://{subdomain}",
            baseDomain = _ingressService.BaseDomain
        }));
    }
}