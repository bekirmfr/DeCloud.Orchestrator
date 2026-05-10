using Orchestrator.Models;

namespace Orchestrator.Interfaces;

public interface IVmService
{
    Task<CreateVmResponse> CreateVmAsync(string userId, CreateVmRequest request, string? targetNodeId = null);
    Task<List<VirtualMachine>> GetVmsByUserAsync(string userId, VmStatus? statusFilter = null);
    Task<PagedResult<VmSummary>> ListVmsAsync(string? userId, ListQueryParams queryParams);
    Task<bool> PerformVmActionAsync(string vmId, VmAction action, string? userId = null);

    Task<bool> DeleteVmAsync(string vmId, string? userId = null);
    Task<bool> UpdateVmStatusAsync(string vmId, VmStatus status, string? message = null);
    Task<bool> UpdateVmMetricsAsync(string vmId, VmMetrics metrics);
    //Task SchedulePendingVmsAsync();
    Task<bool> SecurePasswordAsync(string vmId, string userId, string encryptedPassword);
}
