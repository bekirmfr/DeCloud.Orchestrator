using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services;

namespace Orchestrator.Controllers;

/// <summary>
/// Attestation API endpoints for VM liveness verification
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AttestationController : ControllerBase
{
    private readonly IAttestationService _attestationService;
    private readonly AttestationSchedulerService _schedulerService;
    private readonly DataStore _dataStore;
    private readonly ILogger<AttestationController> _logger;

    public AttestationController(
        IAttestationService attestationService,
        AttestationSchedulerService schedulerService,
        DataStore dataStore,
        ILogger<AttestationController> logger)
    {
        _attestationService = attestationService;
        _schedulerService = schedulerService;
        _dataStore = dataStore;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    /// <summary>
    /// Get attestation status for a specific VM
    /// </summary>
    [HttpGet("vms/{vmId}/status")]
    public async Task<ActionResult<ApiResponse<AttestationStatusResponse>>> GetVmStatus(string vmId)
    {
        var userId = GetUserId();

        // Verify user owns this VM
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
        {
            return NotFound(ApiResponse<AttestationStatusResponse>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if(!_dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            return NotFound(ApiResponse<AttestationStatusResponse>.Fail("NODE_NOT_FOUND", "Node not found"));
        }

        var nodeUser = _dataStore.Users.GetValueOrDefault(node.WalletAddress);

        var livenessState = _attestationService.GetLivenessState(vmId);
        var stats = await _attestationService.GetVmStatsAsync(vmId);

        var response = new AttestationStatusResponse
        {
            VmId = vmId,
            VmName = vm.Name,
            VmStatus = vm.Status.ToString(),
            AttestationEnabled = true,
            LastSuccessfulAttestation = livenessState?.LastSuccessfulAttestation,
            ConsecutiveSuccesses = livenessState?.ConsecutiveSuccesses ?? 0,
            ConsecutiveFailures = livenessState?.ConsecutiveFailures ?? 0,
            TotalChallenges = stats?.TotalChallenges ?? 0,
            SuccessRate = stats?.SuccessRate ?? 0,
            AverageResponseTimeMs = stats?.AverageResponseTimeMs ?? 0,
            BillingPaused = livenessState?.BillingPaused ?? false,
            BillingPauseReason = livenessState?.PauseReason,
            BillingPausedAt = livenessState?.PausedAt
        };

        return Ok(ApiResponse<AttestationStatusResponse>.Ok(response));
    }

    /// <summary>
    /// Get attestation history for a specific VM
    /// </summary>
    [HttpGet("vms/{vmId}/history")]
    public async Task<ActionResult<ApiResponse<List<AttestationRecordResponse>>>> GetVmHistory(
        string vmId,
        [FromQuery] int limit = 50,
        [FromQuery] DateTime? since = null)
    {
        var userId = GetUserId();

        // Verify user owns this VM
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
        {
            return NotFound(ApiResponse<List<AttestationRecordResponse>>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId)
        {
            return Forbid();
        }

        var records = await _dataStore.GetAttestationsAsync(vmId, limit, since);

        var response = records.Select(r => new AttestationRecordResponse
        {
            Id = r.Id,
            Timestamp = r.Timestamp,
            Success = r.Success,
            ResponseTimeMs = r.ResponseTimeMs,
            Errors = r.Errors,
            CpuCores = r.ReportedMetrics?.CpuCores,
            MemoryMb = r.ReportedMetrics?.MemoryKb / 1024,
            LoadAvg = r.ReportedMetrics?.LoadAvg1
        }).ToList();

        return Ok(ApiResponse<List<AttestationRecordResponse>>.Ok(response));
    }

    /// <summary>
    /// Trigger immediate attestation for a VM (user-initiated)
    /// Useful if user suspects issues with their VM
    /// </summary>
    [HttpPost("vms/{vmId}/verify")]
    public async Task<ActionResult<ApiResponse<AttestationVerificationResult>>> TriggerVerification(string vmId)
    {
        var userId = GetUserId();

        // Verify user owns this VM
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
        {
            return NotFound(ApiResponse<AttestationVerificationResult>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId)
        {
            return Forbid();
        }

        if (vm.Status != VmStatus.Running)
        {
            return BadRequest(ApiResponse<AttestationVerificationResult>.Fail(
                "VM_NOT_RUNNING",
                $"VM is not running (status: {vm.Status})"));
        }

        _logger.LogInformation(
            "User {UserId} triggered immediate attestation for VM {VmId}",
            userId, vmId);

        var result = await _schedulerService.TriggerImmediateAttestationAsync(vmId);

        return Ok(ApiResponse<AttestationVerificationResult>.Ok(result));
    }

    /// <summary>
    /// Get aggregated attestation stats for all user's VMs
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<UserAttestationSummary>>> GetUserSummary()
    {
        var userId = GetUserId();

        var userVms = _dataStore.VirtualMachines.Values
            .Where(vm => vm.OwnerId == userId && vm.VmType != VmType.Relay)
            .ToList();

        var summary = new UserAttestationSummary
        {
            TotalVms = userVms.Count,
            RunningVms = userVms.Count(vm => vm.Status == VmStatus.Running),
            VmsWithPausedBilling = 0,
            OverallSuccessRate = 0,
            VmStatuses = new List<VmAttestationBrief>()
        };

        foreach (var vm in userVms)
        {
            var state = _attestationService.GetLivenessState(vm.Id);
            var stats = await _attestationService.GetVmStatsAsync(vm.Id);

            if (state?.BillingPaused == true)
            {
                summary.VmsWithPausedBilling++;
            }

            summary.VmStatuses.Add(new VmAttestationBrief
            {
                VmId = vm.Id,
                VmName = vm.Name,
                Status = vm.Status.ToString(),
                SuccessRate = stats?.SuccessRate ?? 0,
                LastAttestation = state?.LastSuccessfulAttestation,
                BillingPaused = state?.BillingPaused ?? false
            });
        }

        // Calculate overall success rate
        var allStats = summary.VmStatuses.Where(s => s.SuccessRate > 0).ToList();
        if (allStats.Count > 0)
        {
            summary.OverallSuccessRate = allStats.Average(s => s.SuccessRate);
        }

        return Ok(ApiResponse<UserAttestationSummary>.Ok(summary));
    }
}

// Response DTOs
public class AttestationStatusResponse
{
    public string VmId { get; set; } = string.Empty;
    public string VmName { get; set; } = string.Empty;
    public string VmStatus { get; set; } = string.Empty;
    public bool AttestationEnabled { get; set; }
    public DateTime? LastSuccessfulAttestation { get; set; }
    public int ConsecutiveSuccesses { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int TotalChallenges { get; set; }
    public double SuccessRate { get; set; }
    public double AverageResponseTimeMs { get; set; }
    public bool BillingPaused { get; set; }
    public string? BillingPauseReason { get; set; }
    public DateTime? BillingPausedAt { get; set; }
}

public class AttestationRecordResponse
{
    public string Id { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool Success { get; set; }
    public double ResponseTimeMs { get; set; }
    public List<string> Errors { get; set; } = new();
    public int? CpuCores { get; set; }
    public long? MemoryMb { get; set; }
    public double? LoadAvg { get; set; }
}

public class UserAttestationSummary
{
    public int TotalVms { get; set; }
    public int RunningVms { get; set; }
    public int VmsWithPausedBilling { get; set; }
    public double OverallSuccessRate { get; set; }
    public List<VmAttestationBrief> VmStatuses { get; set; } = new();
}

public class VmAttestationBrief
{
    public string VmId { get; set; } = string.Empty;
    public string VmName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double SuccessRate { get; set; }
    public DateTime? LastAttestation { get; set; }
    public bool BillingPaused { get; set; }
}