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
    /// </summary>
    Task<VmHoldResult> SetVmComplianceHoldAsync(string vmId, bool held);

    Task<bool> DeleteVmAsync(string vmId, string? userId = null);
    Task<bool> UpdateVmStatusAsync(string vmId, VmStatus status, string? message = null);
    Task<bool> UpdateVmMetricsAsync(string vmId, VmMetrics metrics);
    //Task SchedulePendingVmsAsync();
    Task<bool> SecurePasswordAsync(string vmId, string userId, string encryptedPassword);
}

/// <summary>Outcome of an admin VM compliance-hold change.</summary>
public record VmHoldResult(bool Success, string? Error, string? OwnerId, bool VmStopped);
