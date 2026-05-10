using Orchestrator.Persistence;
using Orchestrator.Models;
using System.Text.Json;
using Orchestrator.Interfaces.VmScheduling;
using Orchestrator.Interfaces;

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
        var offlineNodeIds = (await dataStore.GetAllNodesAsync())
            .Where(n => n.Status == NodeStatus.Offline)
            .Select(n => n.Id)
            .ToHashSet();

        var candidates = allVms
            .Where(v =>
                v.Status == VmStatus.Error &&
                v.Spec.ReplicationFactor > 0 &&
                !string.IsNullOrEmpty(v.NodeId) &&
                offlineNodeIds.Contains(v.NodeId) &&
                string.IsNullOrEmpty(v.ActiveCommandId))
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
                v.VmType == VmType.General &&
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
        var sourceNodeId = fresh.NodeId;
        var commandId = Guid.NewGuid().ToString();

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
        var command = new NodeCommand(
            CommandId: commandId,
            Type: NodeCommandType.CreateVm,
            Payload: JsonSerializer.Serialize(new
            {
                VmId = fresh.Id,
                Name = fresh.Name,
                OwnerId = fresh.OwnerId ?? "",
                VmType = (int)(fresh.Spec.VmType ?? VmType.General),
                VirtualCpuCores = fresh.Spec.VirtualCpuCores,
                QualityTier = (int)fresh.Spec.QualityTier,
                ComputePointCost = fresh.Spec.ComputePointCost,
                MemoryBytes = fresh.Spec.MemoryBytes,
                DiskBytes = fresh.Spec.DiskBytes,
                ImageId = fresh.Spec.ImageId,
                BaseImageUrl = (string?)null,
                SshPublicKey = sshPublicKey,
                GpuMode = (int)fresh.Spec.GpuMode,
                GpuPciAddress = (string?)null,
                ContainerImage = fresh.Spec.ContainerImage,
                Network = new
                {
                    MacAddress = "",
                    IpAddress = (string?)null,
                    Gateway = "",
                    VxlanVni = 0,
                    AllowedPorts = new List<int>()
                },
                Password = (string?)null,
                IsMigration = true,
                ManifestRootCid = manifest.ConfirmedRootCid,
                ConfirmedVersion = manifest.ConfirmedVersion,
                SourceNodeId = sourceNodeId,
                ChunkMap = manifest.ChunkMap,
                TargetNodeId = targetNode.Id,
                ReplicationFactor = fresh.Spec.ReplicationFactor
            }),
            RequiresAck: true,
            TargetResourceId: fresh.Id
        );

        await commandService.DeliverCommandAsync(targetNode.Id, command);

        _logger.LogInformation(
            "Migration command {CommandId} delivered to node {NodeId} for VM {VmId}",
            commandId, targetNode.Id, fresh.Id);
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
