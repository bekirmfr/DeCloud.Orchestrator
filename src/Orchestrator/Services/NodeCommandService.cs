using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Service for delivering commands to nodes using hybrid push-pull strategy.
/// Attempts push for instant delivery, falls back to queue on failure.
/// </summary>
public interface INodeCommandService
{
    /// <summary>
    /// Deliver command to node using hybrid push-pull strategy
    /// </summary>
    Task<CommandDeliveryResult> DeliverCommandAsync(
        string nodeId,
        NodeCommand command,
        CancellationToken ct = default);

    /// <summary>
    /// Get statistics for monitoring
    /// </summary>
    CommandDeliveryStats GetStats();
}

public class NodeCommandService : INodeCommandService
{
    private readonly DataStore _dataStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<NodeCommandService> _logger;

    // Statistics
    private long _pushAttempts = 0;
    private long _pushSuccesses = 0;
    private long _pushFailures = 0;
    private long _queueFallbacks = 0;

    public NodeCommandService(
        DataStore dataStore,
        HttpClient httpClient,
        ILogger<NodeCommandService> logger)
    {
        _dataStore = dataStore;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<CommandDeliveryResult> DeliverCommandAsync(
        string nodeId,
        NodeCommand command,
        CancellationToken ct = default)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
        {
            _logger.LogError("Cannot deliver command {CommandId}: Node {NodeId} not found",
                command.CommandId, nodeId);
            return new CommandDeliveryResult(false, DeliveryMethod.None, "Node not found");
        }

        // ================================================================
        // STEP 1: Check if queue has pending commands (maintain ordering)
        // ================================================================
        var hasQueuedCommands = _dataStore.HasPendingCommands(nodeId);

        if (hasQueuedCommands)
        {
            // Queue not empty - maintain FIFO order by queuing
            _dataStore.AddPendingCommand(nodeId, command);

            Interlocked.Increment(ref _queueFallbacks);

            _logger.LogDebug(
                "Command {CommandId} queued for node {NodeId} (queue not empty, maintaining order)",
                command.CommandId, nodeId);

            return new CommandDeliveryResult(true, DeliveryMethod.Queued,
                "Queued to maintain command ordering");
        }

        // ================================================================
        // STEP 2: Queue empty - attempt direct push if node is reachable
        // ================================================================

        // Check if push is disabled for this node
        if (!node.PushEnabled)
        {
            _logger.LogDebug(
                "Push disabled for node {NodeId}, queuing command {CommandId}",
                nodeId, command.CommandId);

            _dataStore.AddPendingCommand(nodeId, command);
            Interlocked.Increment(ref _queueFallbacks);

            return new CommandDeliveryResult(true, DeliveryMethod.Queued,
                "Node push disabled, using pull");
        }

        var pushResult = await TryPushCommandAsync(node, command, ct);

        if (pushResult.Success)
        {
            Interlocked.Increment(ref _pushAttempts);
            Interlocked.Increment(ref _pushSuccesses);

            _logger.LogInformation(
                "✓ Command {CommandId} pushed directly to node {NodeId} ({Method}, latency: ~100-200ms)",
                command.CommandId, nodeId, pushResult.ConnectionMethod);

            // Update node push metrics
            node.LastCommandPushedAt = DateTime.UtcNow;
            node.ConsecutivePushSuccesses++;
            node.ConsecutivePushFailures = 0;
            await _dataStore.SaveNodeAsync(node);

            return new CommandDeliveryResult(true, DeliveryMethod.Pushed,
                $"Pushed via {pushResult.ConnectionMethod}");
        }

        // ================================================================
        // STEP 3: Push failed - enqueue for node to pull when available
        // ================================================================
        Interlocked.Increment(ref _pushAttempts);
        Interlocked.Increment(ref _pushFailures);
        Interlocked.Increment(ref _queueFallbacks);

        _dataStore.AddPendingCommand(nodeId, command);

        _logger.LogWarning(
            "Command {CommandId} push failed ({Reason}), queued for pull by node {NodeId}",
            command.CommandId, pushResult.ErrorMessage, nodeId);

        // Track consecutive failures
        node.ConsecutivePushFailures++;
        node.ConsecutivePushSuccesses = 0;

        // Disable push if too many consecutive failures
        if (node.ConsecutivePushFailures >= 5)
        {
            node.PushEnabled = false;
            _logger.LogWarning(
                "Node {NodeId} push disabled after {Failures} consecutive failures",
                nodeId, node.ConsecutivePushFailures);
        }

        await _dataStore.SaveNodeAsync(node);

        return new CommandDeliveryResult(true, DeliveryMethod.Queued,
            $"Push failed: {pushResult.ErrorMessage}, queued for pull");
    }

