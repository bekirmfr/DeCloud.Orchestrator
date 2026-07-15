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
/// Three invariants, evaluated in order:
///
///   1. UNSTICK — no VM may carry an ActiveCommandId older than the timeout.
///      Added after incident 2026-07-15 (Finding D): a tenant VM's StartVm
///      command was acked and correctly cleared in the same request, yet the
///      marker reappeared and survived ~11 hours, blocking both this
///      reconciler and the migration scanner (both gate on ActiveCommandId
///      being empty). Root cause, confirmed by reading DataStore.cs directly:
///      the orchestrator keeps an in-memory ActiveVMs cache that (a) loads
///      EVERY non-deleted VM verbatim from Mongo on every restart, Stopped/
///      Error included, and (b) gets pushed back out to Mongo wholesale,
///      once a minute, by a background full-sync (cache always wins). If a
///      restart ever lands while Mongo transiently shows a stale command
///      marker, that snapshot enters the cache and the sync loop re-asserts
///      it over Mongo every 60 seconds afterward — a self-reinforcing loop,
///      not a one-off race. CleanupExpiredCommands does not cover this: its
///      rollback is migration-specific (restores NodeId/TargetNodeId), so a
///      stuck plain StartVm/StopVm is out of its scope entirely. This step
///      fills that gap. Scoped to General-role VMs, matching this
///      reconciler's own remit — System VMs have their own node-side
///      reconciler and are not this service's business.
///
///   2. SWEEP — no VM may hold Running status while its node is Offline.
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
///   3. RESUME — a tenant VM whose owner wants it Running (DesiredStatus),
///      that is observed Error/Stopped, on an ONLINE node, with nothing in
///      flight, gets a StartVm command. The node-Online gate is what keeps
///      this loop and the migration scanner from ever racing: offline-node
///      VMs belong to the DR pipeline (RF>0 → migrate; RF0 → Lost, nothing
///      is possible), online-node VMs belong here. Stamping ActiveCommandId
///      would otherwise exclude the VM from the migration scanner's filter
///      and block disaster recovery. A VM unstuck by invariant 1 this same
///      cycle becomes eligible here on the next cycle (30s later) — the
///      candidate snapshot below is taken once, before UNSTICK's writes;
///      not worth a second fetch for a delay this small against a VM that
///      was already stuck for 5+ minutes.
///
/// What this service deliberately does NOT do:
///   - It never calls TransitionAsync for the resume path. Status moves to
///     Running through the existing CommandAck/Heartbeat paths, which carry
///     all the side effects (ingress, ports, billing) already proven correct.
///   - Every write in this file goes through DataStore.SaveVmAsync, never a
///     raw Mongo update — see invariant 1's note on why a Mongo-only write
///     would be silently undone by the cache/sync-loop within a minute.
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

    // Same 5-minute convention used elsewhere in this codebase (e.g.
    // CleanupExpiredCommands) for "how long is too long for a command to go
    // unacknowledged." No real command — ack or expiry-driven rollback —
    // takes anywhere near this long, so anything older is definitionally
    // stuck, regardless of which mechanism failed to clear it.
    private static readonly TimeSpan StaleCommandTimeout = TimeSpan.FromMinutes(5);

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

        // Fetched once, used by invariants 1 and 3. Queries MongoDB directly
        // for the same reason ScanMigratingVmsAsync does: the in-memory
        // ActiveVMs dict may lag writes that went through the MongoDB-only
        // path (MarkNodeVmsAsErrorAsync, lifecycle transitions) — and, per
        // invariant 1's doc, may itself be holding a stale snapshot loaded
        // at startup. Mongo is the one place both invariants can trust.
        var allVms = await dataStore.GetAllVMsAsync();

        // ── Invariant 1: UNSTICK ─────────────────────────────────────────────
        await UnstickStaleCommandsAsync(allVms, dataStore, ct);

        // ── Invariant 2: SWEEP ───────────────────────────────────────────────
        // Offline only — see class doc for why Suspended is excluded.
        foreach (var node in allNodes.Where(n => n.Status == NodeStatus.Offline))
        {
            if (ct.IsCancellationRequested) return;
            await nodeService.MarkNodeVmsAsErrorAsync(node.Id);
        }

        // ── Invariant 3: RESUME ──────────────────────────────────────────────
        var onlineNodeIds = allNodes
            .Where(n => n.Status == NodeStatus.Online)
            .Select(n => n.Id)
            .ToHashSet();

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
                // In-flight guard. Invariant 1 (UNSTICK, above) clears this
                // after StaleCommandTimeout if no ack ever lands — this
                // reconciler owns its own janitor for its own command type.
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
    /// Clears ActiveCommandId/Type/IssuedAt on any General-role VM where the
    /// marker has outlived StaleCommandTimeout with no ack or expiry ever
    /// having cleared it — see the class doc (invariant 1) for why this
    /// exists and why it must go through SaveVmAsync rather than a direct
    /// Mongo write. Purely corrective: never touches Status or DesiredStatus,
    /// only the command-tracking fields. A VM freed here becomes a RESUME
    /// candidate on the next cycle once it also satisfies that invariant's
    /// own conditions (desired=Running, node online, etc.).
    /// </summary>
    private async Task UnstickStaleCommandsAsync(
        IReadOnlyList<VirtualMachine> allVms,
        DataStore dataStore,
        CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - StaleCommandTimeout;

        var stuck = allVms
            .Where(v =>
                v.Role == VmRole.General &&
                v.ActiveCommandId != null &&
                v.ActiveCommandIssuedAt is { } issuedAt &&
                issuedAt < cutoff)
            .ToList();

        foreach (var vm in stuck)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                // Re-fetch: confirm it's still stuck before touching it — a
                // concurrent ack may have cleared it between the scan above
                // and now. Same idempotency shape as IssueStartVmAsync.
                var fresh = await dataStore.GetVmAsync(vm.Id);
                if (fresh?.ActiveCommandId == null ||
                    fresh.ActiveCommandIssuedAt is not { } freshIssuedAt ||
                    freshIssuedAt >= cutoff)
                {
                    continue;
                }

                var staleCommandId = fresh.ActiveCommandId;
                var staleAge = DateTime.UtcNow - freshIssuedAt;

                fresh.ActiveCommandId = null;
                fresh.ActiveCommandType = null;
                fresh.ActiveCommandIssuedAt = null;
                fresh.UpdatedAt = DateTime.UtcNow;
                fresh.PushMessage(
                    $"Cleared stale command marker ({staleCommandId}, " +
                    $"{staleAge.TotalMinutes:F0}min old) — no ack or expiry ever landed.",
                    VmMessageLevel.Warning, "reconciler");

                // SaveVmAsync, not a raw Mongo update — see class doc,
                // invariant 1. A Mongo-only clear here would be silently
                // reverted by DataStore's periodic cache→Mongo full-sync
                // within 60 seconds if this VM is sitting in ActiveVMs.
                await dataStore.SaveVmAsync(fresh);

                _logger.LogWarning(
                    "TenantVmReconciler: cleared stale ActiveCommandId {CommandId} on VM {VmId} " +
                    "({AgeMinutes:F0}min old, no ack/expiry ever landed)",
                    staleCommandId, fresh.Id, staleAge.TotalMinutes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear stale command marker for VM {VmId}", vm.Id);
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