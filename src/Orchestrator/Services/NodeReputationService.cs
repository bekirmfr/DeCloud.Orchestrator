using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Tracks node reputation metrics: uptime, VMs hosted, success rate
/// </summary>
public interface INodeReputationService
{
    /// <summary>
    /// Update uptime percentage based on heartbeat history
    /// Called during heartbeat processing
    /// </summary>
    Task UpdateUptimeAsync(string nodeId);

    /// <summary>
    /// Increment VMs hosted count when a VM is assigned to a node
    /// </summary>
    Task IncrementVmsHostedAsync(string nodeId);

    /// <summary>
    /// Increment successful completions when a VM is cleanly terminated
    /// </summary>
    Task IncrementSuccessfulCompletionsAsync(string nodeId);

    /// <summary>
    /// Calculate uptime for all nodes (background task)
    /// </summary>
    Task RecalculateAllUptimesAsync();
}

public class NodeReputationService : INodeReputationService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<NodeReputationService> _logger;

    // Uptime calculation window (30 days)
    private static readonly TimeSpan UptimeWindow = TimeSpan.FromDays(30);

    // Expected heartbeat interval (with 10% tolerance)
    private static readonly TimeSpan ExpectedHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HeartbeatTolerance = TimeSpan.FromSeconds(20); // 15s + 5s grace

    public NodeReputationService(
        DataStore dataStore,
        ILogger<NodeReputationService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    /// <summary>
    /// Update uptime percentage based on recent heartbeat history
    /// Uses a rolling 30-day window
    /// </summary>
    public async Task UpdateUptimeAsync(string nodeId)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
        {
            _logger.LogWarning("Cannot update uptime for non-existent node {NodeId}", nodeId);
            return;
        }

        try
        {
            var uptime = CalculateUptime(node);
            node.UptimePercentage = uptime;

            await _dataStore.SaveNodeAsync(node);

            _logger.LogDebug(
                "Updated uptime for node {NodeId}: {Uptime:F2}%",
                nodeId, uptime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update uptime for node {NodeId}", nodeId);
        }
    }

    /// <summary>
    /// Calculate uptime based on heartbeat history
    /// </summary>
    private double CalculateUptime(Node node)
    {
        var now = DateTime.UtcNow;
        var windowStart = now - UptimeWindow;

        // If node just registered, use time since registration
        var effectiveStart = node.RegisteredAt > windowStart
            ? node.RegisteredAt
            : windowStart;

        var totalTime = (now - effectiveStart).TotalSeconds;

        // If node is brand new (< 1 hour), return 100% until we have data
        if (totalTime < 3600)
        {
            return 100.0;
        }

        // Calculate expected heartbeats
        var expectedHeartbeats = (int)(totalTime / ExpectedHeartbeatInterval.TotalSeconds);

        // Calculate downtime windows
        var downtimeSeconds = CalculateDowntime(node, effectiveStart, now);

        // Uptime = (total time - downtime) / total time
        var uptimeSeconds = Math.Max(0, totalTime - downtimeSeconds);
        var uptimePercentage = (uptimeSeconds / totalTime) * 100.0;

        // Clamp between 0 and 100
        return Math.Clamp(uptimePercentage, 0.0, 100.0);
    }

    /// <summary>
    /// Calculate total downtime in seconds based on missed heartbeats
    /// Simplified approach: if last heartbeat is older than tolerance, count it as down
    /// </summary>
    private double CalculateDowntime(Node node, DateTime start, DateTime end)
    {
        // If node is currently offline, count time since last heartbeat as downtime
        if (node.Status == NodeStatus.Offline && node.LastHeartbeat.HasValue)
        {
            var timeSinceLastHeartbeat = end - node.LastHeartbeat.Value;
            if (timeSinceLastHeartbeat > HeartbeatTolerance)
            {
                // Node is down right now
                var downSince = node.LastHeartbeat.Value + HeartbeatTolerance;
                if (downSince < start)
                    downSince = start;

                return (end - downSince).TotalSeconds;
            }
        }

        // TODO: For more accurate tracking, implement heartbeat history storage
        // For now, assume if node is online, it's been mostly online
        return 0;
    }

    /// <summary>
    /// Increment VMs hosted count
    /// </summary>
    public async Task IncrementVmsHostedAsync(string nodeId)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
        {
            _logger.LogWarning("Cannot increment VMs hosted for non-existent node {NodeId}", nodeId);
            return;
        }

        try
        {
            node.TotalVmsHosted++;

            await _dataStore.SaveNodeAsync(node);

            _logger.LogDebug(
                "Incremented VMs hosted for node {NodeId}: {Total}",
                nodeId, node.TotalVmsHosted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to increment VMs hosted for node {NodeId}", nodeId);
        }
    }

    /// <summary>
    /// Increment successful VM completions
    /// </summary>
    public async Task IncrementSuccessfulCompletionsAsync(string nodeId)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
        {
            _logger.LogWarning("Cannot increment completions for non-existent node {NodeId}", nodeId);
            return;
        }

        try
        {
            node.SuccessfulVmCompletions++;

            await _dataStore.SaveNodeAsync(node);

            _logger.LogDebug(
                "Incremented successful completions for node {NodeId}: {Total}",
                nodeId, node.SuccessfulVmCompletions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to increment completions for node {NodeId}", nodeId);
        }
    }

    /// <summary>
    /// Recalculate uptime for all nodes
    /// Should be run periodically (e.g., every hour)
    /// </summary>
    public async Task RecalculateAllUptimesAsync()
    {
        try
        {
            var nodes = _dataStore.ActiveNodes.Values.ToList();

            _logger.LogInformation("Recalculating uptime for {Count} nodes", nodes.Count);

            foreach (var node in nodes)
            {
                await UpdateUptimeAsync(node.Id);
            }

            _logger.LogInformation("Completed uptime recalculation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recalculate all uptimes");
        }
    }
}