    /// <summary>
    /// Try to push command directly to node
    /// Handles both public IP and CGNAT nodes (via relay tunnel)
    /// </summary>
    private async Task<PushResult> TryPushCommandAsync(
        Node node,
        NodeCommand command,
        CancellationToken ct)
    {
        try
        {
            // Determine target URL based on node type
            string targetHost;
            string connectionMethod;

            if (node.CgnatInfo != null && !string.IsNullOrEmpty(node.CgnatInfo.TunnelIp))
            {
                // CGNAT node - push through WireGuard tunnel
                targetHost = node.CgnatInfo.TunnelIp;
                connectionMethod = "CGNAT-TUNNEL";

                _logger.LogDebug(
                    "Pushing to CGNAT node {NodeId} via tunnel IP {TunnelIp}",
                    node.Id, targetHost);
            }
            else if (!string.IsNullOrEmpty(node.PublicIp))
            {
                // Public IP node - direct push
                targetHost = node.PublicIp;
                connectionMethod = "PUBLIC-IP";

                _logger.LogDebug(
                    "Pushing to public IP node {NodeId} at {PublicIp}",
                    node.Id, targetHost);
            }
            else
            {
                return new PushResult(false, null, "No reachable address");
            }

            var targetPort = node.AgentPort > 0 ? node.AgentPort : 7000;
            var pushUrl = $"http://{targetHost}:{targetPort}/api/commands/receive";

            _logger.LogDebug(
                "Push URL for node {NodeId}: {Url} ({Method})",
                node.Id, pushUrl, connectionMethod);

            // Create request with fast timeout (3 seconds)
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            var response = await _httpClient.PostAsJsonAsync(
                pushUrl,
                command,
                cts.Token);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Push successful to node {NodeId} via {Method}",
                    node.Id, connectionMethod);

                return new PushResult(true, connectionMethod, null);
            }
            else
            {
                var error = $"HTTP {response.StatusCode}: {response.ReasonPhrase}";
                _logger.LogDebug(
                    "Push to node {NodeId} returned error: {Error}",
                    node.Id, error);

                return new PushResult(false, null, error);
            }
        }
        catch (TaskCanceledException)
        {
            // Timeout - node likely offline
            _logger.LogDebug(
                "Push to node {NodeId} timed out (node may be offline)",
                node.Id);

            return new PushResult(false, null, "Timeout");
        }
        catch (HttpRequestException ex)
        {
            // Network error
            _logger.LogDebug(ex,
                "Push to node {NodeId} failed with network error",
                node.Id);

            return new PushResult(false, null, $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error pushing command to node {NodeId}",
                node.Id);

            return new PushResult(false, null, $"Unexpected error: {ex.Message}");
        }
    }

    public CommandDeliveryStats GetStats()
    {
        var pushAttempts = Interlocked.Read(ref _pushAttempts);
        var pushSuccesses = Interlocked.Read(ref _pushSuccesses);
        var pushFailures = Interlocked.Read(ref _pushFailures);
        var queueFallbacks = Interlocked.Read(ref _queueFallbacks);

        return new CommandDeliveryStats
        {
            PushAttempts = pushAttempts,
            PushSuccesses = pushSuccesses,
            PushFailures = pushFailures,
            QueueFallbacks = queueFallbacks,
            PushSuccessRate = pushAttempts > 0
                ? (double)pushSuccesses / pushAttempts * 100
                : 0
        };
    }
}

/// <summary>
/// Result of command delivery operation
/// </summary>
public record CommandDeliveryResult(
    bool Success,
    DeliveryMethod Method,
    string Message);

/// <summary>
/// Result of push attempt
/// </summary>
public record PushResult(
    bool Success,
    string? ConnectionMethod,
    string? ErrorMessage);

/// <summary>
/// How command was delivered
/// </summary>
public enum DeliveryMethod
{
    None,
    Pushed,    // Delivered via push
    Queued     // Queued for pull
}

/// <summary>
/// Statistics for monitoring command delivery
/// </summary>
public class CommandDeliveryStats
{
    public long PushAttempts { get; set; }
    public long PushSuccesses { get; set; }
    public long PushFailures { get; set; }
    public long QueueFallbacks { get; set; }
    public double PushSuccessRate { get; set; }
}