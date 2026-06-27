using Orchestrator.Persistence;
using Orchestrator.Models;
using System.Text.Json;
using Orchestrator.Interfaces.VmScheduling;
using Orchestrator.Interfaces;
using DeCloud.Shared.Enums;

namespace Orchestrator.Services;

/// <summary>
/// Background service that checks node health periodically
/// </summary>
public class NodeHealthMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NodeHealthMonitorService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);

    public NodeHealthMonitorService(
        IServiceProvider serviceProvider,
        ILogger<NodeHealthMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Node Health Monitor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var nodeService = scope.ServiceProvider.GetRequiredService<INodeService>();
                
                await nodeService.CheckNodeHealthAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking node health");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}

/// <summary>
/// Scans for VMs stranded on offline nodes and triggers migration to a healthy node.
///
/// Runs every 10 seconds (existing _scheduleInterval). New-VM scheduling (which
/// needs plain-text passwords) remains commented out below.
/// </summary>
public class VmSchedulerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VmSchedulerService> _logger;
    private readonly TimeSpan _scheduleInterval = TimeSpan.FromSeconds(10);

    public VmSchedulerService(
        IServiceProvider serviceProvider,
        ILogger<VmSchedulerService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VmSchedulerService started (migration scan active)");

        // 2-minute startup delay — let NodeHealthMonitor mark offline nodes first.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                await ScanMigratingVmsAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "VmSchedulerService scan failed");
            }

            await Task.Delay(_scheduleInterval, stoppingToken);
        }

        _logger.LogInformation("VmSchedulerService stopped");

        // ── Original new-VM scheduler (needs plain-text passwords — cannot run here) ──
        /*
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();
                await vmService.SchedulePendingVmsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling VMs");
            }
            await Task.Delay(_scheduleInterval, stoppingToken);
        }
        */
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Migration scan
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds VMs in Error + {Migrating | Recovering} with no command in flight
    /// and issues a CreateVm command to the best available target node.
    /// </summary>
    private async Task ScanMigratingVmsAsync(IServiceProvider services, CancellationToken ct)
    {
        var dataStore = services.GetRequiredService<DataStore>();
        var blockStoreService = services.GetRequiredService<IBlockStoreService>();
        var schedulingService = services.GetRequiredService<IVmSchedulingService>();
        var commandService = services.GetRequiredService<INodeCommandService>();

        // Query MongoDB directly — the in-memory ActiveVMs dict may not reflect
        // LazysyncStatus updates written by MarkNodeVmsAsErrorAsync, which fetches
        // and saves VMs via MongoDB bypassing the in-memory write path.
        var allVms = await dataStore.GetAllVMsAsync();

        // Gate migration on node-offline status rather than LazysyncStatus.
        // LazysyncStatus is informational and prone to billing-race overwrites.
        // The correct semantic: migrate Error VMs whose host node is confirmed offline.
        // MigrateVmAsync checks manifest state downstream and marks Unrecoverable if
        // no confirmed replica exists.
        // Nodes whose VMs must be evacuated: offline (disaster recovery) and
        // compliance-suspended (operator blocked — drain replicated VMs to clean
        // nodes). The same migration pipeline serves both; only the trigger differs.
        var allNodesForScan = await dataStore.GetAllNodesAsync();
        var evacuateNodeIds = allNodesForScan
            .Where(n => n.Status == NodeStatus.Offline || n.Status == NodeStatus.Suspended)
            .Select(n => n.Id)
            .ToHashSet();
        var suspendedNodeIds = allNodesForScan
            .Where(n => n.Status == NodeStatus.Suspended)
            .Select(n => n.Id)
            .ToHashSet();

        var candidates = allVms
            .Where(v =>
                v.Status == VmStatus.Error &&
                // A compliance-held VM must never be auto-revived on another node.
                // Migration re-creates AND starts the VM on the target, and the hold is
                // node-local persisted state that does not travel in CreateVmPayload — so a
                // migrated held VM would come up running. Held VMs stay out of the pipeline;
                // their data is already replicated, and normal scheduling re-places them once
                // an admin lifts the hold.
                !v.ComplianceHold &&
                v.Spec.ReplicationFactor > 0 &&
                !string.IsNullOrEmpty(v.NodeId) &&
                evacuateNodeIds.Contains(v.NodeId) &&
                string.IsNullOrEmpty(v.ActiveCommandId) &&
                // Unrecoverable is a terminal, deliberate classification (no confirmed
                // replica), written only by ClassifyOfflineVm and MigrateVmAsync — never
                // on the volatile manifest-push/audit path. Excluding it here is safe
                // (unlike the other LazysyncStatus values) and makes the existing
                // "exits the filter permanently" contract in MigrateVmAsync actually true.
                v.LazysyncStatus != LazysyncStatus.Unrecoverable)
            .ToList();

        if (candidates.Count == 0) return;

        _logger.LogInformation(
            "VmSchedulerService: {Count} VM(s) pending migration", candidates.Count);

        foreach (var vm in candidates)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await MigrateVmAsync(vm, dataStore, blockStoreService, schedulingService, commandService, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Migration failed for VM {VmId}", vm.Id);
            }
        }

        // ── Compliance drain ─────────────────────────────────────────────────
        // Suspended-operator nodes: evacuate their replicated tenant VMs to clean
        // nodes via the migration pipeline above. These VMs are still Running on the
        // (online) suspended node; transition them to Error so the next scan migrates
        // each from its last confirmed replica. Only VMs that already hold a confirmed
        // replica (Protected/Replicating) are drained — unconfirmed ones are left to
        // finish seeding on the still-alive source and picked up on a later cycle.
        // Ephemeral (factor 0) VMs are not drainable: they stay running and resolve to
        // Lost at hard cutoff (slice 3). Held VMs are already Stopped, so the Running
        // filter skips them.
        if (suspendedNodeIds.Count > 0)
        {
            var drainCandidates = allVms
                .Where(v =>
                    v.Status == VmStatus.Running &&
                    v.Role == VmRole.General &&
                    v.Spec.ReplicationFactor > 0 &&
                    !v.ComplianceHold &&
                    !string.IsNullOrEmpty(v.NodeId) &&
                    suspendedNodeIds.Contains(v.NodeId) &&
                    string.IsNullOrEmpty(v.ActiveCommandId) &&
                    (v.LazysyncStatus == LazysyncStatus.Protected ||
                     v.LazysyncStatus == LazysyncStatus.Replicating))
                .ToList();

            if (drainCandidates.Count > 0)
            {
                _logger.LogInformation(
                    "VmSchedulerService: draining {Count} replicated VM(s) off suspended node(s)",
                    drainCandidates.Count);

                var drainLifecycle = services.GetRequiredService<IVmLifecycleManager>();

                foreach (var vm in drainCandidates)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        vm.PushMessage(
                            "Operator suspended — migrating to another node.",
                            VmMessageLevel.Warning, "compliance");

                        await drainLifecycle.TransitionAsync(
                            vm.Id,
                            VmStatus.Error,
                            TransitionContext.Compliance("operator suspended — draining node"));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to transition VM {VmId} to Error for compliance drain", vm.Id);
                    }
                }
            }
        }

        // ── Compliance migration ─────────────────────────────────────────────
        // VMs flagged non-compliant by FlagNonCompliantVmsAsync during
        // re-registration. These are Running on an online node — the node
        // itself is fine, but its new locality violates the VM's constraints.
        //
        // Transition to Error to enter the existing migration pipeline.
        // The migration will select a compliant target node via the same
        // constraint-aware scheduling chain.
        var complianceCandidates = allVms
            .Where(v =>
                v.NonCompliantSince != null &&
                v.Status == VmStatus.Running &&
                v.Role == VmRole.General &&
                // Defense-in-depth: a held VM is force-stopped (so not Running) and must
                // never be re-created on another node — make the exclusion explicit, to
                // match the offline-DR and compliance-drain candidate filters.
                !v.ComplianceHold &&
                string.IsNullOrEmpty(v.ActiveCommandId))
            .ToList();

        if (complianceCandidates.Count > 0)
        {
            _logger.LogInformation(
                "VmSchedulerService: {Count} VM(s) non-compliant, transitioning for migration",
                complianceCandidates.Count);

            var lifecycleManager = services.GetRequiredService<IVmLifecycleManager>();

            foreach (var vm in complianceCandidates)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    _logger.LogInformation(
                        "VM {VmId} ({VmName}) non-compliant since {Since}: {Reason}. " +
                        "Transitioning to Error for migration.",
                        vm.Id, vm.Name, vm.NonCompliantSince, vm.NonComplianceReason);

                    vm.PushMessage(
                        $"Non-compliant: {vm.NonComplianceReason}. Scheduling migration.",
                        VmMessageLevel.Warning, "scheduler");

                    await lifecycleManager.TransitionAsync(
                        vm.Id,
                        VmStatus.Error,
                        TransitionContext.Compliance(vm.NonComplianceReason));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to transition non-compliant VM {VmId} to Error",
                        vm.Id);
                }
            }
        }

        // ── Compliance hard cutoff (operator-node takedown, slice 3) ──────────
        // A suspended node is cut off once it has nothing left to drain: no
        // confirmed-replica VM still on it, nothing in the migration pipeline for it,
        // and no in-flight migration sourced from it. What remains then is ephemeral
        // (→ Lost) and replicas that never confirmed (→ Unrecoverable) — neither leaves
        // via migration. CutoffSuspendedNodeAsync re-checks the manifest before the
        // irreversible deregister, so a stale LazysyncStatus cannot cause premature
        // cutoff. Unconfirmed replicas do not block cutoff (lost here, as in a crash).
        if (suspendedNodeIds.Count > 0)
        {
            var nodeService = services.GetRequiredService<INodeService>();
            foreach (var nodeId in suspendedNodeIds)
            {
                if (ct.IsCancellationRequested) break;

                var stillDraining = allVms.Any(v =>
                    v.Role == VmRole.General &&
                    v.Spec.ReplicationFactor > 0 &&
                    !v.ComplianceHold &&
                    v.LazysyncStatus != LazysyncStatus.Unrecoverable &&
                    (
                        (v.NodeId == nodeId && v.Status == VmStatus.Running &&
                            (v.LazysyncStatus == LazysyncStatus.Protected ||
                             v.LazysyncStatus == LazysyncStatus.Replicating)) ||
                        (v.NodeId == nodeId && (v.Status == VmStatus.Error ||
                                                v.Status == VmStatus.Provisioning)) ||
                        (v.MigrationSourceNodeId == nodeId &&
                            !string.IsNullOrEmpty(v.ActiveCommandId))
                    ));

                if (stillDraining) continue;

                try
                {
                    await nodeService.CutoffSuspendedNodeAsync(nodeId, "operator blocked", ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Compliance hard cutoff failed for node {NodeId}", nodeId);
                }
            }
        }
    }

    private async Task MigrateVmAsync(
        VirtualMachine vm,
        DataStore dataStore,
        IBlockStoreService blockStoreService,
        IVmSchedulingService schedulingService,
        INodeCommandService commandService,
        CancellationToken ct)
    {
        // ── Step 1: Get manifest from blockstore ─────────────────────────────
        var manifest = await blockStoreService.GetMigrationManifestAsync(vm.Id, ct);
        if (manifest == null)
        {
            _logger.LogWarning("VM {VmId}: no confirmed replica — marking Unrecoverable", vm.Id);
            var unrecoverable = await dataStore.GetVmAsync(vm.Id);
            if (unrecoverable != null)
            {
                unrecoverable.LazysyncStatus = LazysyncStatus.Unrecoverable; // ← exits the filter permanently
                unrecoverable.PushMessage(
                    "No confirmed replica exists — VM cannot be migrated automatically. " +
                    "Redeployment from scratch required.",
                    VmMessageLevel.Error, "scheduler");
                unrecoverable.UpdatedAt = DateTime.UtcNow;
                await dataStore.SaveVmAsync(unrecoverable);
            }
            return;
        }

        // ── Step 2: Select target node ───────────────────────────────────────
        // Region/zone constraints are already in vm.Spec.Constraints (lowered
        // at VM creation). Architecture stickiness is added as an ephemeral
        // constraint on a spec clone — a VM that booted on x86_64 has its
        // overlay disk filled with x86_64 binaries; migration must target
        // the same architecture. The constraint is NOT persisted on the VM.
        var sourceNode = await dataStore.GetNodeAsync(vm.NodeId ?? "");
        Node? targetNode;

        var originalConstraints = vm.Spec.Constraints;
        vm.Spec.Constraints = (originalConstraints ?? new List<Constraint>())
            .Append(new Constraint
            {
                Target = ConstraintTargets.Node.Architecture,
                Operator = ConstraintOperators.Eq,
                Value = sourceNode?.Architecture ?? "x86_64"
            })
            .ToList();

        try
        {
            targetNode = await schedulingService.SelectBestNodeForVmAsync(
                vm.Spec,
                vm.Spec.QualityTier,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VM {VmId}: scheduling service failed during migration", vm.Id);
            return;
        }
        finally
        {
            // Restore original constraints — the architecture stickiness
            // constraint is migration-specific and must not persist on the VM.
            vm.Spec.Constraints = originalConstraints;
        }

        if (targetNode == null)
        {
            _logger.LogWarning("VM {VmId}: no eligible migration target", vm.Id);

            var strandedVm = await dataStore.GetVmAsync(vm.Id);
            if (strandedVm != null)
            {
                // Derive a region hint from the locality constraint if one is set,
                // so the status message remains informative without reading flat fields.
                var regionConstraint = vm.Spec.Constraints?
                    .FirstOrDefault(c => c.Target == ConstraintTargets.Node.Locality.Region
                    && c.Operator == ConstraintOperators.Eq);
                var regionHint = regionConstraint != null
                    ? $" in region '{regionConstraint.Value}'"
                    : "";
                var hasConstraints = vm.Spec.Constraints is { Count: > 0 };

                var newMessage =
                    $"Waiting for an available node{regionHint}. " +
                    "Migration will proceed automatically when one becomes available" +
                    (hasConstraints
                        ? ", or update your scheduling constraints to broaden eligibility."
                        : ".");

                if (strandedVm.StatusMessage != newMessage)
                {
                    strandedVm.PushMessage(newMessage, VmMessageLevel.Warning, "scheduler");
                    strandedVm.UpdatedAt = DateTime.UtcNow;
                    await dataStore.SaveVmAsync(strandedVm);
                }
            }
            return;
        }

        // ── Step 2.5: Pre-flight DHT check (Phase D) ─────────────────────────
        // The blockstore mesh handles ongoing repair (Phase B + C), but the
        // orchestrator's audit now runs on a 6-hour sentinel cadence for steady-
        // state manifests. By migration time, the ConfirmedChunkMap snapshot
        // could be up to 6h stale — some CIDs we expect to fetch might have
        // lost all providers without the orchestrator noticing. A one-shot DHT
        // walk before authority transfer catches this and defers the migration
        // cleanly rather than committing to a partial reconstruction on the target.
        //
        // Uses the TARGET's DHT VM (the source is offline by definition). The
        // target's DHT walk is also the most accurate predictor of what its
        // blockstore will see when fetching via bitswap.
        var preflightOk = await MigrationPreflightAsync(
            targetNode, manifest.ChunkMap, vm.Id, ct);
        if (!preflightOk)
        {
            _logger.LogWarning(
                "VM {VmId}: pre-flight DHT check failed — too many ChunkMap CIDs " +
                "have no remote providers. Deferring migration to next scan cycle.",
                vm.Id);
            var deferred = await dataStore.GetVmAsync(vm.Id);
            if (deferred != null)
            {
                deferred.PushMessage(
                    "Pre-flight check failed: too many block CIDs have no remote providers. " +
                    "The blockstore mesh may not have repaired in time. Retrying next cycle.",
                    VmMessageLevel.Warning, "scheduler");
                deferred.UpdatedAt = DateTime.UtcNow;
                await dataStore.SaveVmAsync(deferred);
            }
            return;
        }

        var migrationStatus = manifest.ConfirmedVersion == manifest.CurrentVersion
            ? "Ready"
            : "ReadyWithDataLoss";

        // ── Re-fetch + idempotency gate ──────────────────────────────────────
        // Another scan cycle or concurrent process may have already acted on this VM.
        var fresh = await dataStore.GetVmAsync(vm.Id);
        if (fresh == null ||
            fresh.ActiveCommandId != null ||
            fresh.Status != VmStatus.Error)
        {
            _logger.LogDebug("VM {VmId}: state changed since scan — skipping", vm.Id);
            return;
        }

        _logger.LogInformation(
            "VM {VmId} ({LazysyncStatus}): {SourceNode} → {TargetNode} " +
            "confirmedV={CV} ({MigrationStatus})",
            fresh.Id, fresh.LazysyncStatus,
            fresh.NodeId ?? "none", targetNode.Id,
            manifest.ConfirmedVersion, migrationStatus);

        // ── Atomic authority transfer ────────────────────────────────────────
        // NodeId is advanced to the target optimistically so the target's
        // success-heartbeat is adopted (orchestrator ownership check) rather than
        // flagged as a zombie. MigrationSourceNodeId records the origin so the
        // transfer can be rolled back if the create fails — see the failure
        // branch in ProcessCommandAcknowledgmentAsync and CleanupExpiredCommands.
        var sourceNodeId = fresh.NodeId;
        var commandId = Guid.NewGuid().ToString();

        fresh.MigrationSourceNodeId = sourceNodeId;
        fresh.NodeId = targetNode.Id;
        fresh.TargetNodeId = targetNode.Id;
        fresh.Status = VmStatus.Provisioning;
        fresh.LazysyncStatus = LazysyncStatus.Migrating;
        fresh.ActiveCommandId = commandId;
        fresh.ActiveCommandType = NodeCommandType.CreateVm;
        fresh.ActiveCommandIssuedAt = DateTime.UtcNow;
        fresh.UpdatedAt = DateTime.UtcNow;
        // Clear stale IP/access info from the source node. WaitForPrivateIpAsync in
        // OnVmBecameRunningAsync will immediately return true on a non-null PrivateIp,
        // causing RegisterVmAsync to build a Caddy upstream pointing to the old host's
        // VM IP. The correct IP arrives from the target node's first heartbeat.
        fresh.NetworkConfig ??= new VmNetworkConfig();
        fresh.NetworkConfig.PrivateIp = null;
        fresh.NetworkConfig.IsIpAssigned = false;
        fresh.AccessInfo = null;
        fresh.PushMessage(
            $"Migrating to node {targetNode.Id} " +
            $"(confirmedV={manifest.ConfirmedVersion}, {migrationStatus}).",
            VmMessageLevel.Info, "scheduler");

        await dataStore.SaveVmAsync(fresh);

        dataStore.RegisterCommand(
            commandId, fresh.Id, targetNode.Id, NodeCommandType.CreateVm);

        // ── SSH key ──────────────────────────────────────────────────────────
        string? sshPublicKey = fresh.Spec.SshPublicKey;
        if (string.IsNullOrEmpty(sshPublicKey) &&
            !string.IsNullOrEmpty(fresh.OwnerId) &&
            dataStore.Users.TryGetValue(fresh.OwnerId, out var owner) &&
            owner.SshKeys.Any())
        {
            sshPublicKey = string.Join("\n", owner.SshKeys.Select(k => k.PublicKey));
        }

        // See VmService.TryScheduleVmAsync for rationale.
        // Two scheduler entry points; both stamp.
        fresh.Spec.SshPublicKey = sshPublicKey;

        // ── CreateVm command ─────────────────────────────────────────────────
        // Build through CreateVmPayload — the single source of truth, mirroring
        // VmService.CreateVmAsync. The migration is signalled implicitly by
        // ManifestRootCid + ChunkMap; cloud-init is left null so LibvirtVmManager
        // STEP 6 synthesises the minimal migration config. Enum encoding is owned
        // by the enum [JsonConverter]s — no hand-casting to int.
        var createPayload = new CreateVmPayload
        {
            VmId = fresh.Id,
            Name = fresh.Name,
            Category = fresh.Category,
            Role = fresh.Role,

            OwnerId = fresh.OwnerId,
            OwnerWallet = fresh.OwnerWallet,

            VirtualCpuCores = fresh.Spec.VirtualCpuCores,
            MemoryBytes = fresh.Spec.MemoryBytes,
            DiskBytes = fresh.Spec.DiskBytes,
            QualityTier = fresh.Spec.QualityTier,
            ComputePointCost = fresh.Spec.ComputePointCost,

            GpuMode = fresh.Spec.GpuMode,
            GpuVramBytes = fresh.Spec.GpuVramBytes,

            DeploymentMode = fresh.Spec.DeploymentMode,
            ContainerImage = fresh.Spec.ContainerImage,
            // Forward the base image identity recorded at first deploy. URL is
            // the fetch hint; hash is the contract the target enforces. Empty
            // hash means the source's first deploy predated content-addressed
            // base images — the target will still verify the URL download as
            // best it can, but cross-node byte identity is not guaranteed.
            // See BASE_IMAGE_DESIGN.md §4.2.
            BaseImageUrl = fresh.Spec.BaseImageUrl,
            BaseImageHash = fresh.Spec.BaseImageHash,
            SshPublicKey = sshPublicKey ?? "",

            ReplicationFactor = fresh.Spec.ReplicationFactor,
            TargetNodeId = targetNode.Id,

            ManifestRootCid = manifest.ConfirmedRootCid,
            ChunkMap = manifest.ChunkMap,
        };

        var command = new NodeCommand(
            CommandId: commandId,
            Type: NodeCommandType.CreateVm,
            Payload: JsonSerializer.Serialize(createPayload),
            RequiresAck: true,
            TargetResourceId: fresh.Id
        );

        await commandService.DeliverCommandAsync(targetNode.Id, command);

        _logger.LogInformation(
            "Migration command {CommandId} delivered to node {NodeId} for VM {VmId}",
            commandId, targetNode.Id, fresh.Id);
    }

    /// <summary>
    /// Pre-flight DHT walk against the migration target. Samples 50 random CIDs
    /// from the ChunkMap and verifies the target's DHT VM can FindProviders for
    /// each. At a 90% pass threshold, the false-positive rate (deferring a
    /// healthy manifest) is negligible; the false-negative rate (allowing a
    /// degraded manifest through) is bounded by what bitswap retry can absorb
    /// during target-side reconstruction.
    ///
    /// Step 1 (added 2026-05-31): Before the DHT walk, ask the target's
    /// blockstore directly which CIDs it already holds for this VM. Phase B/C
    /// mesh replication writes /owners/{vmId} on every receiver, so this is the
    /// authoritative source for "does the target have the blocks." If the
    /// target already holds the ChunkMap CIDs locally, no DHT lookup is needed
    /// — bitswap reconstruction on the target is a local read. This path is
    /// essential when the mesh collapses to two nodes and the source dies: the
    /// surviving target's DHT routing table empties (Finding 1 then returns
    /// HTTP 503 on /providers), and the DHT walk would defer the migration
    /// indefinitely even though the target holds every required block.
    ///
    /// Returns true (allow) if the target has no DHT VM at all — we'd rather
    /// migrate optimistically than block on missing infrastructure metadata.
    /// Returns false (defer) if every sampled CID returns indeterminate — we
    /// can't validate at all, so committing to authority transfer is unsafe.
    /// </summary>
    private async Task<bool> MigrationPreflightAsync(
        Node targetNode,
        Dictionary<long, string> chunkMap,
        string vmId,
        CancellationToken ct)
    {
        const int SampleSize = 50;
        const double MinPassRate = 0.90;

        var allCids = chunkMap.Values
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();

        if (allCids.Count == 0)
        {
            return true; // empty manifest — let downstream handle it
        }

        // ── Step 1: Direct blockstore inventory check ──────────────────────────
        // Authoritative ground truth: the target's blockstore knows what it has.
        // Short-circuits the DHT walk in cases where the DHT view is degraded
        // but Phase B/C replication has already placed the blocks on the target.
        var localOk = await TryDirectBlockstorePreflightAsync(
            targetNode, allCids, vmId, MinPassRate, ct);
        if (localOk == true)
        {
            return true;
        }

        // ── Step 2: DHT walk fallback (original logic) ─────────────────────────
        if (targetNode.DhtInfo == null || string.IsNullOrEmpty(targetNode.DhtInfo.ListenAddress))
        {
            _logger.LogWarning(
                "VM {VmId}: target node {NodeId} has no DHT VM — skipping pre-flight",
                vmId, targetNode.Id);
            return true;
        }

        var ip = targetNode.DhtInfo.ListenAddress.Split(':')[0];
        var dhtApiUrl = $"http://{ip}:8080"; // dht-dashboard.py proxy

        var cids = allCids
            .OrderBy(_ => Random.Shared.Next())
            .Take(SampleSize)
            .ToList();

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        var passed = 0;
        var failed = 0;
        var indeterminate = 0;

        await Parallel.ForEachAsync(
            cids,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (cid, token) =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));

                    var url = $"{dhtApiUrl}/providers/{Uri.EscapeDataString(cid)}";
                    using var response = await httpClient.GetAsync(url, cts.Token);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                    var hasProviders = json.TryGetProperty("providers", out var providersEl)
                        && providersEl.ValueKind == JsonValueKind.Array
                        && providersEl.GetArrayLength() > 0;

                    if (hasProviders)
                        Interlocked.Increment(ref passed);
                    else
                        Interlocked.Increment(ref failed);
                }
                catch
                {
                    // HTTP failure (including 503 cold-DHT from Finding 1).
                    // Excluded from both numerator and denominator — same
                    // semantic as LazysyncManager's indeterminate path.
                    Interlocked.Increment(ref indeterminate);
                }
            });

        var checkedTotal = passed + failed;
        if (checkedTotal == 0)
        {
            _logger.LogWarning(
                "VM {VmId}: pre-flight all {Count} sampled CIDs indeterminate — " +
                "target DHT unreachable",
                vmId, indeterminate);
            return false;
        }

        var passRate = (double)passed / checkedTotal;
        _logger.LogInformation(
            "VM {VmId}: pre-flight {Passed}/{Checked} passed ({Indeterminate} indeterminate, {Rate:P0})",
            vmId, passed, checkedTotal, indeterminate, passRate);

        return passRate >= MinPassRate;
    }

    /// <summary>
    /// Asks the target's blockstore directly which CIDs it already holds for this VM,
    /// via GET /owners/{vmId} on the blockstore HTTP API. Returns true if the target
    /// holds at least minPassRate of the ChunkMap CIDs locally — a stronger signal
    /// than DHT FindProviders for the "can target reconstruct" question, because it
    /// removes the DHT lookup as a dependency in exactly the case where the DHT has
    /// collapsed but the target still has the data.
    ///
    /// Returns false if the blockstore is reachable but coverage is below threshold,
    /// or null if the blockstore is unreachable / BlockStoreInfo is missing.
    /// In both non-true cases the caller falls through to the DHT walk.
    /// </summary>
    private async Task<bool?> TryDirectBlockstorePreflightAsync(
        Node targetNode,
        List<string> chunkMapCids,
        string vmId,
        double minPassRate,
        CancellationToken ct)
    {
        if (targetNode.BlockStoreInfo == null
            || string.IsNullOrEmpty(targetNode.BlockStoreInfo.ListenAddress))
        {
            return null;
        }

        var ip = targetNode.BlockStoreInfo.ListenAddress.Split(':')[0];
        var port = targetNode.BlockStoreInfo.ApiPort > 0
            ? targetNode.BlockStoreInfo.ApiPort
            : 5090;
        var hasUrl = $"http://{ip}:{port}/blocks/has";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            // POST /blocks/has: raw CID presence check independent of ownership
            // metadata. Blocks fetched via reconstruction (GET /blocks/{cid}?owner=)
            // or bitswap are physically present even if not indexed under /owners/{vmId}.
            var requestBody = JsonContent.Create(new { cids = chunkMapCids });
            using var response = await httpClient.PostAsync(hasUrl, requestBody, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "VM {VmId}: /blocks/has returned {Status} — falling through to DHT walk",
                    vmId, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
            if (!json.TryGetProperty("present", out var presentEl)
                || presentEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var held = presentEl.GetArrayLength();
            var holdRate = (double)held / chunkMapCids.Count;

            if (holdRate >= minPassRate)
            {
                _logger.LogInformation(
                    "VM {VmId}: pre-flight passed via direct blockstore check — " +
                    "target holds {Held}/{Total} ChunkMap CIDs locally ({Rate:P0})",
                    vmId, held, chunkMapCids.Count, holdRate);
                return true;
            }

            _logger.LogInformation(
                "VM {VmId}: target blockstore holds {Held}/{Total} ChunkMap CIDs " +
                "locally ({Rate:P0}, threshold {MinRate:P0}) — falling through to DHT walk",
                vmId, held, chunkMapCids.Count, holdRate, minPassRate);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "VM {VmId}: target blockstore pre-flight unreachable at {Url} — " +
                "falling through to DHT walk",
                vmId, hasUrl);
            return null;
        }
    }
}

