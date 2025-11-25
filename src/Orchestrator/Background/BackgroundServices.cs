using Orchestrator.Services;

namespace Orchestrator.Background;

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
                
                await ((NodeService)nodeService).CheckNodeHealthAsync();
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
    }
}

/// <summary>
/// Background service for billing calculations
/// </summary>
public class BillingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BillingService> _logger;
    private readonly TimeSpan _billingInterval = TimeSpan.FromMinutes(1);

    public BillingService(
        IServiceProvider serviceProvider,
        ILogger<BillingService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Billing Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dataStore = scope.ServiceProvider.GetRequiredService<Data.OrchestratorDataStore>();
                
                await ProcessBillingAsync(dataStore);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing billing");
            }

            await Task.Delay(_billingInterval, stoppingToken);
        }
    }

    private Task ProcessBillingAsync(Data.OrchestratorDataStore dataStore)
    {
        var now = DateTime.UtcNow;
        
        foreach (var vm in dataStore.VirtualMachines.Values)
        {
            if (vm.Status == Models.VmStatus.Running && vm.StartedAt.HasValue)
            {
                var runtime = now - vm.StartedAt.Value;
                var hourlyRate = vm.BillingInfo.HourlyRateCrypto;
                
                // Calculate billing since last check
                var lastBilling = vm.BillingInfo.LastBillingAt ?? vm.StartedAt.Value;
                var billableDuration = now - lastBilling;
                var billableHours = (decimal)billableDuration.TotalHours;
                
                if (billableHours > 0)
                {
                    var amount = hourlyRate * billableHours;
                    vm.BillingInfo.TotalBilled += amount;
                    vm.BillingInfo.TotalRuntime += billableDuration;
                    vm.BillingInfo.LastBillingAt = now;
                }
            }
        }

        return Task.CompletedTask;
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
                var dataStore = scope.ServiceProvider.GetRequiredService<Data.OrchestratorDataStore>();
                
                await CleanupDeletedVmsAsync(dataStore);
                await TrimEventHistoryAsync(dataStore);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    private Task CleanupDeletedVmsAsync(Data.OrchestratorDataStore dataStore)
    {
        var cutoff = DateTime.UtcNow - _deletedRetention;
        var toRemove = dataStore.VirtualMachines.Values
            .Where(v => v.Status == Models.VmStatus.Deleted && v.UpdatedAt < cutoff)
            .Select(v => v.Id)
            .ToList();

        foreach (var vmId in toRemove)
        {
            if (dataStore.VirtualMachines.TryRemove(vmId, out _))
            {
                _logger.LogInformation("Cleaned up deleted VM: {VmId}", vmId);
            }
        }

        return Task.CompletedTask;
    }

    private Task TrimEventHistoryAsync(Data.OrchestratorDataStore dataStore)
    {
        // Event history is already bounded in DataStore, but we can do additional cleanup here
        return Task.CompletedTask;
    }
}
