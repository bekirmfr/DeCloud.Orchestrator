using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Orchestrator.Hubs;
using Orchestrator.Models;

namespace Orchestrator.Services;

public interface IVmNotificationService
{
    Task BroadcastStatusAsync(string vmId, string? ownerId, VmStatus status, string? message = null);
    Task BroadcastServicesAsync(string vmId, IReadOnlyList<VmServiceModel> services);
    Task BroadcastAccessInfoAsync(string vmId, VmAccessInfo accessInfo);
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

    public async Task BroadcastStatusAsync(string vmId, string? ownerId, VmStatus status, string? message = null)
    {
        try
        {
            var payload = new
            {
                VmId = vmId,
                Status = status.ToString(),
                Message = message,
                Timestamp = DateTime.UtcNow
            };

            // Two audiences, one send: the cockpit watching THIS VM, and the
            // owner's dashboard watching ALL of theirs. OrchestratorHub has
            // exposed SubscribeToUser since it was written, but nothing ever
            // published to user:{id} — a subscription with no publisher.
            if (!string.IsNullOrEmpty(ownerId))
                await _hub.Clients.Groups($"vm:{vmId}", $"user:{ownerId}")
                    .SendAsync("VmStatusChanged", payload);
            else
                await _hub.Clients.Group($"vm:{vmId}")
                    .SendAsync("VmStatusChanged", payload);
        }
        catch (Exception ex)
        {
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

    public async Task BroadcastAccessInfoAsync(string vmId, VmAccessInfo accessInfo)
    {
        try
        {
            // VM-scoped only — the dashboard shows status, not SSH endpoints.
            // Owner-facing fields only: VncPassword stays server-side.
            await _hub.Clients.Group($"vm:{vmId}").SendAsync("VmAccessInfoUpdated", new
            {
                VmId = vmId,
                AccessInfo = new
                {
                    accessInfo.SshHost,
                    accessInfo.SshPort,
                    accessInfo.VncHost,
                    accessInfo.VncPort,
                    accessInfo.ConsoleWebSocketUrl
                },
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VmAccessInfoUpdated broadcast failed for {VmId}", vmId);
        }
    }
}