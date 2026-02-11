using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Reconciliation.Handlers;

/// <summary>
/// Handles node.deploy-relay-vm obligations: deploy a relay VM on an eligible node.
///
/// Triggered during node registration for relay-eligible nodes, and by the
/// recovery scanner for eligible nodes that are missing relay infrastructure.
///
/// Delegates to RelayNodeService.DeployRelayVmAsync which:
///   1. Allocates a unique /24 subnet (10.20.X.0/24)
///   2. Generates WireGuard keypair
///   3. Creates the relay VM via VmService.CreateVmAsync (userId="system")
///   4. Initializes RelayNodeInfo on the node
///
/// Idempotency: If node already has RelayInfo with a relay VM, returns Completed.
/// </summary>
public class NodeDeployRelayVmHandler : IObligationHandler
{
    private readonly DataStore _dataStore;
    private readonly IRelayNodeService _relayNodeService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NodeDeployRelayVmHandler> _logger;

    public IReadOnlyList<string> SupportedTypes => [ObligationTypes.NodeDeployRelayVm];

    public NodeDeployRelayVmHandler(
        DataStore dataStore,
        IRelayNodeService relayNodeService,
        IServiceProvider serviceProvider,
        ILogger<NodeDeployRelayVmHandler> logger)
    {
        _dataStore = dataStore;
        _relayNodeService = relayNodeService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ObligationResult> ExecuteAsync(Obligation obligation, CancellationToken ct)
    {
        var nodeId = obligation.ResourceId;
        var node = await _dataStore.GetNodeAsync(nodeId);

        if (node == null)
            return ObligationResult.Fail($"Node {nodeId} not found");

        // Idempotency: already has relay infrastructure
        if (node.RelayInfo != null && !string.IsNullOrEmpty(node.RelayInfo.RelayVmId))
        {
            _logger.LogDebug("Node {NodeId} already has relay VM {VmId}", nodeId, node.RelayInfo.RelayVmId);
            return ObligationResult.Completed($"Relay VM already deployed: {node.RelayInfo.RelayVmId}");
        }

        // Node must be online
        if (node.Status != NodeStatus.Online)
            return ObligationResult.Retry($"Node {nodeId} is {node.Status} — waiting for online");

        // Eligibility check
        if (!_relayNodeService.IsEligibleForRelay(node))
            return ObligationResult.Fail($"Node {nodeId} is not eligible for relay");

        // Deploy the relay VM
        var vmService = _serviceProvider.GetRequiredService<IVmService>();
        var relayVmId = await _relayNodeService.DeployRelayVmAsync(node, vmService, ct);

        if (relayVmId == null)
            return ObligationResult.Retry("Relay VM deployment failed — will retry");

        _logger.LogInformation(
            "Relay VM {VmId} deployed on node {NodeId} via obligation",
            relayVmId, nodeId);

        await _dataStore.SaveNodeAsync(node);

        return ObligationResult.Completed($"Relay VM deployed: {relayVmId}");
    }
}
