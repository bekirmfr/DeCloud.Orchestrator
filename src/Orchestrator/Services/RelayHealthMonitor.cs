using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrator.Persistence;
using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Background service that monitors relay node health and performs failover
/// ENHANCED: Self-healing WireGuard peer recovery when relay responds but peer is missing
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

    // Grace period for newly deployed relays to fully initialize
    private static readonly TimeSpan RelayInitializationTimeout = TimeSpan.FromMinutes(10);

    // Timeout for individual health checks
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(10);

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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("🔍 Relay health monitor starting (with self-healing)");

        // Wait for system to initialize
        await Task.Delay(TimeSpan.FromMinutes(1), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAllRelaysAsync(ct);
                await Task.Delay(HealthCheckInterval, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in relay health monitor");
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
        }
    }

    private async Task CheckAllRelaysAsync(CancellationToken ct)
    {
        var relayNodes = _dataStore.GetActiveNodes()
            .Where(n => n.RelayInfo != null &&
                        n.RelayInfo.Status != RelayStatus.Initializing)
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

        // Skip health checks for newly deployed relays (grace period)
        var relayAge = DateTime.UtcNow - relay.RegisteredAt;
        if (relay.RelayInfo.Status == RelayStatus.Initializing)
        {
            var remainingTimeout = RelayInitializationTimeout - relayAge;

            _logger.LogDebug(
                "Relay {RelayId} is initializing (age: {Age:mm\\:ss}, remaining: {Timeout:mm\\:ss})",
                relay.Id, relayAge, remainingTimeout);

            return;
        }

        try
        {
            // Use relay VM's WireGuard tunnel IP
            var tunnelIp = relay.RelayInfo.TunnelIp
                ?? $"10.20.{relay.RelayInfo.RelaySubnet}.254"
                ?? "10.20.0.254";  // Fallback for legacy relays

            var healthUrl = $"http://{tunnelIp}/health";

            _logger.LogDebug(
                "Checking relay {RelayId} health at {HealthUrl}",
                relay.Id, healthUrl);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HealthCheckTimeout);

            var response = await _httpClient.GetAsync(healthUrl, cts.Token);

            // ========================================================
            // HEALTH CHECK SUCCEEDED - Relay VM is responding
            // ========================================================
            if (response.IsSuccessStatusCode)
            {
                bool wasRecovering = relay.RelayInfo.Status == RelayStatus.Degraded;
                bool wasOffline = relay.RelayInfo.Status == RelayStatus.Offline;

                relay.RelayInfo.Status = RelayStatus.Active;
                relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;

                // ====================================================
                // SELF-HEALING: Check if orchestrator has this relay as WireGuard peer
                // ====================================================
                var hasPeer = await _wireGuardManager.HasRelayPeerAsync(relay, ct);

                if (!hasPeer)
                {
                    // Relay is healthy but orchestrator doesn't have it as peer
                    // This can happen after:
                    // 1. Orchestrator restart
                    // 2. WireGuard peer cleanup
                    // 3. Manual wg configuration changes
                    _logger.LogWarning(
                        "🔧 SELF-HEAL: Relay {RelayId} is responding but missing from WireGuard peers - recovering",
                        relay.Id);

                    var added = await _wireGuardManager.AddRelayPeerAsync(relay, ct);

                    if (added)
                    {
                        _logger.LogInformation(
                            "✅ SELF-HEAL: Successfully re-added relay {RelayId} as WireGuard peer",
                            relay.Id);

                        // Verify handshake was established
                        await Task.Delay(TimeSpan.FromSeconds(2), ct); // Wait for handshake
                        var handshakeEstablished = await VerifyHandshakeAsync(relay, ct);

                        if (handshakeEstablished)
                        {
                            _logger.LogInformation(
                                "✅ SELF-HEAL: WireGuard handshake established with relay {RelayId}",
                                relay.Id);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "⚠️  SELF-HEAL: Peer added but handshake not yet established with relay {RelayId} " +
                                "(may take a few moments)",
                                relay.Id);
                        }
                    }
                    else
                    {
                        _logger.LogError(
                            "❌ SELF-HEAL: Failed to re-add relay {RelayId} as WireGuard peer",
                            relay.Id);

                        // Mark as degraded since we can't communicate with it
                        relay.RelayInfo.Status = RelayStatus.Degraded;
                    }
                }
                else
                {
                    // Peer exists - verify handshake is active
                    var handshakeEstablished = await VerifyHandshakeAsync(relay, ct);

                    if (!handshakeEstablished && relay.RelayInfo.Status == RelayStatus.Active)
                    {
                        _logger.LogWarning(
                            "⚠️  Relay {RelayId} peer exists but handshake is stale or not established",
                            relay.Id);
                    }
                }

                // Reconcile relay state (sync database with actual peers)
                await ReconcileRelayStateAsync(relay, ct);
                await _dataStore.SaveNodeAsync(relay);

                // Log recovery if applicable
                if (wasRecovering)
                {
                    _logger.LogInformation(
                        "✓ Relay {RelayId} recovered: Degraded → Active",
                        relay.Id);
                }
                else if (wasOffline)
                {
                    _logger.LogInformation(
                        "✓ Relay {RelayId} recovered: Offline → Active - re-enabling CGNAT connections",
                        relay.Id);

                    // Trigger re-registration for all connected nodes
                    foreach (var nodeId in relay.RelayInfo.ConnectedNodeIds.ToList())
                    {
                        var node = await _dataStore.GetNodeAsync(nodeId);
                        if (node != null)
                        {
                            _logger.LogDebug(
                                "Re-ensuring peer registration for node {NodeId} on recovered relay {RelayId}",
                                nodeId, relay.Id);
                            // Will be handled on next heartbeat via EnsurePeerRegisteredAsync
                        }
                    }
                }
            }
            // ========================================================
            // HEALTH CHECK FAILED - Relay VM not responding
            // ========================================================
            else
            {
                _logger.LogWarning(
                    "Relay {RelayId} health check failed with status {StatusCode}",
                    relay.Id, response.StatusCode);

                relay.RelayInfo.Status = RelayStatus.Degraded;
                relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;
                await _dataStore.SaveNodeAsync(relay);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Relay {RelayId} health check timed out after {Timeout}s",
                relay.Id, HealthCheckTimeout.TotalSeconds);

            relay.RelayInfo.Status = RelayStatus.Degraded;
            relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            await _dataStore.SaveNodeAsync(relay);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Relay {RelayId} health check failed - marking offline",
                relay.Id);

            relay.RelayInfo.Status = RelayStatus.Offline;
            relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            await _dataStore.SaveNodeAsync(relay);

            // Trigger failover for affected CGNAT nodes
            await FailoverRelayAsync(relay, ct);
        }
    }

    /// <summary>
    /// Verify that WireGuard handshake has been established with relay
    /// </summary>
    private async Task<bool> VerifyHandshakeAsync(Node relay, CancellationToken ct)
    {
        if (relay.RelayInfo == null || string.IsNullOrEmpty(relay.RelayInfo.WireGuardPublicKey))
        {
            return false;
        }

        try
        {
            // Query WireGuard for peer handshake info
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wg",
                    Arguments = "show wg-relay-client latest-handshakes",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                return false;
            }

            // Parse output: format is "publickey\ttimestamp"
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length >= 2 && parts[0] == relay.RelayInfo.WireGuardPublicKey)
                {
                    // Check if handshake is recent (within last 5 minutes)
                    if (long.TryParse(parts[1], out var timestamp))
                    {
                        var handshakeTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
                        var age = DateTime.UtcNow - handshakeTime.UtcDateTime;

                        if (age < TimeSpan.FromMinutes(5))
                        {
                            _logger.LogDebug(
                                "Relay {RelayId} handshake: {Age:mm\\:ss} ago",
                                relay.Id, age);
                            return true;
                        }
                        else
                        {
                            _logger.LogDebug(
                                "Relay {RelayId} handshake is stale: {Age:mm\\:ss} ago",
                                relay.Id, age);
                            return false;
                        }
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify handshake for relay {RelayId}", relay.Id);
            return false;
        }
    }

    /// <summary>
    /// Reconcile orchestrator's relay state with actual relay VM state
    /// Queries relay API to get real peer list and syncs database
    /// </summary>
    private async Task ReconcileRelayStateAsync(Node relay, CancellationToken ct)
    {
        try
        {
            var relayTunnelIp = relay.RelayInfo?.TunnelIp
                ?? $"10.20.{relay.RelayInfo.RelaySubnet}.254"
                ?? "10.20.0.254";
            var apiUrl = $"http://{relayTunnelIp}:8080/api/relay/wireguard";

            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await httpClient.GetAsync(apiUrl, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Could not query relay {RelayId} for state reconciliation: {Status}",
                    relay.Id, response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("peers", out var peersArray))
            {
                _logger.LogWarning("Relay API response missing 'peers' property");
                return;
            }

            // Get actual peer public keys from relay
            var actualPeerKeys = new HashSet<string>();

            foreach (var peer in peersArray.EnumerateArray())
            {
                if (peer.TryGetProperty("public_key", out var pubKeyProp))
                {
                    var peerKey = pubKeyProp.GetString();
                    if (!string.IsNullOrEmpty(peerKey))
                    {
                        actualPeerKeys.Add(peerKey);
                    }
                }
            }

            _logger.LogDebug(
                "Relay {RelayId} reconciliation: {ActualCount} actual peers, {DbCount} in database",
                relay.Id, actualPeerKeys.Count, relay.RelayInfo.ConnectedNodeIds.Count);

            // Find nodes that are in database but not on relay VM
            var nodesToRemove = new List<string>();

            foreach (var nodeId in relay.RelayInfo.ConnectedNodeIds.ToList())
            {
                var node = await _dataStore.GetNodeAsync(nodeId);
                if (node == null)
                {
                    nodesToRemove.Add(nodeId);
                    _logger.LogWarning(
                        "Node {NodeId} in relay ConnectedNodeIds but not in database - removing",
                        nodeId);
                    continue;
                }

                var publicKey = await _wireGuardManager.ExtractPublicKeyFromConfigAsync(
                    node.CgnatInfo?.WireGuardConfig ?? "",
                    ct);

                if (string.IsNullOrEmpty(publicKey))
                {
                    continue;
                }

                // Node is in database but NOT on relay VM → remove from database
                if (!actualPeerKeys.Contains(publicKey))
                {
                    nodesToRemove.Add(nodeId);

                    _logger.LogWarning(
                        "Node {NodeId} in database but not on relay VM - removing from ConnectedNodeIds",
                        nodeId);
                }
            }

            // Apply fixes to database state
            bool stateChanged = false;

            foreach (var nodeId in nodesToRemove)
            {
                relay.RelayInfo.ConnectedNodeIds.Remove(nodeId);
                stateChanged = true;
            }

            // Recalculate load based on actual connected nodes
            var correctLoad = relay.RelayInfo.ConnectedNodeIds.Count;

            if (relay.RelayInfo.CurrentLoad != correctLoad)
            {
                _logger.LogDebug(
                    "Relay {RelayId} CurrentLoad corrected: {OldLoad} → {NewLoad}",
                    relay.Id, relay.RelayInfo.CurrentLoad, correctLoad);

                relay.RelayInfo.CurrentLoad = correctLoad;
                stateChanged = true;
            }

            // Save corrected state
            if (stateChanged)
            {
                await _dataStore.SaveNodeAsync(relay);

                _logger.LogInformation(
                    "✓ Reconciled relay {RelayId}: Removed {RemovedCount} ghost nodes, CurrentLoad={Load}",
                    relay.Id, nodesToRemove.Count, correctLoad);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to reconcile relay {RelayId} state",
                relay.Id);
        }
    }

    private async Task FailoverRelayAsync(Node failedRelay, CancellationToken ct)
    {
        if (failedRelay.RelayInfo == null)
        {
            return;
        }

        var affectedNodes = (await _dataStore.GetAllNodesAsync())
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
            "Relay {RelayId} failed with {Count} connected CGNAT nodes - initiating failover",
            failedRelay.Id, affectedNodes.Count);

        foreach (var node in affectedNodes)
        {
            try
            {
                // Find alternative relay
                var newRelay = await _relayNodeService.FindBestRelayForCgnatNodeAsync(
                    node,
                    ct);

                if (newRelay != null)
                {
                    _logger.LogInformation(
                        "Failing over CGNAT node {NodeId} from relay {OldRelay} to {NewRelay}",
                        node.Id, failedRelay.Id, newRelay.Id);

                    await _relayNodeService.AssignCgnatNodeToRelayAsync(node, newRelay, ct);
                }
                else
                {
                    _logger.LogWarning(
                        "No alternative relay available for CGNAT node {NodeId}",
                        node.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to failover CGNAT node {NodeId} from relay {RelayId}",
                    node.Id, failedRelay.Id);
            }
        }
    }
}