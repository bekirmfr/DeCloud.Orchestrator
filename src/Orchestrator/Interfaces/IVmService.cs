using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Orchestrator.Models;

namespace Orchestrator.Interfaces;

public interface IVmService
{
    Task<CreateVmResponse> CreateVmAsync(string userId, CreateVmRequest request, string? targetNodeId = null);
    Task<List<VirtualMachine>> GetVmsByUserAsync(string userId, VmStatus? statusFilter = null);
    Task<PagedResult<VmSummaryDto>> ListVmsAsync(string? userId, ListQueryParams queryParams);
    Task<bool> PerformVmActionAsync(string vmId, VmAction action, string? userId = null);

    /// <summary>
    /// Admin compliance hold on a single VM. held=true stops it (if running) and sets
    /// VirtualMachine.ComplianceHold so the owner cannot restart it; held=false lifts
    /// the hold (leaves it stopped — owner may start). Refuses system VMs.
    /// <paramref name="reference"/> is the abuse-report reference the hold is applied
    /// under; it is stored on ComplianceHoldReference when held=true and cleared when
    /// held=false. Null is allowed (a hold with no recorded cause cannot later be scanned).
    /// </summary>
    Task<VmHoldResult> SetVmComplianceHoldAsync(string vmId, bool held, string? reference = null);

    /// <summary>
    /// Issue a targeted CSAM hash-check command to the node hosting this VM (Phase 6
    /// pass 2b). Does NOT stamp ActiveCommand* — a scan is not a lifecycle command and
    /// must not occupy that single slot. Returns the issued command id (for the report's
    /// scan record) or null if the VM has no host node. The caller (EnforcementService)
    /// has already enforced the hold + reference precondition.
    /// </summary>
    Task<string?> RequestScanAsync(string vmId, CancellationToken ct = default);

    Task<bool> DeleteVmAsync(string vmId, string? userId = null);
    Task<bool> UpdateVmStatusAsync(string vmId, VmStatus status, string? message = null);
    Task<bool> UpdateVmMetricsAsync(string vmId, VmMetrics metrics);
    //Task SchedulePendingVmsAsync();
    Task<bool> SecurePasswordAsync(string vmId, string userId, string encryptedPassword);
}

/// <summary>Outcome of an admin VM compliance-hold change.</summary>
public record VmHoldResult(bool Success, string? Error, string? OwnerId, bool VmStopped);
