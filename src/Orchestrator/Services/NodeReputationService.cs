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
    /// Record a failed heartbeat for tracking downtime
    /// Called when a heartbeat is expected but not received
    /// </summary>
    Task RecordFailedHeartbeatAsync(string nodeId);

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
    /// Also detects and records failed heartbeats
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
    /// Calculate uptime based on failed heartbeat history
    /// Uses last 30 days of failed heartbeat counts
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

        // Calculate expected heartbeats in the time window
        var expectedHeartbeats = (int)(totalTime / ExpectedHeartbeatInterval.TotalSeconds);

        // Sum failed heartbeats from last 30 days
        var failedHeartbeats = CountFailedHeartbeatsInWindow(node, effectiveStart, now);

        // Calculate successful heartbeats
        var successfulHeartbeats = Math.Max(0, expectedHeartbeats - failedHeartbeats);

        // Uptime = successful / expected × 100
        var uptimePercentage = expectedHeartbeats > 0
            ? (successfulHeartbeats / (double)expectedHeartbeats) * 100.0
            : 100.0;

        // Clamp between 0 and 100
        return Math.Clamp(uptimePercentage, 0.0, 100.0);
    }

    /// <summary>
    /// Count failed heartbeats within the time window
    /// </summary>
    private int CountFailedHeartbeatsInWindow(Node node, DateTime start, DateTime end)
    {
        if (node.FailedHeartbeatsByDay == null || node.FailedHeartbeatsByDay.Count == 0)
        {
            return 0;
        }

        var failedCount = 0;

        // Iterate through each day in the window
        for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
        {
            var dateKey = date.ToString("yyyy-MM-dd");
            if (node.FailedHeartbeatsByDay.TryGetValue(dateKey, out var count))
            {
                failedCount += count;
            }
        }

        return failedCount;
    }

    /// <summary>
    /// Track a failed heartbeat for the current day
    /// Called when a heartbeat is expected but not received
    /// </summary>
    public async Task RecordFailedHeartbeatAsync(string nodeId)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
        {
            return;
        }

        try
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            // Initialize dictionary if null
            node.FailedHeartbeatsByDay ??= new Dictionary<string, int>();

            // Increment counter for today
            if (node.FailedHeartbeatsByDay.ContainsKey(today))
            {
                node.FailedHeartbeatsByDay[today]++;
            }
            else
            {
                node.FailedHeartbeatsByDay[today] = 1;
            }

            // Clean up old entries (older than 30 days)
            CleanupOldHeartbeatData(node);

            await _dataStore.SaveNodeAsync(node);

            _logger.LogDebug(
                "Recorded failed heartbeat for node {NodeId} on {Date}: {Count}",
                nodeId, today, node.FailedHeartbeatsByDay[today]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record failed heartbeat for node {NodeId}", nodeId);
        }
    }

    /// <summary>
    /// Remove heartbeat data older than 30 days to keep dictionary size manageable
    /// </summary>
    private void CleanupOldHeartbeatData(Node node)
    {
        if (node.FailedHeartbeatsByDay == null || node.FailedHeartbeatsByDay.Count == 0)
        {
            return;
        }

        var cutoffDate = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");

        var oldKeys = node.FailedHeartbeatsByDay.Keys
            .Where(k => string.Compare(k, cutoffDate, StringComparison.Ordinal) < 0)
            .ToList();

        foreach (var key in oldKeys)
        {
            node.FailedHeartbeatsByDay.Remove(key);
        }

        if (oldKeys.Count > 0)
        {
            _logger.LogDebug(
                "Cleaned up {Count} old heartbeat entries for node {NodeId}",
                oldKeys.Count, node.Id);
        }
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
    /// Also detects and records failed heartbeats for offline nodes
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
                // Detect and record failed heartbeats for offline nodes
                await DetectAndRecordFailedHeartbeatsAsync(node);

                // Update uptime calculation
                await UpdateUptimeAsync(node.Id);
            }

            _logger.LogInformation("Completed uptime recalculation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recalculate all uptimes");
        }
    }

    /// <summary>
    /// Detect failed heartbeats for a node and record them by day
    /// Only records for currently offline nodes, tracks since last check to avoid double-counting
    /// </summary>
    private async Task DetectAndRecordFailedHeartbeatsAsync(Node node)
    {
        // Only check offline nodes
        if (node.Status != NodeStatus.Offline || !node.LastHeartbeat.HasValue)
        {
            // If node is online, update last check time
            if (node.Status == NodeStatus.Online)
            {
                node.LastFailedHeartbeatCheckAt = DateTime.UtcNow;
            }
            return;
        }

        var now = DateTime.UtcNow;
        var timeSinceLastHeartbeat = now - node.LastHeartbeat.Value;

        // If heartbeat is recent (within tolerance), no failures
        if (timeSinceLastHeartbeat <= HeartbeatTolerance)
        {
            return;
        }

        // Calculate when downtime started (last heartbeat + tolerance)
        var downtimeStart = node.LastHeartbeat.Value + HeartbeatTolerance;

        // If we've already checked recently, only count new failures
        if (node.LastFailedHeartbeatCheckAt.HasValue && node.LastFailedHeartbeatCheckAt.Value > downtimeStart)
        {
            downtimeStart = node.LastFailedHeartbeatCheckAt.Value;
        }

        // Calculate total missed heartbeats since last check
        var missedDuration = now - downtimeStart;
        var totalMissedHeartbeats = (int)(missedDuration.TotalSeconds / ExpectedHeartbeatInterval.TotalSeconds);

        if (totalMissedHeartbeats <= 0)
        {
            // Update check time even if no new failures
            node.LastFailedHeartbeatCheckAt = now;
            return;
        }

        // Initialize dictionary if null
        node.FailedHeartbeatsByDay ??= new Dictionary<string, int>();

        // Distribute missed heartbeats across days
        var currentDate = downtimeStart.Date;
        var endDate = now.Date;
        var remainingMissed = totalMissedHeartbeats;

        while (currentDate <= endDate && remainingMissed > 0)
        {
            var dateKey = currentDate.ToString("yyyy-MM-dd");

            // Calculate how much of this day was in the downtime window
            var dayStart = currentDate;
            var dayEnd = currentDate.AddDays(1);

            // Clamp to actual downtime window
            if (dayStart < downtimeStart) dayStart = downtimeStart;
            if (dayEnd > now) dayEnd = now;

            var secondsInDay = (dayEnd - dayStart).TotalSeconds;
            var missedInDay = (int)(secondsInDay / ExpectedHeartbeatInterval.TotalSeconds);

            if (missedInDay > 0)
            {
                // Add to existing count for this day
                if (!node.FailedHeartbeatsByDay.ContainsKey(dateKey))
                {
                    node.FailedHeartbeatsByDay[dateKey] = 0;
                }

                node.FailedHeartbeatsByDay[dateKey] += missedInDay;
                remainingMissed -= missedInDay;

                _logger.LogDebug(
                    "Recorded {Count} failed heartbeats for node {NodeId} on {Date}",
                    missedInDay, node.Id, dateKey);
            }

            currentDate = currentDate.AddDays(1);
        }

        // Update last check time
        node.LastFailedHeartbeatCheckAt = now;

        // Clean up old data and save
        CleanupOldHeartbeatData(node);
        await _dataStore.SaveNodeAsync(node);
    }
}
