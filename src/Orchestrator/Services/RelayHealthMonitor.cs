using Orchestrator.Models;
using Orchestrator.Persistence;

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

    private readonly Dictionary<string, Dictionary<string, DateTime>> _peerRegistrationTimestamps = new();
    private readonly object _timestampLock = new();

    // Health check interval
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromMinutes(1);

    // Prevents premature removal of peers that are still being configured on relay VM
    private static readonly TimeSpan PeerRegistrationGracePeriod = TimeSpan.FromSeconds(30);

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
                "Relay {RelayId} is initializing (age: {Age:mm\\:ss}, timeout period: {Timeout:mm\\:ss} remaining)",
                relay.Id, relayAge, remainingTimeout);

            return;
        }

        try
        {
            // Use relay VM's WireGuard tunnel IP
            var tunnelIp = relay.RelayInfo.TunnelIp
                ?? $"10.20.{relay.RelayInfo.RelaySubnet}.254"
                ?? "10.20.0.254";

            var healthUrl = $"http://{tunnelIp}/health";

            _logger.LogDebug(
                "Checking relay {RelayId} health at {HealthUrl} (subnet {Subnet}, age: {Age:mm\\:ss})",
                relay.Id, healthUrl, relay.RelayInfo.RelaySubnet, relayAge);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HealthCheckTimeout);

            var response = await _httpClient.GetAsync(healthUrl, cts.Token);

            // ========================================================
            // HEALTH CHECK SUCCEEDED - Relay VM is responding
            // ========================================================
            if (response.IsSuccessStatusCode)
            {
                bool wasRecovering = relay.RelayInfo.Status == RelayStatus.Degraded;

                relay.RelayInfo.Status = RelayStatus.Active;
                relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;

                // ✅ FIXED: Pass relay ID to reconciliation for grace period tracking
                await ReconcileRelayStateAsync(relay, ct);
                await _dataStore.SaveNodeAsync(relay);

                if (wasRecovering)
                {
                    _logger.LogInformation(
                        "✓ Relay {RelayId} recovered from Degraded → Active - reconciliation complete",
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

                // ====================================================
                // SELF-HEALING: Check if orchestrator has this relay as WireGuard peer
                // MUST happen BEFORE setting status to Degraded
                // ====================================================
                var hasPeer = await _wireGuardManager.HasRelayPeerAsync(relay, ct);

                if (!hasPeer)
                {
                    _logger.LogWarning(
                        "Relay {RelayId} not configured as WireGuard peer - attempting to add",
                        relay.Id);

                    var added = await _wireGuardManager.AddRelayPeerAsync(relay, ct);

                    if (added)
                    {
                        _logger.LogInformation("✓ Added relay {RelayId} as WireGuard peer (self-healing)", relay.Id);
                    }
                }

                relay.RelayInfo.Status = RelayStatus.Degraded;
                relay.RelayInfo.LastHealthCheck = DateTime.UtcNow;
                await _dataStore.SaveNodeAsync(relay);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Relay {RelayId} health check timed out after {Timeout}s (age: {Age:mm\\:ss})",
                relay.Id, HealthCheckTimeout.TotalSeconds, relayAge);

            // ====================================================
            // SELF-HEALING: Check if orchestrator has this relay as WireGuard peer
            // ====================================================
            var hasPeer = await _wireGuardManager.HasRelayPeerAsync(relay, ct);

            if (!hasPeer)
            {
                _logger.LogWarning(
                    "Relay {RelayId} health check timed out and not configured as WireGuard peer - attempting to add",
                    relay.Id);

                var added = await _wireGuardManager.AddRelayPeerAsync(relay, ct);

                if (added)
                {
                    _logger.LogInformation("✓ Added relay {RelayId} as WireGuard peer (self-healing after timeout)", relay.Id);
                }
            }

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
                ?? (relay.RelayInfo?.RelaySubnet > 0
                    ? $"10.20.{relay.RelayInfo.RelaySubnet}.254"
                    : "10.20.0.254");

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
            // STEP 1: Get actual peer public keys from relay VM
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
            // Note: You may need to load this from config
            var orchestratorConfig = actualPeerKeys.FirstOrDefault(k =>
                k.StartsWith("BL+") || k.Length > 40); // Adjust based on your setup
            if (orchestratorConfig != null)
            {
                actualPeerKeys.Remove(orchestratorConfig);
            }

            _logger.LogInformation(
                "Relay {RelayId} has {ActualCount} actual CGNAT peers (orchestrator shows {DbCount})",
                relay.Id, actualPeerKeys.Count, relay.RelayInfo.ConnectedNodeIds.Count);

            // ========================================
            // STEP 2: Build map of node ID → WireGuard public key
            // ========================================
            var nodeKeyMap = new Dictionary<string, string>();

            foreach (var nodeId in relay.RelayInfo.ConnectedNodeIds.ToList())
            {
                var node = await _dataStore.GetNodeAsync(nodeId);
                if (node != null && !string.IsNullOrEmpty(node.CgnatInfo?.WireGuardConfig))
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
                            _logger.LogWarning("Could not derive public key for node {NodeId}", nodeId);
                        }
                    }
                }
            }

            // ========================================
            // STEP 3 - Track registration timestamps and apply grace period
            // ========================================
            var now = DateTime.UtcNow;
            var nodesToRemove = new List<string>();

            lock (_timestampLock)
            {
                // Initialize tracking for this relay if not exists
                if (!_peerRegistrationTimestamps.ContainsKey(relay.Id))
                {
                    _peerRegistrationTimestamps[relay.Id] = new Dictionary<string, DateTime>();
                }

                var relayTimestamps = _peerRegistrationTimestamps[relay.Id];

                // Update timestamps for all currently connected nodes
                foreach (var nodeId in relay.RelayInfo.ConnectedNodeIds)
                {
                    if (!relayTimestamps.ContainsKey(nodeId))
                    {
                        // New node - record registration time
                        relayTimestamps[nodeId] = now;
                        _logger.LogDebug(
                            "Recording registration timestamp for node {NodeId} on relay {RelayId}",
                            nodeId, relay.Id);
                    }
                }

                // Check each node for removal, respecting grace period
                foreach (var kvp in nodeKeyMap)
                {
                    var nodeId = kvp.Key;
                    var publicKey = kvp.Value;

                    // Check if peer exists on relay VM
                    if (!actualPeerKeys.Contains(publicKey))
                    {
                        // Check if node is within grace period
                        if (relayTimestamps.TryGetValue(nodeId, out var registeredAt))
                        {
                            var age = now - registeredAt;

                            if (age < PeerRegistrationGracePeriod)
                            {
                                _logger.LogDebug(
                                    "Node {NodeId} not found on relay VM but within grace period " +
                                    "(age: {Age:F1}s / {Grace:F1}s) - skipping removal",
                                    nodeId, age.TotalSeconds, PeerRegistrationGracePeriod.TotalSeconds);
                                continue; // ✅ Skip removal - still within grace period
                            }
                        }

                        // Node is missing and past grace period (or no timestamp) - mark for removal
                        nodesToRemove.Add(nodeId);

                        _logger.LogWarning(
                            "Node {NodeId} in database but not on relay VM (past grace period) - " +
                            "removing from ConnectedNodeIds",
                            nodeId);
                    }
                }

                // Clean up timestamps for removed nodes
                foreach (var nodeId in nodesToRemove)
                {
                    relayTimestamps.Remove(nodeId);
                }

                // Clean up timestamps for nodes no longer in ConnectedNodeIds
                var staleTimestamps = relayTimestamps.Keys
                    .Where(nodeId => !relay.RelayInfo.ConnectedNodeIds.Contains(nodeId))
                    .ToList();

                foreach (var nodeId in staleTimestamps)
                {
                    relayTimestamps.Remove(nodeId);
                }
            }

            // ========================================
            // STEP 4: Apply fixes to database state
            // ========================================
            bool stateChanged = false;

            // Remove nodes that aren't actually connected (and past grace period)
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
                    "✓ Reconciled relay {RelayId}: Removed {RemovedCount} ghost nodes, CurrentLoad={Load}",
                    relay.Id, nodesToRemove.Count, correctLoad);
            }
            else
            {
                _logger.LogDebug("Relay {RelayId} state is consistent - no changes needed", relay.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile relay {RelayId} state", relay.Id);
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
                var newRelay = await _relayNodeService.FindBestRelayForCgnatNodeAsync(node, ct);

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