using Orchestrator.Persistence;
using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Background service that checks node health periodically
/// </summary>
public class NodeHealthMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NodeHealthMonitorService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);

    public NodeHealthMonitorService(
        IServiceProvider serviceProvider,
        ILogger<NodeHealthMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Node Health Monitor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var nodeService = scope.ServiceProvider.GetRequiredService<INodeService>();
                
                await nodeService.CheckNodeHealthAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking node health");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}

/// <summary>
/// Background service that schedules pending VMs
/// </summary>
public class VmSchedulerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VmSchedulerService> _logger;
    private readonly TimeSpan _scheduleInterval = TimeSpan.FromSeconds(10);

    public VmSchedulerService(
        IServiceProvider serviceProvider,
        ILogger<VmSchedulerService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VM Scheduler started");

        return;

        // Scheduling pending vms requires access to plain text passwords,
        // which we cannot do in this background service for security reasons.

        /*
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();

                await vmService.SchedulePendingVmsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling VMs");
            }

            await Task.Delay(_scheduleInterval, stoppingToken);
        }
        */
    }
}

/// <summary>
/// Background service to clean up deleted VMs
/// </summary>
public class CleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _deletedRetention = TimeSpan.FromDays(7);

    public CleanupService(
        IServiceProvider serviceProvider,
        ILogger<CleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cleanup Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dataStore = scope.ServiceProvider.GetRequiredService<Persistence.DataStore>();
                
                await TrimEventHistoryAsync(dataStore);
                await CleanupStaleCommandAcks(dataStore);
                await CleanupExpiredCommands(dataStore);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Handle commands that have been waiting for ack too long (5 minutes default)
    /// </summary>
    private async Task CleanupExpiredCommands(DataStore dataStore)
    {
        var timeout = TimeSpan.FromMinutes(5);
        var now = DateTime.UtcNow;

        var timedOutCommands = dataStore.PendingCommandAcks.Values
            .Where(cmd => cmd.Age > timeout)
            .ToList();

        foreach (var command in timedOutCommands)
        {
            _logger.LogWarning(
                "Command {CommandId} ({Type}) for resource {ResourceId} timed out after {Age}s - no acknowledgment received",
                command.CommandId, command.Type, command.TargetResourceId, command.Age.TotalSeconds);

            // Mark associated VM as error if it exists and is in a waiting state
            var vm = await dataStore.GetVmAsync(command.TargetResourceId);
            if (!string.IsNullOrEmpty(command.TargetResourceId) &&
                vm != null)
            {
                if (vm.Status == VmStatus.Provisioning || vm.Status == VmStatus.Deleting)
                {
                    var lifecycleManager = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
                    await lifecycleManager.TransitionAsync(
                        vm.Id,
                        VmStatus.Error,
                        TransitionContext.Timeout(
                            command.Type.ToString(),
                            $"{command.Type} timed out - no acknowledgment received from node after {timeout.TotalMinutes} minutes"));

                    _logger.LogWarning(
                        "Marked VM {VmId} as Error due to command timeout",
                        vm.Id);
                }
            }

            // Remove from tracking
            dataStore.PendingCommandAcks.TryRemove(command.CommandId, out _);
        }

        if (timedOutCommands.Any())
        {
            _logger.LogWarning(
                "Handled {Count} timed-out commands",
                timedOutCommands.Count);
        }
    }

    private Task CleanupStaleCommandAcks(DataStore dataStore)
    {
        var staleCommands = dataStore.PendingCommandAcks.Values
        .Where(cmd =>
            !string.IsNullOrEmpty(cmd.TargetResourceId) &&
            !dataStore.GetActiveVMs().Select(v => v.Id).Contains(cmd.TargetResourceId))
        .Select(cmd => cmd.CommandId)
        .ToList();

        foreach (var commandId in staleCommands)
        {
            dataStore.PendingCommandAcks.TryRemove(commandId, out _);
        }

        if (staleCommands.Any())
        {
            _logger.LogInformation(
                "Cleaned up {Count} stale command acknowledgment entries",
                staleCommands.Count);
        }

        return Task.CompletedTask;
    }

    private Task TrimEventHistoryAsync(Persistence.DataStore dataStore)
    {
        // Event history is already bounded in DataStore, but we can do additional cleanup here
        return Task.CompletedTask;
    }
}
