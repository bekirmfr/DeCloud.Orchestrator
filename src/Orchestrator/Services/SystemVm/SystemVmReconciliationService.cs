using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Background service that converges each node toward its desired system VM state.
///
/// Every 30 seconds, for each online node:
///   1. Ensure obligations exist (backfill legacy nodes, detect new eligible roles)
///   2. Pending obligations with met dependencies → deploy
///   3. Deploying obligations → check if VM is Running + healthy → mark Active
///   4. Active obligations → verify VM still exists (self-heal if gone)
///   5. Failed obligations → retry with exponential backoff
///
/// This is the Kubernetes controller pattern: declare desired state, let a loop
/// converge toward it. Registration is fast (compute + store obligations, deploy
/// what's immediately ready), and the loop handles everything else.
///
/// Self-healing covers:
///   - Legacy nodes registered before the obligation system (empty obligations list)
///   - Capability drift (node gains public IP → now eligible for Relay/Ingress)
///   - Stale Active obligations whose VMs disappeared (node restart, agent update)
/// </summary>
public class SystemVmReconciliationService : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly IRelayNodeService _relayNodeService;
    private readonly IDhtNodeService _dhtNodeService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemVmReconciliationService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public SystemVmReconciliationService(
        DataStore dataStore,
        IRelayNodeService relayNodeService,
        IDhtNodeService dhtNodeService,
        IServiceProvider serviceProvider,
        ILogger<SystemVmReconciliationService> logger)
    {
        _dataStore = dataStore;
        _relayNodeService = relayNodeService;
        _dhtNodeService = dhtNodeService;
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
                    await EnsureObligationsAsync(node, ct);
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
                    await VerifyActiveAsync(node, obligation, ct);
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

            // Sync role-specific info to Active now that VM is Running
            if (obligation.Role == SystemVmRole.Relay && node.RelayInfo != null)
            {
                node.RelayInfo.Status = RelayStatus.Active;
                node.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            }
            else if (obligation.Role == SystemVmRole.Dht && node.DhtInfo != null)
            {
                node.DhtInfo.Status = DhtStatus.Active;
                node.DhtInfo.LastHealthCheck = DateTime.UtcNow;
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
    // Obligation backfill & drift detection
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensure a node's obligation list reflects its current capabilities.
    /// Handles two cases:
    ///   1. Legacy nodes with an empty obligations list (registered before the obligation system)
    ///   2. Capability drift (e.g., node gained a public IP and is now eligible for Relay)
    /// New obligations are added as Pending. Existing obligations are never removed
    /// (removal would require draining VMs, which is a separate operation).
    /// </summary>
    private async Task EnsureObligationsAsync(Node node, CancellationToken ct)
    {
        var requiredRoles = ObligationEligibility.ComputeObligations(node);
        var existingRoles = new HashSet<SystemVmRole>(
            node.SystemVmObligations.Select(o => o.Role));

        var missingRoles = requiredRoles.Where(r => !existingRoles.Contains(r)).ToList();

        if (missingRoles.Count == 0)
            return;

        foreach (var role in missingRoles)
        {
            node.SystemVmObligations.Add(new SystemVmObligation
            {
                Role = role,
                Status = SystemVmStatus.Pending
            });
        }

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Backfilled obligations on node {NodeId}: [{Roles}] " +
            "(total obligations: {Total})",
            node.Id,
            string.Join(", ", missingRoles),
            node.SystemVmObligations.Count);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Active → verify VM still exists (self-heal if gone)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verify that an Active obligation still has a running VM.
    /// Handles node restarts, agent updates, or orphaned obligation state
    /// where the database says Active but the VM no longer exists.
    /// </summary>
    private async Task VerifyActiveAsync(
        Node node, SystemVmObligation obligation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(obligation.VmId))
        {
            // Active with no VM ID — impossible state, reset
            _logger.LogWarning(
                "{Role} obligation on node {NodeId} is Active with no VM ID — resetting to Pending",
                obligation.Role, node.Id);
            ResetObligation(node, obligation);
            return;
        }

        var vm = await _dataStore.GetVmAsync(obligation.VmId);

        if (vm == null)
        {
            _logger.LogWarning(
                "{Role} VM {VmId} on node {NodeId} no longer exists — resetting to Pending for redeployment",
                obligation.Role, obligation.VmId, node.Id);
            ResetObligation(node, obligation);
            return;
        }

        if (vm.Status == VmStatus.Error)
        {
            _logger.LogWarning(
                "{Role} VM {VmId} on node {NodeId} is in Error state — resetting to Pending",
                obligation.Role, obligation.VmId, node.Id);
            ResetObligation(node, obligation);
            return;
        }

        // VM exists and is not in error — still converged
    }

    /// <summary>
    /// Reset an obligation back to Pending so the reconciliation loop
    /// will redeploy it. Clears role-specific info that is stale.
    /// </summary>
    private void ResetObligation(Node node, SystemVmObligation obligation)
    {
        obligation.Status = SystemVmStatus.Pending;
        obligation.VmId = null;
        obligation.ActiveAt = null;
        obligation.DeployedAt = null;

        // Clear stale role-specific info so deployment creates fresh state
        switch (obligation.Role)
        {
            case SystemVmRole.Dht:
                node.DhtInfo = null;
                break;
            case SystemVmRole.Relay:
                node.RelayInfo = null;
                break;
        }
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

    private async Task<string?> DeployDhtVmAsync(Node node, CancellationToken ct)
    {
        var vmService = _serviceProvider.GetRequiredService<IVmService>();
        return await _dhtNodeService.DeployDhtVmAsync(node, vmService, ct);
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
