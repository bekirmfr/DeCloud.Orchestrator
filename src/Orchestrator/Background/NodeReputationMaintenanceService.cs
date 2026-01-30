using Orchestrator.Services;

namespace Orchestrator.Background;

/// <summary>
/// Background service that periodically recalculates node reputation metrics
/// Runs every hour to update uptimes for all nodes
/// </summary>
public class NodeReputationMaintenanceService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NodeReputationMaintenanceService> _logger;

    // Recalculate every hour
    private static readonly TimeSpan RecalculationInterval = TimeSpan.FromHours(1);

    public NodeReputationMaintenanceService(
        IServiceProvider serviceProvider,
        ILogger<NodeReputationMaintenanceService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Node Reputation Maintenance Service started");

        // Wait 5 minutes before first run (let orchestrator start up)
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting node reputation recalculation");

                using var scope = _serviceProvider.CreateScope();
                var reputationService = scope.ServiceProvider.GetRequiredService<INodeReputationService>();

                await reputationService.RecalculateAllUptimesAsync();

                _logger.LogInformation("Node reputation recalculation completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during node reputation maintenance");
            }

            // Wait for next interval
            await Task.Delay(RecalculationInterval, stoppingToken);
        }

        _logger.LogInformation("Node Reputation Maintenance Service stopped");
    }
}
