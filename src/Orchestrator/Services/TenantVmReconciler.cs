using DeCloud.Shared.Contracts;
using DeCloud.Shared.Enums;
using Orchestrator.Interfaces;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Text.Json;

namespace Orchestrator.Services;

/// <summary>
/// Level-triggered reconciler for tenant VM run-state. Every cycle it re-derives
/// what should happen from current state alone — no events, no edges — so an
/// orchestrator restart mid-action changes nothing: the next cycle sees the same
/// state and reaches the same conclusion.
///
/// Two invariants, evaluated in order:
///
///   1. SWEEP — no VM may hold Running status while its node is Offline.
///      Delegates to NodeService.MarkNodeVmsAsErrorAsync (the single
///      implementation; also called explicitly by the compliance cutoff).
///      Idempotent: the method filters on Status==Running, so already-swept
///      VMs drop out. This retires the missed-edge failure where the
///      orchestrator crashed between marking a node Offline and finishing
///      the sweep — CheckNodeHealthAsync only scans Online nodes, so the
///      edge-triggered call could never re-fire for an already-Offline node.
///
///      Suspended nodes are deliberately NOT swept here. Their drain is
///      gradual by design (ScanMigratingVmsAsync's drain branch moves
///      Protected/Replicating VMs off while the node is still alive);
///      collapsing that drain is an explicit admin action
///      (CutoffSuspendedNodeNowAsync), not a standing invariant.
///
///   2. RESUME — a tenant VM whose owner wants it Running (DesiredStatus),
///      that is observed Error/Stopped, on an ONLINE node, with nothing in
///      flight, gets a StartVm command. The node-Online gate is what keeps
///      this loop and the migration scanner from ever racing: offline-node
///      VMs belong to the DR pipeline (RF>0 → migrate; RF0 → Lost, nothing
///      is possible), online-node VMs belong here. Stamping ActiveCommandId
///      would otherwise exclude the VM from the migration scanner's filter
///      and block disaster recovery.
///
/// What this service deliberately does NOT do:
///   - It never calls TransitionAsync for the resume path. Status moves to
///     Running through the existing CommandAck/Heartbeat paths, which carry
///     all the side effects (ingress, ports, billing) already proven correct.
///   - It has no retry/timeout bookkeeping of its own. ActiveCommandId is
///     the in-flight guard, and StaleCommandCleanupService already clears
///     it after 5 minutes for every command type — one janitor, not two.
///   - A crash-looping guest will loop start → crash → start, paced by the
///     reconcile interval plus boot time (~1/min). That is restart=always
///     semantics, visible in the VM's Messages. If damping is ever needed,
///     it belongs here as backoff on re-issue — not as a counter on the node.
/// </summary>
public class TenantVmReconciler : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TenantVmReconciler> _logger;

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    // After an orchestrator restart, node.LastSeenAt is stale until first
    // heartbeats land, so nodes look Offline for up to ~30s. Sweeping during
    // that window would Error healthy VMs (they self-heal via heartbeat, but
    // the ingress/billing churn is avoidable). Same rationale and value as
    // VmSchedulerService's startup delay.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    public TenantVmReconciler(
        IServiceProvider serviceProvider,
        ILogger<TenantVmReconciler> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Tenant VM Reconciler started");

        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                await ReconcileAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in tenant VM reconciliation cycle");
            }

            await Task.Delay(ReconcileInterval, stoppingToken);
        }
    }

    private async Task ReconcileAsync(IServiceProvider services, CancellationToken ct)
    {
        var dataStore = services.GetRequiredService<DataStore>();
        var nodeService = services.GetRequiredService<INodeService>();
        var commandService = services.GetRequiredService<INodeCommandService>();

        var allNodes = await dataStore.GetAllNodesAsync();

        // ── Invariant 1: SWEEP ───────────────────────────────────────────────
        // Offline only — see class doc for why Suspended is excluded.
        foreach (var node in allNodes.Where(n => n.Status == NodeStatus.Offline))
        {
            if (ct.IsCancellationRequested) return;
            await nodeService.MarkNodeVmsAsErrorAsync(node.Id);
        }

        // ── Invariant 2: RESUME ──────────────────────────────────────────────
        var onlineNodeIds = allNodes
            .Where(n => n.Status == NodeStatus.Online)
            .Select(n => n.Id)
            .ToHashSet();

        // Query MongoDB directly for the same reason ScanMigratingVmsAsync does:
        // the in-memory ActiveVMs dict may lag writes that went through the
        // MongoDB-only path (MarkNodeVmsAsErrorAsync, lifecycle transitions).
        var allVms = await dataStore.GetAllVMsAsync();

        var candidates = allVms
            .Where(v =>
                // Tenant VMs only — system VMs have their own node-side
                // reconciler (SystemVmReconciler) as their run-state authority.
                v.Role == VmRole.General &&
                // Owner intent. Null = legacy record with unknown intent —
                // fail closed and skip; one explicit owner Start seeds it.
                v.DesiredStatus == VmStatus.Running &&
                (v.Status == VmStatus.Error || v.Status == VmStatus.Stopped) &&
                // A held VM's run-state belongs to the orchestrator's
                // compliance machinery — never fight the hold.
                !v.ComplianceHold &&
                !string.IsNullOrEmpty(v.NodeId) &&
                // The partition line against the migration scanner: this loop
                // acts only when the VM's node is reachable. Offline-node VMs
                // are the DR pipeline's, and stamping ActiveCommandId here
                // would exclude them from its filter and block migration.
                onlineNodeIds.Contains(v.NodeId) &&
                // In-flight guard. StaleCommandCleanupService clears this
                // after 5 minutes if the command is never acknowledged.
                string.IsNullOrEmpty(v.ActiveCommandId) &&
                // Migration-in-progress guard. MigrateVmAsync stamps both
                // fields; the timeout rollback in CleanupExpiredCommands
                // clears them — so this state is always self-releasing.
                // Post-migration VMs have TargetNodeId == NodeId and pass.
                string.IsNullOrEmpty(v.MigrationSourceNodeId) &&
                (string.IsNullOrEmpty(v.TargetNodeId) || v.TargetNodeId == v.NodeId))
            .ToList();

        if (candidates.Count == 0) return;

        _logger.LogInformation(
            "TenantVmReconciler: {Count} VM(s) down with desired=Running on online node(s) — issuing StartVm",
            candidates.Count);

        foreach (var vm in candidates)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await IssueStartVmAsync(vm, dataStore, commandService);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-resume failed for VM {VmId}", vm.Id);
            }
        }
    }

    /// <summary>
    /// Enqueues a StartVm command for one VM, mirroring MigrateVmAsync's
    /// mechanics exactly: re-fetch + idempotency gate, stamp ActiveCommand*,
    /// RegisterCommand, DeliverCommandAsync. Status is deliberately NOT
    /// touched — the CommandAck/Heartbeat paths own that transition.
    /// </summary>
    private async Task IssueStartVmAsync(
        VirtualMachine vm,
        DataStore dataStore,
        INodeCommandService commandService)
    {
        // Re-fetch + idempotency gate — another cycle or a concurrent actor
        // (owner clicking Start, migration scanner after a node flap) may
        // have acted since the scan snapshot.
        var fresh = await dataStore.GetVmAsync(vm.Id);
        if (fresh == null ||
            fresh.ActiveCommandId != null ||
            (fresh.Status != VmStatus.Error && fresh.Status != VmStatus.Stopped) ||
            fresh.DesiredStatus != VmStatus.Running)
        {
            _logger.LogDebug(
                "VM {VmId}: state changed since reconcile scan — skipping", vm.Id);
            return;
        }

        var commandId = Guid.NewGuid().ToString();

        fresh.ActiveCommandId = commandId;
        fresh.ActiveCommandType = NodeCommandType.StartVm;
        fresh.ActiveCommandIssuedAt = DateTime.UtcNow;
        fresh.UpdatedAt = DateTime.UtcNow;
        fresh.PushMessage(
            "Auto-resume: VM is down but desired state is Running — issuing start.",
            VmMessageLevel.Info, "reconciler");

        await dataStore.SaveVmAsync(fresh);

        dataStore.RegisterCommand(
            commandId, fresh.Id, fresh.NodeId!, NodeCommandType.StartVm);

        var command = new NodeCommand(
            CommandId: commandId,
            Type: NodeCommandType.StartVm,
            Payload: JsonSerializer.Serialize(new { VmId = fresh.Id }),
            RequiresAck: true,
            TargetResourceId: fresh.Id
        );

        await commandService.DeliverCommandAsync(fresh.NodeId!, command);

        _logger.LogInformation(
            "Auto-resume StartVm {CommandId} delivered to node {NodeId} for VM {VmId}",
            commandId, fresh.NodeId, fresh.Id);
    }
}
