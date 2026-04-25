using DeCloud.Shared.Models;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Background service that converges each online node toward its desired system VM state.
///
/// Per-cycle steps (every 30 seconds):
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
///   - Lost role info (DhtInfo/RelayInfo null after crash) with VM still running
/// </summary>
public class SystemVmReconciliationService : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly IObligationEligibility _eligibility;
    private readonly IRelayNodeService _relayNodeService;
    private readonly IDhtNodeService _dhtNodeService;
    private readonly IBlockStoreService _blockStoreService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemVmReconciliationService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloudInitReadyTimeout = TimeSpan.FromMinutes(20); // Deploying → reset if not ready
    private static readonly TimeSpan ActiveVmGracePeriod = TimeSpan.FromMinutes(5);  // Active absence before reset
    private static readonly TimeSpan HeartbeatSnapshotTtl = TimeSpan.FromMinutes(2);  // vm.UpdatedAt freshness window
    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StuckDeletingTimeout = TimeSpan.FromMinutes(15);

    public SystemVmReconciliationService(
        DataStore dataStore,
        IObligationEligibility eligibility,
        IRelayNodeService relayNodeService,
        IDhtNodeService dhtNodeService,
        IBlockStoreService blockStoreService,
        IServiceProvider serviceProvider,
        ILogger<SystemVmReconciliationService> logger)
    {
        _dataStore = dataStore;
        _eligibility = eligibility;
        _relayNodeService = relayNodeService;
        _dhtNodeService = dhtNodeService;
        _blockStoreService = blockStoreService;
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

                // FIX 1: Per-node exception isolation.
                // Previously, a single bad node (corrupt document, transient DB error, etc.)
                // would abort the entire foreach, leaving all subsequent nodes unreconciled
                // for the full 30-second cycle. Now each node is independently guarded.
                foreach (var node in nodes.Where(n => n.Status == NodeStatus.Online))
                {
                    try
                    {
                        await EnsureObligationsAsync(node, ct);
                        await ReconcileNodeAsync(node, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error reconciling obligations for node {NodeId} — skipping this cycle",
                            node.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SystemVmReconciliationService outer loop");
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
        // For CGNAT nodes, if CgnatInfo is not
        // yet assigned, GetAdvertiseIp() falls back to the public IP which is
        // unreachable via the overlay — defer until relay assignment is complete.
        var isCgnat = node.RelayInfo == null && node.HardwareInventory.Network.NatType != NatType.None;
        var hasCgnatAssignment = node.CgnatInfo != null;

        if (isCgnat && !hasCgnatAssignment)
        {
            _logger.LogInformation(
                "Deferring {Role} deployment on node {NodeId} — CgnatInfo not yet assigned, " +
                "waiting for relay assignment before deploying overlay-dependent system VMs",
                obligation.Role, node.Id);
            return;
        }

        // Pre-populate node fields from stored obligation state so deployment
        // services reuse the same identity on every redeploy instead of
        // generating fresh credentials that break mesh connectivity.
        HydrateNodeFromObligationState(node, obligation);

        if (!SystemVmDependencies.AreDependenciesMet(obligation.Role, node.SystemVmObligations))
            return; // Dependencies not met yet — will try again next cycle

        // Guard: CGNAT nodes must have a tunnel IP before deploying DHT.
        // Without this, GetAdvertiseIp() returns the unreachable public IP,
        // which gets baked into the DHT VM's cloud-init as dht-advertise-ip.
        // The self-healing in VerifyActiveAsync catches this eventually, but
        // preventing it avoids a wasted deploy cycle and a window where peers
        // receive an unreachable address.
        if (obligation.Role == SystemVmRole.Dht
            && node.IsBehindCgnat
            && string.IsNullOrEmpty(node.CgnatInfo?.TunnelIp))
        {
            _logger.LogDebug(
                "Deferring DHT deploy on CGNAT node {NodeId} — tunnel IP not yet assigned",
                node.Id);
            return;
        }

        // Guard: skip if another obligation for the same role is already Deploying or Active.
        // This prevents duplicate VMs when registration and the background loop race.
        var alreadyDeployed = node.SystemVmObligations.Any(o =>
            o != obligation &&
            o.Role == obligation.Role &&
            (o.Status == SystemVmStatus.Deploying || o.Status == SystemVmStatus.Active));

        if (alreadyDeployed)
        {
            _logger.LogDebug(
                "Skipping {Role} deploy on node {NodeId} — another obligation for this role is already Deploying/Active",
                obligation.Role, node.Id);
            return;
        }

        // Belt-and-suspenders: check the datastore for an existing VM of the same
        // type on this node. Covers orphaned VMs not tracked by obligations (e.g.,
        // after a failed redeployment where the node agent rejected the duplicate
        // but the old VM was already transitioned to Deleting). Without this, the
        // reconciliation loop creates new VM records that the node agent rejects,
        // leaving orphaned ghost records in the orchestrator DB.
        if (obligation.Role is SystemVmRole.Dht or SystemVmRole.Relay or SystemVmRole.BlockStore)
        {
            var vmType = obligation.Role switch
            {
                SystemVmRole.Dht => VmType.Dht,
                SystemVmRole.Relay => VmType.Relay,
                SystemVmRole.BlockStore => VmType.BlockStore,
                _ => VmType.Dht
            };
            var nodeVms = await _dataStore.GetVmsByNodeAsync(node.Id);

            // Clean up stale Provisioning VMs: CreateVm ACK was never received (node
            // rebooted, timed out). SyncVmStateFromHeartbeat skips Provisioning VMs
            // (transitional guard) so they never leave that state. After ProvisioningTimeout
            // they must be cleaned up or they block TryDeployAsync via the else branch.
            var staleProvisioningVms = nodeVms
                .Where(v => v.Spec.VmType == vmType
                         && v.Status == VmStatus.Provisioning
                         && DateTime.UtcNow - v.UpdatedAt >= ProvisioningTimeout)
                .ToList();

            // Clean up stale Error VMs: accumulate from failed deployments.
            // FirstOrDefault returning one causes the else branch to block indefinitely.
            var staleErrorVms = nodeVms
                .Where(v => v.Spec.VmType == vmType
                         && v.Status == VmStatus.Error
                         && DateTime.UtcNow - v.UpdatedAt >= HeartbeatSnapshotTtl)
                .ToList();

            var staleVmsToClean = staleProvisioningVms.Concat(staleErrorVms).ToList();

            if (staleVmsToClean.Any())
            {
                var vmService = _serviceProvider.GetRequiredService<IVmService>();
                foreach (var staleVm in staleVmsToClean)
                {
                    try
                    {
                        await vmService.DeleteVmAsync(staleVm.Id);
                        _logger.LogInformation(
                            "Sent DeleteVm command for stale {Status} {Role} VM {VmId} on node {NodeId} " +
                            "(stuck for {Min:F0}m)",
                            staleVm.Status, obligation.Role, staleVm.Id, node.Id,
                            (DateTime.UtcNow - staleVm.UpdatedAt).TotalMinutes);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Could not queue deletion for stale {Status} VM {VmId}",
                            staleVm.Status, staleVm.Id);
                    }
                }

                // Exclude now-Deleting VMs from the blocking check.
                nodeVms = nodeVms
                    .Where(v => !staleVmsToClean.Any(e => e.Id == v.Id))
                    .ToList();
            }

            var existingVm = nodeVms.FirstOrDefault(v =>
                v.Spec.VmType == vmType &&
                v.Status is VmStatus.Running or VmStatus.Provisioning or VmStatus.Deleting);

            if (existingVm != null)
            {
                if (existingVm.Status == VmStatus.Running)
                {
                    if (existingVm.IsFullyReady)
                    {
                        obligation.VmId = existingVm.Id;
                        obligation.Status = SystemVmStatus.Active;
                        obligation.ActiveAt = DateTime.UtcNow;
                        obligation.FailureCount = 0;
                        obligation.LastError = null;

                        SyncRoleServiceInfo(node, obligation);

                        _logger.LogInformation(
                            "Re-adopted existing {Role} VM {VmId} on node {NodeId} as Active " +
                            "(Running + IsFullyReady at adoption time)",
                            obligation.Role, existingVm.Id, node.Id);

                        return;
                    }

                    // VM is Running but services are not ready — send a delete command
                    // so the node agent destroys the libvirt domain before we deploy a
                    // replacement. Without this, the node agent's duplicate-name guard
                    // rejects the new CreateVm command because the old VM still exists
                    // with the same name.
                    _logger.LogWarning(
                        "Existing {Role} VM {VmId} on node {NodeId} is Running but not fully ready " +
                        "— sending delete command before deploying replacement",
                        obligation.Role, existingVm.Id, node.Id);

                    try
                    {
                        var vmService = _serviceProvider.GetRequiredService<IVmService>();
                        var deleted = await vmService.DeleteVmAsync(existingVm.Id);

                        if (!deleted)
                        {
                            _logger.LogWarning(
                                "Failed to delete unhealthy {Role} VM {VmId} on node {NodeId} — " +
                                "skipping deployment to avoid duplicate name conflict, will retry next cycle",
                                obligation.Role, existingVm.Id, node.Id);
                            return;
                        }

                        _logger.LogInformation(
                            "Unhealthy {Role} VM {VmId} on node {NodeId} deletion initiated — " +
                            "will deploy replacement once node confirms deletion",
                            obligation.Role, existingVm.Id, node.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Exception deleting unhealthy {Role} VM {VmId} on node {NodeId} — " +
                            "skipping deployment, will retry next cycle",
                            obligation.Role, existingVm.Id, node.Id);
                    }

                    // Return — let deletion propagate to the node agent before redeploying.
                    // Next cycle: VM will be Deleting or gone, falling through to DeploySystemVmAsync.
                    return;
                }
                else if (existingVm.Status == VmStatus.Deleting && existingVm.PowerState == VmPowerState.Running)
                {
                    // VM is Deleting but still running — false-positive, recover it.
                    _logger.LogWarning(
                        "Recovering {Role} VM {VmId} on node {NodeId} from false-positive Deleting " +
                        "(PowerState=Running) — transitioning back to Running",
                        obligation.Role, existingVm.Id, node.Id);

                    var lifecycle = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
                    var recovered = await lifecycle.TransitionAsync(
                        existingVm.Id,
                        VmStatus.Running,
                        new TransitionContext
                        {
                            Trigger = TransitionTrigger.Manual,
                            Source = "SystemVmReconciliationService.TryDeployAsync",
                            StatusMessage = "Recovered from false-positive Deleting state"
                        });

                    if (recovered)
                    {
                        obligation.VmId = existingVm.Id;
                        obligation.Status = SystemVmStatus.Active;
                        obligation.ActiveAt = DateTime.UtcNow;

                        _logger.LogInformation(
                            "Successfully recovered {Role} VM {VmId} on node {NodeId} — " +
                            "status restored to Running, ingress re-registered",
                            obligation.Role, existingVm.Id, node.Id);
                    }
                    return;
                }
                else if (existingVm.Status == VmStatus.Deleting
                    && string.IsNullOrEmpty(existingVm.ActiveCommandId))
                {
                    // Deleting with no active command — VM is an orphan in MongoDB.
                    // TryRetryAsync transitioned it to Deleting but no DeleteVm command
                    // was ever sent or acknowledged. The node never deleted it; this record
                    // will block redeployment forever. Treat it as gone and proceed.
                    var stuckFor = DateTime.UtcNow - existingVm.UpdatedAt;
                    _logger.LogWarning(
                        "{Role} VM {VmId} on node {NodeId} stuck Deleting for {Min:F0}m " +
                        "with no active command — treating as orphan, proceeding with deployment",
                        obligation.Role, existingVm.Id, node.Id, stuckFor.TotalMinutes);

                    try
                    {
                        var lifecycle = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
                        await lifecycle.TransitionAsync(existingVm.Id, VmStatus.Deleted,
                            TransitionContext.Manual(
                                "Orphaned Deleting VM — no active command, presumed gone on node"));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Could not transition orphan {Role} VM {VmId} to Deleted — proceeding anyway",
                            obligation.Role, existingVm.Id);
                    }
                    // Fall through to DeploySystemVmAsync
                }
                else
                {
                    // Provisioning or other transient state — wait for next cycle
                    _logger.LogDebug(
                        "Skipping {Role} deploy on node {NodeId} — existing VM {VmId} in state {Status}",
                        obligation.Role, node.Id, existingVm.Id, existingVm.Status);
                    return;
                }
            }
        }

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

                // Sync auth token from the newly deployed VM's labels
                // so /join authentication works with the new VM's token
                var newVm = await _dataStore.GetVmAsync(vmId);
                var newAuthToken = newVm?.Labels?.GetValueOrDefault("blockstore-auth-token")
                               ?? newVm?.Labels?.GetValueOrDefault("dht-auth-token");
                if (!string.IsNullOrEmpty(newAuthToken))
                    obligation.AuthToken = newAuthToken;

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
            obligation.Status = SystemVmStatus.Pending;
            return;
        }

        var vm = await _dataStore.GetVmAsync(obligation.VmId);

        // Self-heal: if VM exists but NodeId is empty, patch it now.
        // NodeId is required for SyncVmStateFromHeartbeatAsync to find
        // and update the VM's service status via heartbeat.
        if (vm != null && string.IsNullOrEmpty(vm.NodeId))
        {
            vm.NodeId = node.Id;
            await _dataStore.SaveVmAsync(vm);
            _logger.LogInformation(
                "{Role} VM {VmId} had empty NodeId — patched to {NodeId}",
                obligation.Role, vm.Id, node.Id);
        }

        // Ground truth: was this VM reported healthy in the last heartbeat?
        // vm.LastHeartbeatAt is stamped by SyncVmStateFromHeartbeatAsync on every heartbeat cycle.
        // Running + IsFullyReady + fresh LastHeartbeatAt = node confirmed cloud-init complete.
        // Null check: LastHeartbeatAt is null until the first heartbeat arrives after boot.
        var isHealthy = vm != null
            && vm.Status == VmStatus.Running
            && vm.IsFullyReady
            && vm.LastHeartbeatAt.HasValue
            && DateTime.UtcNow - vm.LastHeartbeatAt.Value < HeartbeatSnapshotTtl;

        if (isHealthy)
        {
            obligation.Status = SystemVmStatus.Active;
            obligation.ActiveAt = DateTime.UtcNow;
            obligation.FailureCount = 0;
            obligation.LastError = null;

            if (obligation.CurrentBinaryVersion is not null)
                obligation.RunningBinaryVersion = obligation.CurrentBinaryVersion;

            _logger.LogInformation(
                "{Role} VM {VmId} on node {NodeId} is Active (System service Ready)",
                obligation.Role, obligation.VmId, node.Id);

            SyncRoleServiceInfo(node, obligation);
            return;
        }

        // Not yet healthy — apply CloudInitReadyTimeout clock from DeployedAt.
        // This handles all failure modes uniformly: binary extraction failed,
        // guest agent never started, domain never created, node offline mid-boot.
        if (obligation.DeployedAt == null)
        {
            obligation.DeployedAt = DateTime.UtcNow;
            await _dataStore.SaveNodeAsync(node);
        }

        var elapsed = DateTime.UtcNow - obligation.DeployedAt.Value;
        if (elapsed > CloudInitReadyTimeout)
        {
            _logger.LogWarning(
                "{Role} VM {VmId} on node {NodeId} not healthy after {Min:F0}m " +
                "(vm: {VmStatus}, ready: {Ready}) — resetting to Pending for redeployment",
                obligation.Role, obligation.VmId, node.Id, elapsed.TotalMinutes,
                vm?.Status.ToString() ?? "null",
                vm?.IsFullyReady.ToString() ?? "null");
            ResetObligation(node, obligation);
            return;
        }

        _logger.LogDebug(
            "{Role} VM {VmId} on node {NodeId} deploying " +
            "({Elapsed:F0}m / {Timeout:F0}m timeout, vm: {VmStatus})",
            obligation.Role, obligation.VmId, node.Id,
            elapsed.TotalMinutes, CloudInitReadyTimeout.TotalMinutes,
            vm?.Status.ToString() ?? "null");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Active → verify VM still healthy via heartbeat
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verify that an Active obligation's VM is still being reported healthy
    /// by the node agent via heartbeat. Uses vm.LastHeartbeatAt as the freshness
    /// signal — SyncVmStateFromHeartbeatAsync stamps it on every heartbeat cycle.
    /// A stale LastHeartbeatAt means the node stopped reporting this VM.
    /// </summary>
    private async Task VerifyActiveAsync(
        Node node, SystemVmObligation obligation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(obligation.VmId))
        {
            _logger.LogWarning(
                "{Role} obligation on node {NodeId} is Active with no VM ID — resetting to Pending",
                obligation.Role, node.Id);
            ResetObligation(node, obligation);
            return;
        }

        // Binary update check — unchanged.
        // Relay excluded: its runtime hash is incompatible with the node-side hash.
        if (obligation.Role != SystemVmRole.Relay
            && obligation.RunningBinaryVersion is not null
            && obligation.CurrentBinaryVersion is not null
            && obligation.CurrentBinaryVersion != obligation.RunningBinaryVersion)
        {
            await RedeploySystemVmAsync(node, obligation,
                $"Binary updated: {obligation.RunningBinaryVersion[..8]} → {obligation.CurrentBinaryVersion[..8]}");
            return;
        }

        var vm = await _dataStore.GetVmAsync(obligation.VmId);

        // Ground truth: was this VM reported healthy in the last heartbeat?
        // vm.LastHeartbeatAt is stamped by SyncVmStateFromHeartbeatAsync every 15s.
        // If the node is alive and the VM is running, LastHeartbeatAt is always fresh.
        // Null check: LastHeartbeatAt is null until the first heartbeat arrives after boot.
        var isHealthy = vm != null
            && vm.Status == VmStatus.Running
            && vm.IsFullyReady
            && vm.LastHeartbeatAt.HasValue
            && DateTime.UtcNow - vm.LastHeartbeatAt.Value < HeartbeatSnapshotTtl;

        if (isHealthy)
        {
            SyncRoleServiceInfo(node, obligation);
            return;
        }

        // VM not healthy — apply grace period before resetting.
        // Protects against transient absence: node agent restart (30-60s),
        // network blip, orchestrator processing lag.
        // vm.LastHeartbeatAt is the last heartbeat timestamp for this VM (null until
        // first heartbeat); fall back to obligation.ActiveAt if missing or not yet set.
        var lastSeen = vm?.LastHeartbeatAt ?? obligation.ActiveAt ?? DateTime.UtcNow;
        var unhealthyFor = DateTime.UtcNow - lastSeen;

        // If the VM record was last updated more than HeartbeatSnapshotTtl ago and
        // the node just came back online (LastSeenAt is stale), this is an
        // offline-detection Error — the VMs may still be running on the node.
        // Wait for the first heartbeat to restore true VM state before resetting.
        var nodeJustReconnected = node.LastHeartbeat.HasValue
            && DateTime.UtcNow - node.LastHeartbeat.Value > HeartbeatSnapshotTtl
            && unhealthyFor > ActiveVmGracePeriod;

        if (nodeJustReconnected)
        {
            _logger.LogInformation(
                "{Role} VM {VmId} on node {NodeId} appears unhealthy but node just reconnected " +
                "(last heartbeat: {Min:F1}m ago) — waiting for first heartbeat to restore state",
                obligation.Role, obligation.VmId, node.Id,
                (DateTime.UtcNow - node.LastHeartbeat.Value).TotalMinutes);
            return;
        }

        if (unhealthyFor < ActiveVmGracePeriod)
        {
            _logger.LogDebug(
                "{Role} VM {VmId} on node {NodeId} not healthy " +
                "(vm: {VmStatus}, ready: {Ready}, age: {Age:F1}m) — within grace period",
                obligation.Role, obligation.VmId, node.Id,
                vm?.Status.ToString() ?? "null",
                vm?.IsFullyReady.ToString() ?? "null",
                unhealthyFor.TotalMinutes);
            return;
        }

        _logger.LogWarning(
            "{Role} VM {VmId} on node {NodeId} not healthy for {Min:F0}m " +
            "(vm: {VmStatus}, ready: {Ready}) — resetting to Pending for redeployment",
            obligation.Role, obligation.VmId, node.Id, unhealthyFor.TotalMinutes,
            vm?.Status.ToString() ?? "null",
            vm?.IsFullyReady.ToString() ?? "null");

        ResetObligation(node, obligation);
    }

    /// <summary>
    /// Sync role-specific service info to reflect the current Active obligation.
    /// Only sets VmId, Status, and LastHealthCheck — service-specific fields
    /// (ListenAddress, PeerId, CapacityBytes) come from /join callbacks and
    /// must not be overwritten here.
    /// </summary>
    private static void SyncRoleServiceInfo(Node node, SystemVmObligation obligation)
    {
        switch (obligation.Role)
        {
            case SystemVmRole.Relay:
                node.RelayInfo ??= new RelayNodeInfo();
                node.RelayInfo.RelayVmId = obligation.VmId ?? string.Empty;
                node.RelayInfo.Status = RelayStatus.Active;
                node.RelayInfo.LastHealthCheck = DateTime.UtcNow;
                break;

            case SystemVmRole.Dht:
                node.DhtInfo ??= new DhtNodeInfo();
                node.DhtInfo.DhtVmId = obligation.VmId ?? string.Empty;
                node.DhtInfo.Status = DhtStatus.Active;
                node.DhtInfo.LastHealthCheck = DateTime.UtcNow;
                // Do NOT set ListenAddress or PeerId — set by /api/dht/join callback
                break;

            case SystemVmRole.BlockStore:
                node.BlockStoreInfo ??= new BlockStoreInfo();
                node.BlockStoreInfo.BlockStoreVmId = obligation.VmId ?? string.Empty;
                node.BlockStoreInfo.Status = BlockStoreStatus.Active;
                node.BlockStoreInfo.LastHealthCheck = DateTime.UtcNow;
                // Do NOT set ListenAddress, PeerId, or CapacityBytes — set by /api/blockstore/join callback
                break;
        }
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

        // Clean up the old failed VM before deploying a replacement.
        // Without this, the old Error-state VM lingers with reserved resources,
        // causing resource drift and duplicate VMs.
        if (!string.IsNullOrEmpty(obligation.VmId))
        {
            try
            {
                var lifecycle = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
                await lifecycle.TransitionAsync(obligation.VmId, VmStatus.Deleting,
                    TransitionContext.Manual("Cleaning up failed system VM before retry"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to clean up old {Role} VM {VmId} on node {NodeId} — proceeding with retry",
                    obligation.Role, obligation.VmId, node.Id);
            }
        }

        // Reset and try again
        obligation.Status = SystemVmStatus.Pending;
        obligation.VmId = null;
        await TryDeployAsync(node, obligation, ct);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Obligation backfill & drift detection
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Public entry point for on-demand reconciliation (e.g., after re-evaluation).
    /// Equivalent to one background loop iteration for a single node:
    /// ensures obligations reflect current capabilities, then reconciles state.
    /// </summary>
    public async Task EnsureAndReconcileAsync(Node node, CancellationToken ct = default)
    {
        await EnsureObligationsAsync(node, ct);
        await ReconcileNodeAsync(node, ct);
    }

    /// <summary>
    /// Ensure a node's obligation list reflects its current capabilities.
    /// Handles three cases:
    ///   1. Legacy nodes with an empty obligations list (registered before the obligation system)
    ///   2. Capability drift (e.g., node gained a public IP and is now eligible for Relay)
    ///   3. Adopting existing VMs — legacy nodes may already have running VMs (via RelayInfo/DhtInfo)
    ///      that were deployed before the obligation system. These are adopted as Active instead
    ///      of creating duplicate deployments.
    /// Existing obligations are never removed (removal would require draining VMs).
    /// </summary>
    private async Task EnsureObligationsAsync(Node node, CancellationToken ct)
    {
        // Skip nodes registered within the last 60 seconds — registration
        // seeds and reconciles obligations atomically. Racing with it causes
        // duplicate system VM deployments.
        if ((DateTime.UtcNow - node.RegisteredAt).TotalSeconds < 60)
        {
            _logger.LogDebug(
                "Skipping EnsureObligations for recently registered node {NodeId} (age: {Age:F0}s)",
                node.Id, (DateTime.UtcNow - node.RegisteredAt).TotalSeconds);
            return;
        }

        var requiredRoles = _eligibility.ComputeObligations(node);
        var existingRoles = new HashSet<SystemVmRole>(
            node.SystemVmObligations.Select(o => o.Role));

        var missingRoles = requiredRoles.Where(r => !existingRoles.Contains(r)).ToList();

        if (missingRoles.Count == 0)
            return;

        foreach (var role in missingRoles)
        {
            var adopted = await TryAdoptExistingVmAsync(node, role, ct);
            node.SystemVmObligations.Add(adopted);

            if (adopted.Status == SystemVmStatus.Active)
            {
                _logger.LogInformation(
                    "Adopted existing {Role} VM {VmId} on node {NodeId} as Active obligation",
                    role, adopted.VmId, node.Id);
            }
            else if (adopted.Status == SystemVmStatus.Deploying)
            {
                _logger.LogInformation(
                    "Adopted existing {Role} VM {VmId} on node {NodeId} as Deploying (VM status: not yet Running)",
                    role, adopted.VmId, node.Id);
            }
        }

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Backfilled obligations on node {NodeId}: [{Roles}] " +
            "(total obligations: {Total})",
            node.Id,
            string.Join(", ", missingRoles),
            node.SystemVmObligations.Count);
    }

    /// <summary>
    /// Check if a node already has a VM for a role (deployed before the obligation
    /// system existed). If so, adopt it — but only mark Active if the VM actually
    /// exists in the datastore and is Running. VMs in other states are adopted as
    /// Deploying so they flow through CheckDeploymentProgressAsync normally.
    /// If the referenced VM no longer exists, fall back to Pending.
    /// </summary>
    private async Task<SystemVmObligation> TryAdoptExistingVmAsync(
        Node node, SystemVmRole role, CancellationToken ct)
    {
        // Check role-specific info for an existing VM ID
        string? existingVmId = role switch
        {
            SystemVmRole.Relay => node.RelayInfo?.RelayVmId,
            SystemVmRole.Dht => node.DhtInfo?.DhtVmId,
            SystemVmRole.BlockStore => node.BlockStoreInfo?.BlockStoreVmId,
            _ => null
        };

        // Fallback: search the datastore for a healthy VM of the correct type
        if (existingVmId == null)
        {
            existingVmId = await TryDiscoverHealthySystemVmAsync(node, role);
        }

        if (existingVmId == null)
        {
            return new SystemVmObligation
            {
                Role = role,
                Status = SystemVmStatus.Pending
            };
        }

        // Verify the VM actually exists before adopting
        var vm = await _dataStore.GetVmAsync(existingVmId);
        if (vm == null)
        {
            _logger.LogWarning(
                "Node {NodeId} has {Role} VM ID {VmId} in role info but VM not found in datastore — creating Pending obligation",
                node.Id, role, existingVmId);

            // Clear stale role info pointing to a non-existent VM
            if (role == SystemVmRole.Relay) node.RelayInfo = null;
            if (role == SystemVmRole.Dht) node.DhtInfo = null;

            return new SystemVmObligation
            {
                Role = role,
                Status = SystemVmStatus.Pending
            };
        }

        // VM exists — adopt based on actual status
        if (vm.Status == VmStatus.Running)
        {
            // Sync role-specific status to Active
            if (role == SystemVmRole.Relay && node.RelayInfo != null)
            {
                node.RelayInfo.Status = RelayStatus.Active;
                node.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            }
            else if (role == SystemVmRole.Dht && node.DhtInfo != null)
            {
                node.DhtInfo.Status = DhtStatus.Active;
                node.DhtInfo.LastHealthCheck = DateTime.UtcNow;
            }

            return new SystemVmObligation
            {
                Role = role,
                VmId = existingVmId,
                Status = SystemVmStatus.Active,
                ActiveAt = DateTime.UtcNow
            };
        }

        if (vm.Status == VmStatus.Error)
        {
            _logger.LogWarning(
                "Node {NodeId} has {Role} VM {VmId} in Error state — adopting as Failed",
                node.Id, role, existingVmId);

            return new SystemVmObligation
            {
                Role = role,
                VmId = existingVmId,
                Status = SystemVmStatus.Failed,
                FailureCount = 1,
                LastError = vm.StatusMessage ?? "VM in Error state at adoption"
            };
        }

        // Provisioning, Stopped, or other non-terminal state — adopt as Deploying
        // so CheckDeploymentProgressAsync handles the transition
        return new SystemVmObligation
        {
            Role = role,
            VmId = existingVmId,
            Status = SystemVmStatus.Deploying,
            DeployedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Fallback discovery when role info (DhtInfo/RelayInfo) is lost but a system VM
    /// may still be running on the node. Searches the datastore for a Running VM of
    /// the correct type that is confirmed alive via recent service health checks.
    /// Reconstructs role info from the discovered VM so the system self-heals.
    ///
    /// Only DHT and BlockStore VMs can be fully reconstructed — Relay VMs require
    /// WireGuard keys that aren't stored on the VM record, so they fall through to
    /// fresh deployment.
    /// </summary>
    private async Task<string?> TryDiscoverHealthySystemVmAsync(Node node, SystemVmRole role)
    {
        if (role != SystemVmRole.Dht && role != SystemVmRole.BlockStore) return null;

        // BlockStore VM recovery: reconstruct BlockStoreInfo from a running VM
        if (role == SystemVmRole.BlockStore)
        {
            var bsVms = await _dataStore.GetVmsByNodeAsync(node.Id);
            var bsCandidate = bsVms.FirstOrDefault(v =>
                v.Spec.VmType == VmType.BlockStore &&
                v.Status == VmStatus.Running &&
                v.IsFullyReady &&
                v.Services.Any(s => s.LastCheckAt.HasValue &&
                    (DateTime.UtcNow - s.LastCheckAt.Value).TotalMinutes <= 5));

            if (bsCandidate == null) return null;

            _logger.LogInformation(
                "Discovered healthy BlockStore VM {VmId} on node {NodeId} via datastore fallback",
                bsCandidate.Id, node.Id);

            var bsAdvertiseIp = bsCandidate.Labels?.GetValueOrDefault("blockstore-advertise-ip")
                             ?? DhtNodeService.GetAdvertiseIp(node);

            node.BlockStoreInfo = new BlockStoreInfo
            {
                BlockStoreVmId = bsCandidate.Id,
                ListenAddress = $"{bsAdvertiseIp}:{BlockStoreVmSpec.BitswapPort}",
                ApiPort = BlockStoreVmSpec.ApiPort,
                Status = BlockStoreStatus.Active,
                LastHealthCheck = DateTime.UtcNow,
            };

            if (long.TryParse(bsCandidate.Labels?.GetValueOrDefault("blockstore-storage-bytes"), out var bsCap))
                node.BlockStoreInfo.CapacityBytes = bsCap;

            return bsCandidate.Id;
        }

        // DHT VM recovery
        var nodeVms = await _dataStore.GetVmsByNodeAsync(node.Id);
        var candidate = nodeVms.FirstOrDefault(v =>
            v.Spec.VmType == VmType.Dht &&
            v.Status == VmStatus.Running &&
            v.IsFullyReady &&
            v.Services.Any(s => s.LastCheckAt.HasValue &&
                (DateTime.UtcNow - s.LastCheckAt.Value).TotalMinutes <= 5));

        if (candidate == null) return null;

        _logger.LogInformation(
            "Discovered healthy DHT VM {VmId} on node {NodeId} via datastore fallback — " +
            "DhtInfo was lost but VM is alive and passing health checks",
            candidate.Id, node.Id);

        // Reconstruct DhtInfo from the discovered VM.
        // Prefer the advertise IP baked into the VM's labels (which may be a WG tunnel IP)
        // over GetAdvertiseIp(node) which only handles CGNAT tunnel IPs, not the WG mesh
        // override that DhtNodeService.DeployDhtVmAsync applies for co-located relay nodes.
        var advertiseIp = candidate.Labels?.GetValueOrDefault("dht-advertise-ip")
            ?? DhtNodeService.GetAdvertiseIp(node);

        node.DhtInfo = new DhtNodeInfo
        {
            DhtVmId = candidate.Id,
            ListenAddress = $"{advertiseIp}:{DhtNodeService.DhtListenPort}",
            ApiPort = DhtNodeService.DhtApiPort,
            Status = DhtStatus.Active,
            LastHealthCheck = DateTime.UtcNow,
        };

        return candidate.Id;
    }

    /// <summary>
    /// Begin redeployment of a DHT VM by transitioning it to Deleting.
    /// The obligation is NOT reset here — it stays Active so VerifyActiveAsync
    /// can track the delete flow: Deleting → node confirms → Deleted → reset → redeploy.
    /// This prevents deploying a new VM while the old one still exists on the node.
    /// </summary>
    private Task RedeployDhtVmAsync(Node node, SystemVmObligation obligation, string reason)
        => RedeploySystemVmAsync(node, obligation, reason);

    private async Task RedeploySystemVmAsync(Node node, SystemVmObligation obligation, string reason)
    {
        var vm = await _dataStore.GetVmAsync(obligation.VmId);
        if (vm == null || vm.Status != VmStatus.Running)
        {
            _logger.LogDebug(
                "Skipping redeploy of {Role} VM {VmId} on node {NodeId} — VM is not Running (status: {Status})",
                obligation.Role, obligation.VmId, node.Id, vm?.Status);
            return;
        }

        _logger.LogInformation(
            "{Role} VM {VmId} on node {NodeId} transitioning to Deleting — {Reason}",
            obligation.Role, obligation.VmId, node.Id, reason);

        try
        {
            var lifecycle = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
            await lifecycle.TransitionAsync(obligation.VmId, VmStatus.Deleting,
                TransitionContext.Manual(reason));

            _logger.LogInformation(
                "{Role} VM {VmId} on node {NodeId} transitioning to Deleting — " +
                "obligation stays Active until node confirms deletion",
                obligation.Role, obligation.VmId, node.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to transition {Role} VM {VmId} on node {NodeId} to Deleting — will retry next cycle",
                obligation.Role, obligation.VmId, node.Id);
        }
        // Obligation stays Active — VerifyActiveAsync handles the rest
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
                // Preserve WireGuard keypair — DHT and BlockStore VMs have the relay's
                // public key baked into their wg-mesh.conf at cloud-init time.
                // Reusing the same keypair on redeploy means all mesh-enrolled VMs
                // remain connected without cascading redeployment.
                if (node.RelayInfo != null)
                {
                    node.RelayInfo.RelayVmId = string.Empty;
                    node.RelayInfo.Status = RelayStatus.Initializing;
                    // Preserved: WireGuardPrivateKey, WireGuardPublicKey, TunnelIp,
                    //            RelaySubnet, WireGuardEndpoint, Region
                }
                break;
            case SystemVmRole.BlockStore:
                node.BlockStoreInfo = null;
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Obligation state hydration — pre-populate node fields from stored state
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads identity from <paramref name="obligation"/>.StateJson and writes it
    /// onto the node model so deployment services pick it up without changing
    /// their own signatures.
    ///
    /// Relay  → node.RelayInfo.WireGuardPrivateKey / PublicKey / TunnelIp / RelaySubnet
    ///           The existing reuse guard in RelayNodeService.DeployRelayVmAsync
    ///           already checks node.RelayInfo?.WireGuardPrivateKey, so hydrating
    ///           it here is all that is required.
    ///
    /// Dht    → obligation.AuthToken
    ///           DhtNodeService reads node.SystemVmObligations to find this value.
    ///
    /// BlockStore → obligation.AuthToken
    ///           BlockStoreService reads node.SystemVmObligations to find this value.
    ///
    /// Safe to call on first deploy (StateJson null/empty → method returns immediately,
    /// deployment services generate fresh credentials as before).
    /// </summary>
    private void HydrateNodeFromObligationState(Node node, SystemVmObligation obligation)
    {
        if (string.IsNullOrEmpty(obligation.StateJson))
            return;

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            switch (obligation.Role)
            {
                // ── Relay ────────────────────────────────────────────────
                case SystemVmRole.Relay:
                    {
                        var state = System.Text.Json.JsonSerializer
                            .Deserialize<RelayObligationState>(obligation.StateJson, jsonOptions);

                        if (state is null) return;

                        node.RelayInfo ??= new RelayNodeInfo();

                        // These are the exact three fields the existing
                        // RelayNodeService reuse guard reads.
                        node.RelayInfo.WireGuardPrivateKey = state.WireGuardPrivateKey;
                        node.RelayInfo.WireGuardPublicKey = state.WireGuardPublicKey;
                        node.RelayInfo.TunnelIp = state.TunnelIp;

                        // Subnet slot: "10.20.5.0/24" → 5
                        var slot = ParseRelaySubnetSlot(state.RelaySubnet);
                        if (slot > 0)
                            node.RelayInfo.RelaySubnet = slot;

                        _logger.LogDebug(
                            "Hydrated Relay state v{Version} onto node {NodeId} " +
                            "(pubKey: {PubKey}, subnet: {Subnet})",
                            state.Version, node.Id,
                            state.WireGuardPublicKey.Length > 12
                                ? state.WireGuardPublicKey[..12] + "..."
                                : state.WireGuardPublicKey,
                            state.RelaySubnet);
                        break;
                    }

                // ── DHT ──────────────────────────────────────────────────
                case SystemVmRole.Dht:
                    {
                        var state = System.Text.Json.JsonSerializer
                            .Deserialize<DhtObligationState>(obligation.StateJson, jsonOptions);

                        if (state is null) return;

                        // AuthToken lives on the obligation itself — DhtNodeService
                        // reads node.SystemVmObligations to get it.
                        if (!string.IsNullOrEmpty(state.AuthToken))
                            obligation.AuthToken = state.AuthToken;

                        _logger.LogDebug(
                            "Hydrated DHT state v{Version} onto obligation for node {NodeId} " +
                            "(peerId: {PeerId})",
                            state.Version, node.Id,
                            state.PeerId.Length > 12
                                ? state.PeerId[..12] + "..."
                                : state.PeerId);
                        break;
                    }

                // ── BlockStore ───────────────────────────────────────────
                case SystemVmRole.BlockStore:
                    {
                        var state = System.Text.Json.JsonSerializer
                            .Deserialize<BlockStoreObligationState>(obligation.StateJson, jsonOptions);

                        if (state is null) return;

                        if (!string.IsNullOrEmpty(state.AuthToken))
                            obligation.AuthToken = state.AuthToken;

                        _logger.LogDebug(
                            "Hydrated BlockStore state v{Version} onto obligation for node {NodeId} " +
                            "(peerId: {PeerId})",
                            state.Version, node.Id,
                            state.PeerId.Length > 12
                                ? state.PeerId[..12] + "..."
                                : state.PeerId);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — deployment proceeds with freshly generated credentials.
            // Logs at Warning so operators can investigate if identity drift occurs.
            _logger.LogWarning(ex,
                "Could not deserialise obligation state for {Role} on node {NodeId} " +
                "(StateVersion: {Version}) — deploying with freshly generated identity",
                obligation.Role, node.Id, obligation.StateVersion);
        }
    }

    /// <summary>
    /// Extract the /24 slot integer from a relay subnet CIDR string.
    /// "10.20.5.0/24" → 5.  Returns 0 on any parse failure.
    /// </summary>
    private static int ParseRelaySubnetSlot(string? relaySubnet)
    {
        if (string.IsNullOrEmpty(relaySubnet))
            return 0;

        // "10.20.{slot}.0/24" — the slot is the third octet
        var withoutCidr = relaySubnet.Split('/')[0];
        var octets = withoutCidr.Split('.');
        return octets.Length >= 3 && int.TryParse(octets[2], out var slot) ? slot : 0;
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
        var vmService = _serviceProvider.GetRequiredService<IVmService>();
        return await _relayNodeService.DeployRelayVmAsync(node, vmService, ct);
    }

    private async Task<string?> DeployDhtVmAsync(Node node, CancellationToken ct)
    {
        var vmService = _serviceProvider.GetRequiredService<IVmService>();
        return await _dhtNodeService.DeployDhtVmAsync(node, vmService, ct);
    }

    private async Task<string?> DeployBlockStoreVmAsync(Node node, CancellationToken ct)
    {
        var vmService = _serviceProvider.GetRequiredService<IVmService>();
        return await _blockStoreService.DeployBlockStoreVmAsync(node, vmService, ct);
    }

    private Task<string?> DeployIngressVmAsync(Node node, CancellationToken ct)
    {
        // TODO: Implement Ingress VM deployment when IngressVmService is available
        _logger.LogDebug("Ingress VM deployment not yet implemented for node {NodeId}", node.Id);
        return Task.FromResult<string?>(null);
    }
}