/// <summary>
/// Background service to clean up deleted VMs
/// </summary>
public class CleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _deletedRetention = TimeSpan.FromDays(7);

    public CleanupService(
        IServiceProvider serviceProvider,
        ILogger<CleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cleanup Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dataStore = scope.ServiceProvider.GetRequiredService<Persistence.DataStore>();
                
                await TrimEventHistoryAsync(dataStore);
                await CleanupStaleCommandAcks(dataStore);
                await CleanupExpiredCommands(dataStore);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Handle commands that have been waiting for ack too long (5 minutes default)
    /// </summary>
    private async Task CleanupExpiredCommands(DataStore dataStore)
    {
        var timeout = TimeSpan.FromMinutes(5);
        var now = DateTime.UtcNow;

        var timedOutCommands = dataStore.PendingCommandAcks.Values
            .Where(cmd => cmd.Age > timeout)
            .ToList();

        foreach (var command in timedOutCommands)
        {
            _logger.LogWarning(
                "Command {CommandId} ({Type}) for resource {ResourceId} timed out after {Age}s - no acknowledgment received",
                command.CommandId, command.Type, command.TargetResourceId, command.Age.TotalSeconds);

            // Mark associated VM as error if it exists and is in a waiting state
            var vm = await dataStore.GetVmAsync(command.TargetResourceId);
            if (!string.IsNullOrEmpty(command.TargetResourceId) &&
                vm != null)
            {
                if (vm.Status == VmStatus.Provisioning || vm.Status == VmStatus.Deleting)
                {
                    var lifecycleManager = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
                    await lifecycleManager.TransitionAsync(
                        vm.Id,
                        VmStatus.Error,
                        TransitionContext.Timeout(
                            command.Type.ToString(),
                            $"{command.Type} timed out - no acknowledgment received from node after {timeout.TotalMinutes} minutes"));

                    _logger.LogWarning(
                        "Marked VM {VmId} as Error due to command timeout",
                        vm.Id);

                    // Roll back a migration authority transfer that timed out with no
                    // ack (target went silent). Treat as retryable: restore NodeId to
                    // the source so the scan re-evaluates next cycle. Re-fetch after
                    // TransitionAsync to avoid a stale overwrite.
                    var timedOutVm = await dataStore.GetVmAsync(vm.Id);
                    if (timedOutVm != null &&
                        !string.IsNullOrEmpty(timedOutVm.MigrationSourceNodeId))
                    {
                        timedOutVm.NodeId = timedOutVm.MigrationSourceNodeId;
                        timedOutVm.TargetNodeId = null;
                        timedOutVm.MigrationSourceNodeId = null;
                        timedOutVm.PushMessage(
                            $"Migration timed out with no acknowledgment. " +
                            "Returned to the migration queue.",
                            VmMessageLevel.Warning, "migration");
                        timedOutVm.UpdatedAt = DateTime.UtcNow;
                        await dataStore.SaveVmAsync(timedOutVm);
                    }
                }

                // Always clear ActiveCommandId after timeout — regardless of prior status.
                // If not cleared, the migration scanner's ActiveCommandId == null filter
                // permanently excludes this VM and it can never be re-attempted.
                var vmAfterTimeout = await dataStore.GetVmAsync(command.TargetResourceId);
                if (vmAfterTimeout != null && vmAfterTimeout.ActiveCommandId == command.CommandId)
                {
                    vmAfterTimeout.ActiveCommandId = null;
                    vmAfterTimeout.ActiveCommandType = null;
                    vmAfterTimeout.ActiveCommandIssuedAt = null;
                    vmAfterTimeout.UpdatedAt = DateTime.UtcNow;
                    await dataStore.SaveVmAsync(vmAfterTimeout);
                    _logger.LogDebug(
                        "Cleared ActiveCommandId on VM {VmId} after timeout",
                        command.TargetResourceId);
                }
            }

            // Remove from tracking
            dataStore.PendingCommandAcks.TryRemove(command.CommandId, out _);
        }

        if (timedOutCommands.Any())
        {
            _logger.LogWarning(
                "Handled {Count} timed-out commands",
                timedOutCommands.Count);
        }
    }

    private Task CleanupStaleCommandAcks(DataStore dataStore)
    {
        var staleCommands = dataStore.PendingCommandAcks.Values
        .Where(cmd =>
            !string.IsNullOrEmpty(cmd.TargetResourceId) &&
            !dataStore.GetActiveVMs().Select(v => v.Id).Contains(cmd.TargetResourceId))
        .Select(cmd => cmd.CommandId)
        .ToList();

        foreach (var commandId in staleCommands)
        {
            dataStore.PendingCommandAcks.TryRemove(commandId, out _);
        }

        if (staleCommands.Any())
        {
            _logger.LogInformation(
                "Cleaned up {Count} stale command acknowledgment entries",
                staleCommands.Count);
        }

        return Task.CompletedTask;
    }

    private Task TrimEventHistoryAsync(Persistence.DataStore dataStore)
    {
        // Event history is already bounded in DataStore, but we can do additional cleanup here
        return Task.CompletedTask;
    }
}
