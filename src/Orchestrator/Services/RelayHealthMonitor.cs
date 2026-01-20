using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrator.Persistence;
using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Background service that monitors relay node health and performs failover
/// </summary>
public class RelayHealthMonitor : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly IRelayNodeService _relayNodeService;
    private readonly IWireGuardManager _wireGuardManager;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RelayHealthMonitor> _logger;

    // Health check interval
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(1);

    public RelayHealthMonitor(
        DataStore dataStore,
        IRelayNodeService relayNodeService,
        IWireGuardManager wireGuardManager,
        HttpClient httpClient,
        ILogger<RelayHealthMonitor> logger)
    {
        _dataStore = dataStore;
        _relayNodeService = relayNodeService;
        _wireGuardManager = wireGuardManager;
        _httpClient = httpClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Relay health monitor starting");

        // Wait for system to initialize
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllRelaysAsync(stoppingToken);
                await Task.Delay(HealthCheckInterval, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in relay health monitor");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task CheckAllRelaysAsync(CancellationToken ct)
    {
        var relayNodes = _dataStore.Nodes.Values
            .Where(n => n.RelayInfo?.IsActive == true)
            .ToList();

        _logger.LogDebug("Checking health of {Count} relay nodes", relayNodes.Count);

        foreach (var relay in relayNodes)
        {
            await CheckRelayHealthAsync(relay, ct);
        }
    }

    private async Task CheckRelayHealthAsync(Node relay, CancellationToken ct)
    {
        if (relay.RelayInfo == null)
        {
            _logger.LogWarning("Relay node {NodeId} has null RelayInfo", relay.Id);
            return;
        }

        try
        {
            // Use relay VM's WireGuard tunnel IP
            // Fallback to legacy subnet 0 if not set (backward compatibility)
            var tunnelIp = relay.RelayInfo.TunnelIp
                ?? $"10.20.{relay.RelayInfo.RelaySubnet}.254"
                ?? "10.20.0.254";  // Double fallback for very old relays

            var healthUrl = $"http://{tunnelIp}/health";

            _logger.LogDebug(
                "Checking relay {RelayId} health at {HealthUrl} (subnet {Subnet})",
                relay.Id, healthUrl, relay.RelayInfo.RelaySubnet);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 second timeout

            var response = await _httpClient.GetAsync(healthUrl, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                if (relay.RelayInfo.Status != RelayStatus.Active)
                {
                    _logger.LogInformation(
                        "Relay {RelayId} recovered - marking as Active",
                        relay.Id);
                }

                relay.RelayInfo.Status = RelayStatus.Active;
                relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;
                await _dataStore.SaveNodeAsync(relay);
            }
            else
            {
                _logger.LogWarning(
                    "Relay {RelayId} health check failed with status {StatusCode}",
                    relay.Id, response.StatusCode);

                relay.RelayInfo.Status = RelayStatus.Degraded;
                relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;

                // Check if relay peer is configured on orchestrator
                var hasPeer = await _wireGuardManager.HasRelayPeerAsync(relay, ct);

                if (!hasPeer && relay.RelayInfo?.IsActive == true)
                {
                    _logger.LogWarning(
                        "Relay {RelayId} is active but not configured as peer - " +
                        "attempting to add", relay.Id);

                    var added = await _wireGuardManager.AddRelayPeerAsync(relay, ct);

                    if (added)
                    {
                        _logger.LogInformation(
                            "✓ Recovered relay {RelayId} via health check",
                            relay.Id);
                    }

                    await _dataStore.SaveNodeAsync(relay);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Relay {RelayId} health check timed out", relay.Id);
            relay.RelayInfo.Status = RelayStatus.Degraded;
            relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            await _dataStore.SaveNodeAsync(relay);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relay {RelayId} health check failed - marking offline", relay.Id);

            relay.RelayInfo.Status = RelayStatus.Offline;
            relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            await _dataStore.SaveNodeAsync(relay);

            // Trigger failover for affected CGNAT nodes
            await FailoverRelayAsync(relay, ct);
        }
    }

    private async Task FailoverRelayAsync(Node failedRelay, CancellationToken ct)
    {
        if (failedRelay.RelayInfo == null)
        {
            return;
        }

        var affectedNodes = _dataStore.Nodes.Values
            .Where(n => n.CgnatInfo?.AssignedRelayNodeId == failedRelay.Id)
            .ToList();

        if (!affectedNodes.Any())
        {
            _logger.LogInformation(
                "Relay {RelayId} has no connected CGNAT nodes - no failover needed",
                failedRelay.Id);
            return;
        }

        _logger.LogWarning(
            "Failing over {Count} CGNAT nodes from relay {RelayId}",
            affectedNodes.Count, failedRelay.Id);

        foreach (var node in affectedNodes)
        {
            try
            {
                var newRelay = await _relayNodeService.FindBestRelayForCgnatNodeAsync(node, ct);

                if (newRelay != null && newRelay.Id != failedRelay.Id)
                {
                    _logger.LogInformation(
                        "Reassigning CGNAT node {NodeId} from relay {OldRelay} to {NewRelay}",
                        node.Id, failedRelay.Id, newRelay.Id);

                    await _relayNodeService.AssignCgnatNodeToRelayAsync(node, newRelay, ct);
                }
                else
                {
                    _logger.LogWarning(
                        "No alternative relay found for CGNAT node {NodeId}",
                        node.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to failover CGNAT node {NodeId}",
                    node.Id);
            }
        }

        // Update failed relay's connected nodes list
        failedRelay.RelayInfo.ConnectedNodeIds.Clear();
        failedRelay.RelayInfo.CurrentLoad = 0;
        await _dataStore.SaveNodeAsync(failedRelay);

        _logger.LogInformation(
            "Failover completed for relay {RelayId} - reassigned {Count} nodes",
            failedRelay.Id, affectedNodes.Count);
    }
}