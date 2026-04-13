using Orchestrator.Persistence;
using Orchestrator.Models;
using System.Text.Json;

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
        var commandService = services.GetRequiredService<INodeCommandService>();

        // Query MongoDB directly — the in-memory ActiveVMs dict may not reflect
        // LazysyncStatus updates written by MarkNodeVmsAsErrorAsync, which fetches
        // and saves VMs via MongoDB bypassing the in-memory write path.
        var allVms = await dataStore.GetAllVMsAsync();
        var candidates = allVms
            .Where(v =>
                v.Status == VmStatus.Error &&
                (v.LazysyncStatus == LazysyncStatus.Migrating ||
                 v.LazysyncStatus == LazysyncStatus.Recovering) &&
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
                await MigrateVmAsync(vm, dataStore, blockStoreService, commandService, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Migration failed for VM {VmId}", vm.Id);
            }
        }
    }

    private async Task MigrateVmAsync(
        VirtualMachine vm,
        DataStore dataStore,
        IBlockStoreService blockStoreService,
        INodeCommandService commandService,
        CancellationToken ct)
    {
        // ── Plan ────────────────────────────────────────────────────────────
        var plan = await blockStoreService.PlanMigrationAsync(vm.Id, [], ct);

        if (plan.TargetNodeId == null)
        {
            _logger.LogWarning(
                "VM {VmId}: no migration target ({Status}: {Reason})",
                vm.Id, plan.MigrationStatus, plan.Reason);
            return;
        }

        // ── Re-fetch to avoid stale overwrite + re-check idempotency gate ───
        var fresh = await dataStore.GetVmAsync(vm.Id);
        if (fresh == null ||
            fresh.ActiveCommandId != null ||
            fresh.Status != VmStatus.Error)
        {
            _logger.LogDebug(
                "VM {VmId}: state changed since scan — skipping", vm.Id);
            return;
        }

        _logger.LogInformation(
            "VM {VmId} ({LazysyncStatus}): {SourceNode} → {TargetNode} " +
            "confirmedV={CV} ({PlanStatus})",
            fresh.Id, fresh.LazysyncStatus,
            fresh.NodeId ?? "none", plan.TargetNodeId,
            plan.ConfirmedVersion, plan.MigrationStatus);

        // ── Atomic authority transfer ────────────────────────────────────────
        var sourceNodeId = fresh.NodeId;
        var commandId = Guid.NewGuid().ToString();

        fresh.NodeId = plan.TargetNodeId;
        fresh.TargetNodeId = plan.TargetNodeId;
        fresh.Status = VmStatus.Provisioning;
        fresh.LazysyncStatus = LazysyncStatus.Migrating;
        fresh.StatusMessage = $"Migrating to node {plan.TargetNodeId} " +
                                      $"(confirmedV={plan.ConfirmedVersion}, {plan.MigrationStatus})";
        fresh.ActiveCommandId = commandId;
        fresh.ActiveCommandType = NodeCommandType.CreateVm;
        fresh.ActiveCommandIssuedAt = DateTime.UtcNow;
        fresh.UpdatedAt = DateTime.UtcNow;

        await dataStore.SaveVmAsync(fresh);

        dataStore.RegisterCommand(
            commandId, fresh.Id, plan.TargetNodeId, NodeCommandType.CreateVm);

        // ── SSH key ──────────────────────────────────────────────────────────
        string? sshPublicKey = fresh.Spec.SshPublicKey;
        if (string.IsNullOrEmpty(sshPublicKey) &&
            !string.IsNullOrEmpty(fresh.OwnerId) &&
            dataStore.Users.TryGetValue(fresh.OwnerId, out var owner) &&
            owner.SshKeys.Any())
        {
            sshPublicKey = string.Join("\n", owner.SshKeys.Select(k => k.PublicKey));
        }

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
                BaseImageUrl = (string?)null,   // NodeAgent resolves from ImageId
                SshPublicKey = sshPublicKey,
                GpuMode = (int)fresh.Spec.GpuMode,
                GpuPciAddress = (string?)null,    // re-assigned by target scheduler
                ContainerImage = fresh.Spec.ContainerImage,
                Network = new
                {
                    MacAddress = "",
                    IpAddress = (string?)null,    // new IP on target
                    Gateway = "",
                    VxlanVni = 0,
                    AllowedPorts = new List<int>()
                },
                Password = (string?)null,    // SSH key + wallet decryption instead
                UserData = fresh.Spec.UserData,
                Labels = fresh.Labels,
                ReplicationFactor = fresh.Spec.ReplicationFactor,
                Services = fresh.Services.Select(s => new
                {
                    s.Name,
                    s.Port,
                    s.Protocol,
                    CheckType = s.CheckType.ToString(),
                    s.HttpPath,
                    s.ExecCommand,
                    s.TimeoutSeconds
                }).ToList(),

                // Migration fields — read by NodeAgent disk reconstruction (Phase D step 4)
                IsMigration = true,
                ManifestRootCid = plan.ConfirmedManifestRootCid,
                ConfirmedVersion = plan.ConfirmedVersion,
                SourceNodeId = sourceNodeId,
                ChunkMap = plan.ChunkMap,
                TargetNodeId = plan.TargetNodeId
            }),
            RequiresAck: true,
            TargetResourceId: fresh.Id
        );

        var result = await commandService.DeliverCommandAsync(plan.TargetNodeId, command, ct);

        _logger.LogInformation(
            "VM {VmId}: CreateVm delivered to {TargetNode} " +
            "(delivery={Method}, commandId={CommandId})",
            fresh.Id, plan.TargetNodeId, result.Method, commandId);
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
