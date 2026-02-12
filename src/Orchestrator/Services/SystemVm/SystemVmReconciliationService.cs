using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Background service that converges each node toward its desired system VM state.
///
/// Every 30 seconds, for each online node:
///   - Pending obligations with met dependencies → deploy
///   - Deploying obligations → check if VM is Running + healthy → mark Active
///   - Active obligations → nothing to do
///   - Failed obligations → retry with exponential backoff
///
/// This is the Kubernetes controller pattern: declare desired state, let a loop
/// converge toward it. Registration is fast (compute + store obligations, deploy
/// what's immediately ready), and the loop handles everything else.
/// </summary>
public class SystemVmReconciliationService : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly IRelayNodeService _relayNodeService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemVmReconciliationService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public SystemVmReconciliationService(
        DataStore dataStore,
        IRelayNodeService relayNodeService,
        IServiceProvider serviceProvider,
        ILogger<SystemVmReconciliationService> logger)
    {
        _dataStore = dataStore;
        _relayNodeService = relayNodeService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SystemVmReconciliationService started (interval: {Interval}s)", Interval.TotalSeconds);

        // Wait for startup to complete
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var nodes = await _dataStore.GetAllNodesAsync();
                foreach (var node in nodes.Where(n => n.Status == NodeStatus.Online))
                {
                    if (node.SystemVmObligations.Count == 0)
                        continue;

                    await ReconcileNodeAsync(node, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SystemVmReconciliationService");
            }

            await Task.Delay(Interval, ct);
        }
    }

    /// <summary>
    /// Reconcile a single node: process each obligation based on its current status.
    /// Called during the background loop AND immediately after registration
    /// (to deploy obligations with no dependencies right away).
    /// </summary>
    public async Task ReconcileNodeAsync(Node node, CancellationToken ct = default)
    {
        var changed = false;

        foreach (var obligation in node.SystemVmObligations)
        {
            var before = obligation.Status;

            switch (obligation.Status)
            {
                case SystemVmStatus.Pending:
                    await TryDeployAsync(node, obligation, ct);
                    break;

                case SystemVmStatus.Deploying:
                    await CheckDeploymentProgressAsync(node, obligation, ct);
                    break;

                case SystemVmStatus.Active:
                    // Already converged — nothing to do
                    break;

                case SystemVmStatus.Failed:
                    await TryRetryAsync(node, obligation, ct);
                    break;
            }

            if (obligation.Status != before)
                changed = true;
        }

        if (changed)
            await _dataStore.SaveNodeAsync(node);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Pending → Deploy (if dependencies are met)
    // ════════════════════════════════════════════════════════════════════════

    private async Task TryDeployAsync(Node node, SystemVmObligation obligation, CancellationToken ct)
    {
        if (!SystemVmDependencies.AreDependenciesMet(obligation.Role, node.SystemVmObligations))
            return; // Dependencies not met yet — will try again next cycle

        _logger.LogInformation(
            "Deploying {Role} system VM on node {NodeId} (dependencies met)",
            obligation.Role, node.Id);

        try
        {
            var vmId = await DeploySystemVmAsync(node, obligation.Role, ct);

            if (vmId != null)
            {
                obligation.VmId = vmId;
                obligation.Status = SystemVmStatus.Deploying;
                obligation.DeployedAt = DateTime.UtcNow;

                _logger.LogInformation(
                    "{Role} VM {VmId} deploying on node {NodeId}",
                    obligation.Role, vmId, node.Id);
            }
            else
            {
                obligation.FailureCount++;
                obligation.Status = SystemVmStatus.Failed;
                obligation.LastError = "Deployment returned null";

                _logger.LogWarning(
                    "Failed to deploy {Role} VM on node {NodeId} (attempt {Count})",
                    obligation.Role, node.Id, obligation.FailureCount);
            }
        }
        catch (Exception ex)
        {
            obligation.FailureCount++;
            obligation.Status = SystemVmStatus.Failed;
            obligation.LastError = ex.Message;

            _logger.LogError(ex,
                "Exception deploying {Role} VM on node {NodeId} (attempt {Count})",
                obligation.Role, node.Id, obligation.FailureCount);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Deploying → Active (or Failed)
    // ════════════════════════════════════════════════════════════════════════

    private async Task CheckDeploymentProgressAsync(
        Node node, SystemVmObligation obligation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(obligation.VmId))
        {
            // VM ID missing — reset to Pending
            obligation.Status = SystemVmStatus.Pending;
            obligation.VmId = null;
            return;
        }

        var vm = await _dataStore.GetVmAsync(obligation.VmId);

        if (vm == null)
        {
            // VM disappeared — reset to Pending for redeployment
            _logger.LogWarning(
                "{Role} VM {VmId} on node {NodeId} disappeared — resetting to Pending",
                obligation.Role, obligation.VmId, node.Id);
            obligation.Status = SystemVmStatus.Pending;
            obligation.VmId = null;
            return;
        }

        if (vm.Status == VmStatus.Running)
        {
            obligation.Status = SystemVmStatus.Active;
            obligation.ActiveAt = DateTime.UtcNow;

            _logger.LogInformation(
                "{Role} VM {VmId} on node {NodeId} is Active",
                obligation.Role, obligation.VmId, node.Id);

            // For Relay: sync RelayInfo status to Active now that VM is Running
            if (obligation.Role == SystemVmRole.Relay && node.RelayInfo != null)
            {
                node.RelayInfo.Status = RelayStatus.Active;
                node.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            }
        }
        else if (vm.Status == VmStatus.Error)
        {
            obligation.Status = SystemVmStatus.Failed;
            obligation.FailureCount++;
            obligation.LastError = vm.StatusMessage;

            _logger.LogWarning(
                "{Role} VM {VmId} on node {NodeId} entered Error state: {Error}",
                obligation.Role, obligation.VmId, node.Id, vm.StatusMessage);
        }
        // else: still Provisioning — wait for next cycle
    }

    // ════════════════════════════════════════════════════════════════════════
    // Failed → Retry (with exponential backoff)
    // ════════════════════════════════════════════════════════════════════════

    private async Task TryRetryAsync(Node node, SystemVmObligation obligation, CancellationToken ct)
    {
        // Exponential backoff: 30s, 60s, 120s, 240s, cap at 5 min
        var backoffSeconds = 30 * Math.Pow(2, Math.Min(obligation.FailureCount - 1, 4));
        var backoff = TimeSpan.FromSeconds(backoffSeconds);

        var lastAttempt = obligation.DeployedAt ?? DateTime.MinValue;
        if (DateTime.UtcNow - lastAttempt < backoff)
            return; // Too soon to retry

        _logger.LogInformation(
            "Retrying {Role} VM on node {NodeId} (attempt {Count}, backoff: {Backoff}s)",
            obligation.Role, node.Id, obligation.FailureCount + 1, backoffSeconds);

        // Reset and try again
        obligation.Status = SystemVmStatus.Pending;
        obligation.VmId = null;
        await TryDeployAsync(node, obligation, ct);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Deployment dispatch (role → service)
    // ════════════════════════════════════════════════════════════════════════

    private async Task<string?> DeploySystemVmAsync(
        Node node, SystemVmRole role, CancellationToken ct)
    {
        return role switch
        {
            SystemVmRole.Relay => await DeployRelayVmAsync(node, ct),
            SystemVmRole.Dht => await DeployDhtVmAsync(node, ct),
            SystemVmRole.BlockStore => await DeployBlockStoreVmAsync(node, ct),
            SystemVmRole.Ingress => await DeployIngressVmAsync(node, ct),
            _ => throw new ArgumentException($"Unknown system VM role: {role}")
        };
    }

    private async Task<string?> DeployRelayVmAsync(Node node, CancellationToken ct)
    {
        // Delegate to existing RelayNodeService
        var vmService = _serviceProvider.GetRequiredService<IVmService>();
        return await _relayNodeService.DeployRelayVmAsync(node, vmService, ct);
    }

    private Task<string?> DeployDhtVmAsync(Node node, CancellationToken ct)
    {
        // TODO: Implement DHT VM deployment when DhtService is available
        _logger.LogDebug("DHT VM deployment not yet implemented for node {NodeId}", node.Id);
        return Task.FromResult<string?>(null);
    }

    private Task<string?> DeployBlockStoreVmAsync(Node node, CancellationToken ct)
    {
        // TODO: Implement BlockStore VM deployment when BlockStoreService is available
        _logger.LogDebug("BlockStore VM deployment not yet implemented for node {NodeId}", node.Id);
        return Task.FromResult<string?>(null);
    }

    private Task<string?> DeployIngressVmAsync(Node node, CancellationToken ct)
    {
        // TODO: Implement Ingress VM deployment when IngressVmService is available
        _logger.LogDebug("Ingress VM deployment not yet implemented for node {NodeId}", node.Id);
        return Task.FromResult<string?>(null);
    }
}
