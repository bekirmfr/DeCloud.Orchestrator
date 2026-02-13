using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class VmsController : ControllerBase
{
    private readonly IVmService _vmService;
    private readonly INodeService _nodeService;
    private readonly DataStore _dataStore;
    private readonly ILogger<VmsController> _logger;

    public VmsController(
        IVmService vmService,
        INodeService nodeService,
        DataStore dataStore,
        ILogger<VmsController> logger)
    {
        _vmService = vmService;
        _nodeService = nodeService;
        _dataStore = dataStore;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    /// <summary>
    /// Create a new VM
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<CreateVmResponse>>> Create([FromBody] CreateVmRequest request)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<CreateVmResponse>.Fail("UNAUTHORIZED", "User not authenticated"));
        }

        try
        {
            // Support targeted node deployment (user can select specific node from marketplace)
            var response = await _vmService.CreateVmAsync(userId, request, request.NodeId);

            if (string.IsNullOrEmpty(response.VmId))
            {
                return BadRequest(ApiResponse<CreateVmResponse>.Fail("CREATE_ERROR", response.Message));
            }

            return Ok(ApiResponse<CreateVmResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating VM");
            return BadRequest(ApiResponse<CreateVmResponse>.Fail("CREATE_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// List VMs for the authenticated user
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<VmSummary>>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool sortDesc = false)
    {
        var userId = GetUserId();

        var filters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(status)) filters["status"] = status;

        var queryParams = new ListQueryParams(page, pageSize, sortBy, sortDesc, search, filters);
        var result = await _vmService.ListVmsAsync(userId, queryParams);

        return Ok(ApiResponse<PagedResult<VmSummary>>.Ok(result));
    }

    /// <summary>
    /// Get a specific VM
    /// </summary>
    [HttpGet("{vmId}")]
    public async Task<ActionResult<ApiResponse<VmDetailResponse>>> Get(string vmId)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<VmDetailResponse>.Fail("NOT_FOUND", "VM not found"));
        }

        // Check ownership (unless admin)
        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        // Get host node info
        Node? hostNode = null;
        if (!string.IsNullOrEmpty(vm.NodeId))
        {
            hostNode = await _dataStore.GetNodeAsync(vm.NodeId);
        }

        // Redact sensitive labels before returning (secrets are only needed by node agent)
        var safeLabels = new Dictionary<string, string>(vm.Labels.Count);
        foreach (var kvp in vm.Labels)
        {
            safeLabels[kvp.Key] = SensitiveLabelKeys.Contains(kvp.Key) ? "[REDACTED]" : kvp.Value;
        }
        vm.Labels = safeLabels;

        return Ok(ApiResponse<VmDetailResponse>.Ok(new VmDetailResponse(vm, hostNode)));
    }

    private static readonly HashSet<string> SensitiveLabelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "wireguard-private-key",
        "custom:cloud-init-vars",
        "template:cloud-init-vars"
    };

    /// <summary>
    /// Perform an action on a VM (start, stop, restart, etc.)
    /// </summary>
    [HttpPost("{vmId}/action")]
    public async Task<ActionResult<ApiResponse<bool>>> Action(string vmId, [FromBody] VmActionRequest request)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        // Validate action based on current state
        var validAction = (request.Action, vm.Status) switch
        {
            (VmAction.Start, VmStatus.Stopped) => true,
            (VmAction.Stop, VmStatus.Running) => true,
            (VmAction.ForceStop, VmStatus.Running) => true,
            (VmAction.Restart, VmStatus.Running) => true,
            (VmAction.Pause, VmStatus.Running) => true,
            (VmAction.Resume, VmStatus.Running) => vm.PowerState == VmPowerState.Paused,
            _ => false
        };

        if (!validAction)
        {
            return BadRequest(ApiResponse<bool>.Fail(
                "INVALID_ACTION",
                $"Cannot {request.Action} a VM in {vm.Status} state"));
        }

        var success = await _vmService.PerformVmActionAsync(vmId, request.Action, userId);
        if (!success)
        {
            return BadRequest(ApiResponse<bool>.Fail("ACTION_FAILED", "Failed to perform action"));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Delete a VM
    /// </summary>
    [HttpDelete("{vmId}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(string vmId)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        var success = await _vmService.DeleteVmAsync(vmId, userId);
        if (!success)
        {
            return BadRequest(ApiResponse<bool>.Fail("DELETE_FAILED", "Failed to delete VM"));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Get VM metrics
    /// </summary>
    [HttpGet("{vmId}/metrics")]
    public async Task<ActionResult<ApiResponse<VmMetrics>>> GetMetrics(string vmId)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<VmMetrics>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        if (vm.LatestMetrics == null)
        {
            return NotFound(ApiResponse<VmMetrics>.Fail("NO_METRICS", "No metrics available"));
        }

        return Ok(ApiResponse<VmMetrics>.Ok(vm.LatestMetrics));
    }

    /// <summary>
    /// Get console/terminal access info
    /// </summary>
    [HttpGet("{vmId}/console")]
    public async Task<ActionResult<ApiResponse<VmConsoleResponse>>> GetConsole(string vmId)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<VmConsoleResponse>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        if (vm.Status != VmStatus.Running)
        {
            return BadRequest(ApiResponse<VmConsoleResponse>.Fail("NOT_RUNNING", "VM is not running"));
        }

        // Return WebSocket URL for terminal
        var wsUrl = $"/hub/orchestrator?vmId={vmId}";

        return Ok(ApiResponse<VmConsoleResponse>.Ok(new VmConsoleResponse(
            vmId,
            wsUrl,
            vm.AccessInfo?.SshHost,
            vm.AccessInfo?.SshPort ?? 22,
            vm.AccessInfo?.VncHost,
            vm.AccessInfo?.VncPort ?? 5900
        )));
    }

    /// <summary>
    /// Store the encrypted password for a VM.
    /// Called by dashboard after user encrypts the password client-side.
    /// </summary>
    [HttpPost("{vmId}/secure-password")]
    public async Task<ActionResult<ApiResponse<bool>>> SecurePassword(
        string vmId,
        [FromBody] SecurePasswordRequest request)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ApiResponse<bool>.Fail("UNAUTHORIZED", "Not authenticated"));

        var result = await _vmService.SecurePasswordAsync(vmId, userId, request.EncryptedPassword);

        if (!result)
            return BadRequest(ApiResponse<bool>.Fail("SECURE_FAILED", "Failed to secure password"));

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Get encrypted password for decryption client-side.
    /// </summary>
    [HttpGet("{vmId}/encrypted-password")]
    public async Task<ActionResult<ApiResponse<EncryptedPasswordResponse>>> GetEncryptedPassword(string vmId)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null || vm.OwnerId != userId)
            return NotFound();

        if (string.IsNullOrEmpty(vm.Spec.WalletEncryptedPassword))
        {
            return BadRequest(ApiResponse<EncryptedPasswordResponse>.Fail("FETCH_FAILED", "Failed to fetch encrypted password"));
        }

        // Use positional constructor for the record
        return Ok(ApiResponse<EncryptedPasswordResponse>.Ok(
            new EncryptedPasswordResponse(vm.Spec.WalletEncryptedPassword, true)));
    }
}

// DTOs
public record SecurePasswordRequest(string EncryptedPassword);
public record EncryptedPasswordResponse(string? EncryptedPassword, bool IsSecured);

public record VmConsoleResponse(
    string VmId,
    string WebSocketUrl,
    string? SshHost,
    int SshPort,
    string? VncHost,
    int VncPort
);