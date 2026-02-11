using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Reconciliation.Handlers;

/// <summary>
/// Handles node.assign-relay obligations: find the best relay for a CGNAT node
/// and assign it.
///
/// Triggered during node registration for CGNAT nodes, and by the recovery
/// scanner for CGNAT nodes missing relay assignments.
///
/// Flow:
///   1. FindBestRelayForCgnatNodeAsync — scores relays by proximity, load, capacity
///   2. AssignCgnatNodeToRelayAsync — allocates tunnel IP, generates WireGuard config,
///      registers peer with relay VM
///
/// Idempotency: If node already has CgnatInfo with valid relay, returns Completed.
/// Retry: If no relay is available (all at capacity), retries with backoff.
/// </summary>
public class NodeAssignRelayHandler : IObligationHandler
{
    private readonly DataStore _dataStore;
    private readonly IRelayNodeService _relayNodeService;
    private readonly ILogger<NodeAssignRelayHandler> _logger;

    public IReadOnlyList<string> SupportedTypes => [ObligationTypes.NodeAssignRelay];

    public NodeAssignRelayHandler(
        DataStore dataStore,
        IRelayNodeService relayNodeService,
        ILogger<NodeAssignRelayHandler> logger)
    {
        _dataStore = dataStore;
        _relayNodeService = relayNodeService;
        _logger = logger;
    }

    public async Task<ObligationResult> ExecuteAsync(Obligation obligation, CancellationToken ct)
    {
        var nodeId = obligation.ResourceId;
        var node = await _dataStore.GetNodeAsync(nodeId);

        if (node == null)
            return ObligationResult.Fail($"Node {nodeId} not found");

        // Idempotency: already has a valid relay assignment
        if (node.CgnatInfo != null &&
            !string.IsNullOrEmpty(node.CgnatInfo.AssignedRelayNodeId) &&
            !string.IsNullOrEmpty(node.CgnatInfo.TunnelIp))
        {
            // Verify the assigned relay still exists and is active
            var existingRelay = await _dataStore.GetNodeAsync(node.CgnatInfo.AssignedRelayNodeId);
            if (existingRelay?.RelayInfo?.Status == RelayStatus.Active)
            {
                _logger.LogDebug(
                    "Node {NodeId} already assigned to relay {RelayId}",
                    nodeId, node.CgnatInfo.AssignedRelayNodeId);
                return ObligationResult.Completed(
                    $"Already assigned to relay {node.CgnatInfo.AssignedRelayNodeId}");
            }

            // Existing assignment is stale — clear it and reassign
            _logger.LogWarning(
                "Node {NodeId} relay assignment to {RelayId} is stale — reassigning",
                nodeId, node.CgnatInfo.AssignedRelayNodeId);
        }

        // Not behind CGNAT — shouldn't have this obligation
        if (node.HardwareInventory.Network.NatType == NatType.None)
            return ObligationResult.Fail($"Node {nodeId} is not behind CGNAT");

        // Find best relay
        var relay = await _relayNodeService.FindBestRelayForCgnatNodeAsync(node, ct);
        if (relay == null)
            return ObligationResult.Retry("No available relay — will retry when one becomes available");

        // Assign
        var success = await _relayNodeService.AssignCgnatNodeToRelayAsync(node, relay, ct);
        if (!success)
            return ObligationResult.Retry($"Failed to assign to relay {relay.Id} — will retry");

        _logger.LogInformation(
            "CGNAT node {NodeId} assigned to relay {RelayId} via obligation (tunnel: {TunnelIp})",
            nodeId, relay.Id, node.CgnatInfo?.TunnelIp);

        return ObligationResult.Completed($"Assigned to relay {relay.Id}");
    }
}
