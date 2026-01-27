using Orchestrator.Models;
using Orchestrator.Persistence;

/// <summary>
/// Background service that prunes stale data from in-memory caches.
/// Runs every 5 minutes to remove offline nodes and stopped VMs.
/// </summary>
public class StatePruningService : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<StatePruningService> _logger;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    public StatePruningService(
        DataStore dataStore,
        ILogger<StatePruningService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("State pruning service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PruneInterval, stoppingToken);
                await PruneStaleDataAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in state pruning service");
            }
        }

        _logger.LogInformation("State pruning service stopped");
    }

    private async Task PruneStaleDataAsync(CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var prunedNodes = 0;
        var prunedVMs = 0;
        var prunedUsage = 0;

        // Prune offline nodes (heartbeat > 5 minutes ago)
        var nodeOnlineCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        var offlineNodes = (await _dataStore.GetAllNodesAsync(NodeStatus.Offline))
            .Where(n => n.LastHeartbeat < nodeOnlineCutoff)
            .Select(n => n.Id)
            .ToList();

        foreach (var nodeId in offlineNodes)
        {
            if (_dataStore.ActiveNodes.TryRemove(nodeId, out _))
            {
                prunedNodes++;
            }
        }

        // Prune stopped/deleted VMs
        var stoppedVMs = _dataStore.GetActiveVMs()
            .Where(vm => vm.Status == VmStatus.Stopped ||
                        vm.Status == VmStatus.Deleted)
            .Select(vm => vm.Id)
            .ToList();

        foreach (var vmId in stoppedVMs)
        {
            if (_dataStore.ActiveVMs.TryRemove(vmId, out _))
            {
                prunedVMs++;
            }
        }

        // Prune old settled usage records (>30 days)
        var usageAgeCutoff = DateTime.UtcNow - TimeSpan.FromDays(30);
        var oldUsage = _dataStore.UnsettledUsage.Values
            .Where(u => u.SettledOnChain || u.CreatedAt < usageAgeCutoff)
            .Select(u => u.Id)
            .ToList();

        foreach (var usageId in oldUsage)
        {
            if (_dataStore.UnsettledUsage.TryRemove(usageId, out _))
            {
                prunedUsage++;
            }
        }

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        if (prunedNodes > 0 || prunedVMs > 0 || prunedUsage > 0)
        {
            _logger.LogInformation(
                "Pruned stale data in {Elapsed}ms: {Nodes} nodes, {VMs} VMs, {Usage} usage records",
                elapsed, prunedNodes, prunedVMs, prunedUsage);
        }
    }
}