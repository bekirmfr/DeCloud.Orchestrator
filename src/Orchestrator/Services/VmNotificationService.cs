using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Orchestrator.Hubs;

namespace Orchestrator.Services;

public interface IVmNotificationService
{
    Task BroadcastStatusAsync(string vmId, VmStatus status, string? message = null);
    Task BroadcastServicesAsync(string vmId, IReadOnlyList<VmServiceModel> services);
}

/// The ONE place a VM status change is pushed to SignalR clients. Both the
/// optimistic in-flight write (VmService.PerformVmActionAsync) and the confirmed
/// transition (VmLifecycleManager.TransitionAsync) call this — so every status
/// change reaches the cockpit, whether it goes through the lifecycle machinery or not.
public class VmNotificationService : IVmNotificationService
{
    private readonly IHubContext<OrchestratorHub> _hub;
    private readonly ILogger<VmNotificationService> _logger;

    public VmNotificationService(IHubContext<OrchestratorHub> hub, ILogger<VmNotificationService> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task BroadcastStatusAsync(string vmId, VmStatus status, string? message = null)
    {
        try
        {
            await _hub.Clients.Group($"vm:{vmId}").SendAsync("VmStatusChanged", new
            {
                VmId = vmId,
                Status = status.ToString(),   // NAME (client tolerates numeric too)
                Message = message,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            // Never let a notification failure affect a state change. Best-effort.
            _logger.LogWarning(ex, "VmStatusChanged broadcast failed for {VmId}", vmId);
        }
    }
    public async Task BroadcastServicesAsync(string vmId, IReadOnlyList<VmServiceModel> services)
    {
        try
        {
            // Owner-facing projection only. VmServiceModel also carries internals
            // (ExecCommand, LastSuccessBody, ConsecutiveFailures) that the cockpit
            // has no use for and shouldn't receive.
            await _hub.Clients.Group($"vm:{vmId}").SendAsync("VmServicesUpdated", new
            {
                VmId = vmId,
                Services = services.Select(s => new
                {
                    s.Name,
                    s.Port,
                    s.Protocol,
                    Status = s.Status.ToString(),   // NAME, as with VmStatusChanged
                    s.StatusMessage,
                    s.ReadyAt
                }).ToList(),
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VmServicesUpdated broadcast failed for {VmId}", vmId);
        }
    }
}