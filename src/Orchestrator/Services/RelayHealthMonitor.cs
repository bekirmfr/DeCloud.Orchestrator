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

    // FIXED: Grace period for newly deployed relays to fully initialize
    // Relays need time for cloud-init, package installation, WireGuard setup, etc.
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
        _logger.LogInformation("Relay health monitor starting");

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
        var relayNodes = _dataStore.Nodes.Values
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
                "Relay {RelayId} is initializing (age: {Age:mm\\:ss}, timeout period: {Timeout:mm\\:ss} remaining)",
                relay.Id, relayAge, remainingTimeout);

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
                "Checking relay {RelayId} health at {HealthUrl} (subnet {Subnet}, age: {Age:mm\\:ss})",
                relay.Id, healthUrl, relay.RelayInfo.RelaySubnet, relayAge);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HealthCheckTimeout);

            var response = await _httpClient.GetAsync(healthUrl, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                bool wasRecovering = relay.RelayInfo.Status == RelayStatus.Degraded;

                relay.RelayInfo.Status = RelayStatus.Active;
                relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;

                await ReconcileRelayStateAsync(relay, ct);
                await _dataStore.SaveNodeAsync(relay);

                if (wasRecovering)
                {
                    _logger.LogInformation(
                        "✓ Relay {RelayId} recovered from Degraded → Active - reconciliation complete",
                        relay.Id);

                    // ✅ NEW: Trigger re-registration for all connected nodes
                    // This ensures peers get re-added to relay VM after recovery
                    foreach (var nodeId in relay.RelayInfo.ConnectedNodeIds.ToList())
                    {
                        if (_dataStore.Nodes.TryGetValue(nodeId, out var node))
                        {
                            _logger.LogDebug(
                                "Re-ensuring peer registration for node {NodeId} on recovered relay {RelayId}",
                                nodeId, relay.Id);

                            // Will be handled on next heartbeat via EnsurePeerRegisteredAsync
                            // Just log for visibility
                        }
                    }
                }
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

                if (!hasPeer && relay.RelayInfo?.Status == RelayStatus.Active)
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
            _logger.LogWarning(
                "Relay {RelayId} health check timed out after {Timeout}s (age: {Age:mm\\:ss})",
                relay.Id, HealthCheckTimeout.TotalSeconds, relayAge);

            relay.RelayInfo.Status = RelayStatus.Degraded;
            relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            await _dataStore.SaveNodeAsync(relay);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Relay {RelayId} health check failed - marking offline (age: {Age:mm\\:ss})",
                relay.Id, relayAge);

            relay.RelayInfo.Status = RelayStatus.Offline;
            relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            await _dataStore.SaveNodeAsync(relay);

            // Trigger failover for affected CGNAT nodes
            await FailoverRelayAsync(relay, ct);
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
            var relayTunnelIp = relay.RelayInfo?.TunnelIp ?? "10.20.0.254";
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

            // ========================================
            // STEP 1: Get actual peer public keys from relay
            // ========================================
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

            // Remove orchestrator's peer (always connected, not a CGNAT node)
            var orchestratorPeerKey = "BL+cVFOmB/WCNgml...";  // Orchestrator's peer key
            actualPeerKeys.Remove(orchestratorPeerKey);

            _logger.LogInformation(
                "Relay {RelayId} has {ActualCount} actual CGNAT peers (orchestrator shows {DbCount})",
                relay.Id, actualPeerKeys.Count, relay.RelayInfo.ConnectedNodeIds.Count);

            // ========================================
            // STEP 2: Build map of node ID → WireGuard public key
            // ========================================
            var nodeKeyMap = new Dictionary<string, string>();

            foreach (var nodeId in relay.RelayInfo.ConnectedNodeIds.ToList())
            {
                if (_dataStore.Nodes.TryGetValue(nodeId, out var node))
                {
                    // Extract public key from WireGuard config
                    if (!string.IsNullOrEmpty(node.CgnatInfo?.WireGuardConfig))
                    {
                        var privateKeyMatch = System.Text.RegularExpressions.Regex.Match(
                            node.CgnatInfo.WireGuardConfig,
                            @"PrivateKey\s*=\s*([A-Za-z0-9+/=]+)");

                        if (privateKeyMatch.Success)
                        {
                            try
                            {
                                var privateKey = privateKeyMatch.Groups[1].Value.Trim();
                                var publicKey = await _wireGuardManager.DerivePublicKeyAsync(privateKey, ct);
                                nodeKeyMap[nodeId] = publicKey;
                            }
                            catch
                            {
                                _logger.LogWarning(
                                    "Could not derive public key for node {NodeId}",
                                    nodeId);
                            }
                        }
                    }
                }
            }

            // ========================================
            // STEP 3: Reconcile - find nodes that should be removed
            // ========================================
            var nodesToRemove = new List<string>();

            foreach (var kvp in nodeKeyMap)
            {
                var nodeId = kvp.Key;
                var publicKey = kvp.Value;

                // Node is in database but NOT on relay VM → remove from database
                if (!actualPeerKeys.Contains(publicKey))
                {
                    nodesToRemove.Add(nodeId);

                    _logger.LogWarning(
                        "Node {NodeId} in database but not on relay VM - removing from ConnectedNodeIds",
                        nodeId);
                }
            }

            // ========================================
            // STEP 4: Apply fixes to database state
            // ========================================
            bool stateChanged = false;

            // Remove nodes that aren't actually connected
            foreach (var nodeId in nodesToRemove)
            {
                relay.RelayInfo.ConnectedNodeIds.Remove(nodeId);
                stateChanged = true;
            }

            // Recalculate load based on actual connected nodes
            var correctLoad = relay.RelayInfo.ConnectedNodeIds.Count;

            if (relay.RelayInfo.CurrentLoad != correctLoad)
            {
                _logger.LogWarning(
                    "Relay {RelayId} CurrentLoad mismatch: DB={DbLoad}, Actual={ActualLoad} - correcting",
                    relay.Id, relay.RelayInfo.CurrentLoad, correctLoad);

                relay.RelayInfo.CurrentLoad = correctLoad;
                stateChanged = true;
            }

            // Save corrected state
            if (stateChanged)
            {
                await _dataStore.SaveNodeAsync(relay);

                _logger.LogInformation(
                    "✓ Reconciled relay {RelayId} state: Removed {RemovedCount} ghost nodes, CurrentLoad corrected to {Load}",
                    relay.Id, nodesToRemove.Count, correctLoad);
            }
            else
            {
                _logger.LogDebug(
                    "Relay {RelayId} state is consistent - no changes needed",
                    relay.Id);
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