using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

// ════════════════════════════════════════════════════════════════════════
// VM Lifecycle Manager — single entry point for all VM state transitions
// ════════════════════════════════════════════════════════════════════════
//
// Every code path that changes VM status (command ack, heartbeat, SignalR,
// background timeout, node-offline) MUST go through TransitionAsync().
// This ensures side effects (ingress, port allocation, template fees,
// events, cleanup) fire consistently regardless of the trigger source.
//
// Design principles:
//   1. Validate transition legality (monotonic state progression)
//   2. Persist status first (atomic), then run side effects (best-effort)
//   3. Side effects keyed by (from, to) transition pair
//   4. PrivateIp polling before IP-dependent operations
//   5. Individual error handling per side effect (never abort entire chain)
// ════════════════════════════════════════════════════════════════════════

public interface IVmLifecycleManager
{
    /// <summary>
    /// Transition a VM to a new status with all associated side effects.
    /// This is the ONLY method that should change vm.Status.
    /// </summary>
    Task<bool> TransitionAsync(string vmId, VmStatus newStatus, TransitionContext context);
}

/// <summary>
/// Context describing what triggered the state transition.
/// Used for logging, debugging, and conditional side-effect behavior.
/// </summary>
public class TransitionContext
{
    public TransitionTrigger Trigger { get; init; }
    public string? Source { get; init; }
    public string? CommandId { get; init; }
    public string? NodeId { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Optional payload for trigger-specific data (e.g., error details)
    /// </summary>
    public Dictionary<string, object>? Payload { get; init; }

    public static TransitionContext CommandAck(string commandId, string nodeId, DateTime? completedAt = null) => new()
    {
        Trigger = TransitionTrigger.CommandAck,
        Source = "NodeService.ProcessCommandAcknowledgmentAsync",
        CommandId = commandId,
        NodeId = nodeId,
        CompletedAt = completedAt ?? DateTime.UtcNow
    };

    public static TransitionContext Heartbeat(string nodeId) => new()
    {
        Trigger = TransitionTrigger.Heartbeat,
        Source = "NodeService.SyncVmStateFromHeartbeatAsync",
        NodeId = nodeId
    };

    public static TransitionContext Manual(string? message = null) => new()
    {
        Trigger = TransitionTrigger.Manual,
        Source = "VmService.UpdateVmStatusAsync",
        StatusMessage = message
    };

    public static TransitionContext Timeout(string commandType, string? message = null) => new()
    {
        Trigger = TransitionTrigger.Timeout,
        Source = "BackgroundServices.StaleCommandCleanupService",
        StatusMessage = message ?? $"{commandType} timed out"
    };

    public static TransitionContext NodeOffline(string nodeId) => new()
    {
        Trigger = TransitionTrigger.NodeOffline,
        Source = "NodeService.MarkNodeVmsAsErrorAsync",
        NodeId = nodeId,
        StatusMessage = "Node went offline"
    };

    public static TransitionContext CommandFailed(string commandId, string nodeId, string? error = null) => new()
    {
        Trigger = TransitionTrigger.CommandFailed,
        Source = "NodeService.ProcessCommandAcknowledgmentAsync",
        CommandId = commandId,
        NodeId = nodeId,
        StatusMessage = error
    };
}

public enum TransitionTrigger
{
    CommandAck,
    Heartbeat,
    Manual,
    Timeout,
    NodeOffline,
    CommandFailed
}

public class VmLifecycleManager : IVmLifecycleManager
{
    private readonly DataStore _dataStore;
    private readonly ICentralIngressService _ingressService;
    private readonly ITemplateService _templateService;
    private readonly IEventService _eventService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VmLifecycleManager> _logger;

    // Valid state transitions. Key = current status, Value = set of allowed next statuses.
    private static readonly Dictionary<VmStatus, HashSet<VmStatus>> ValidTransitions = new()
    {
        [VmStatus.Pending]       = [VmStatus.Scheduling, VmStatus.Provisioning, VmStatus.Error, VmStatus.Deleting],
        [VmStatus.Scheduling]    = [VmStatus.Provisioning, VmStatus.Pending, VmStatus.Error, VmStatus.Deleting],
        [VmStatus.Provisioning]  = [VmStatus.Running, VmStatus.Error, VmStatus.Deleting],
        [VmStatus.Running]       = [VmStatus.Stopping, VmStatus.Error, VmStatus.Deleting],
        [VmStatus.Stopping]      = [VmStatus.Stopped, VmStatus.Running, VmStatus.Error, VmStatus.Deleting],
        [VmStatus.Stopped]       = [VmStatus.Provisioning, VmStatus.Running, VmStatus.Deleting, VmStatus.Error],
        [VmStatus.Deleting]      = [VmStatus.Deleted, VmStatus.Error],
        [VmStatus.Migrating]     = [VmStatus.Running, VmStatus.Error, VmStatus.Deleting],
        [VmStatus.Error]         = [VmStatus.Provisioning, VmStatus.Running, VmStatus.Deleting, VmStatus.Stopped, VmStatus.Error],
        [VmStatus.Deleted]       = [] // Terminal state
    };

    public VmLifecycleManager(
        DataStore dataStore,
        ICentralIngressService ingressService,
        ITemplateService templateService,
        IEventService eventService,
        IServiceProvider serviceProvider,
        ILogger<VmLifecycleManager> logger)
    {
        _dataStore = dataStore;
        _ingressService = ingressService;
        _templateService = templateService;
        _eventService = eventService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<bool> TransitionAsync(string vmId, VmStatus newStatus, TransitionContext context)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning(
                "Cannot transition VM {VmId} to {NewStatus} — VM not found (trigger: {Trigger})",
                vmId, newStatus, context.Trigger);
            return false;
        }

        var oldStatus = vm.Status;

        // No-op if already in target status
        if (oldStatus == newStatus)
        {
            _logger.LogDebug("VM {VmId} already in status {Status}, skipping transition", vmId, newStatus);
            return true;
        }

        // Validate transition legality
        if (!IsValidTransition(oldStatus, newStatus))
        {
            _logger.LogWarning(
                "Invalid VM transition {VmId}: {OldStatus} → {NewStatus} (trigger: {Trigger}, source: {Source}). Ignoring.",
                vmId, oldStatus, newStatus, context.Trigger, context.Source);
            return false;
        }

        _logger.LogInformation(
            "VM {VmId} transitioning: {OldStatus} → {NewStatus} (trigger: {Trigger})",
            vmId, oldStatus, newStatus, context.Trigger);

        // ════════════════════════════════════════════════════════════════
        // Step 1: Update status and persist (atomic)
        // ════════════════════════════════════════════════════════════════

        vm.Status = newStatus;
        vm.StatusMessage = context.StatusMessage;
        vm.UpdatedAt = DateTime.UtcNow;

        // Update power state and timestamps based on target status
        switch (newStatus)
        {
            case VmStatus.Running:
                vm.PowerState = VmPowerState.Running;
                vm.StartedAt ??= context.CompletedAt ?? DateTime.UtcNow;
                break;
            case VmStatus.Stopped:
                vm.PowerState = VmPowerState.Off;
                vm.StoppedAt = context.CompletedAt ?? DateTime.UtcNow;
                break;
            case VmStatus.Deleted:
                vm.PowerState = VmPowerState.Off;
                vm.StoppedAt ??= DateTime.UtcNow;
                break;
        }

        await _dataStore.SaveVmAsync(vm);

        // ════════════════════════════════════════════════════════════════
        // Step 2: Execute side effects (best-effort, individually guarded)
        // ════════════════════════════════════════════════════════════════

        await ExecuteSideEffectsAsync(vm, oldStatus, newStatus, context);

        // ════════════════════════════════════════════════════════════════
        // Step 3: Emit lifecycle event
        // ════════════════════════════════════════════════════════════════

        await EmitTransitionEventAsync(vm, oldStatus, newStatus, context);

        _logger.LogInformation(
            "VM {VmId} transition complete: {OldStatus} → {NewStatus}",
            vmId, oldStatus, newStatus);

        return true;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Transition Validation
    // ════════════════════════════════════════════════════════════════════════

    private static bool IsValidTransition(VmStatus from, VmStatus to)
    {
        return ValidTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Side Effect Dispatch (keyed by transition pair)
    // ════════════════════════════════════════════════════════════════════════

    private async Task ExecuteSideEffectsAsync(
        VirtualMachine vm, VmStatus from, VmStatus to, TransitionContext context)
    {
        switch (to)
        {
            // ── Entering Running ──────────────────────────────────────
            case VmStatus.Running when from is VmStatus.Provisioning or VmStatus.Stopped or VmStatus.Error or VmStatus.Stopping:
                await OnVmBecameRunningAsync(vm, context);
                break;

            // ── Entering Stopped ──────────────────────────────────────
            case VmStatus.Stopped:
                await OnVmStoppedAsync(vm);
                break;

            // ── Entering Deleting (leaving Running) ──────────────────
            case VmStatus.Deleting when from == VmStatus.Running:
                await OnVmLeavingRunningAsync(vm);
                break;

            // ── Entering Stopping (leaving Running) ──────────────────
            case VmStatus.Stopping:
                // Don't cleanup ingress yet — wait for Stopped confirmation
                break;

            // ── Entering Error ────────────────────────────────────────
            case VmStatus.Error when from == VmStatus.Running:
                await OnVmLeavingRunningAsync(vm);
                break;

            // ── Entering Deleted (terminal) ───────────────────────────
            case VmStatus.Deleted:
                await OnVmDeletedAsync(vm);
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Side Effect Handlers
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Side effects when a VM transitions to Running:
    ///   1. Wait for PrivateIp (polling, 30s timeout)
    ///   2. Register with ingress (Caddy reverse proxy)
    ///   3. Auto-allocate template ports (DirectAccess)
    ///   4. Settle template fee (paid templates)
    /// </summary>
    private async Task OnVmBecameRunningAsync(VirtualMachine vm, TransitionContext context)
    {
        // Reset all service readiness to Pending so node agent re-checks via qemu-guest-agent
        if (vm.Services.Count > 0)
        {
            foreach (var service in vm.Services)
            {
                service.Status = ServiceReadiness.Pending;
                service.StatusMessage = null;
                service.ReadyAt = null;
                service.LastCheckAt = null;
            }
            _logger.LogDebug("VM {VmId} service readiness reset to Pending ({Count} services)",
                vm.Id, vm.Services.Count);
        }

        // Wait for PrivateIp to be available (heartbeat may not have delivered it yet)
        var ipReady = await WaitForPrivateIpAsync(vm.Id, TimeSpan.FromSeconds(30));
        if (!ipReady)
        {
            _logger.LogWarning(
                "VM {VmId} is Running but PrivateIp not assigned after 30s. " +
                "Ingress and port allocation deferred until next heartbeat confirms IP.",
                vm.Id);
            // Don't fail the transition — VM is running, side effects will be retried
            // when the heartbeat delivers the IP and triggers Running → Running (no-op transition).
            // A reconciliation background service can also catch these.
            return;
        }

        // Re-read VM to get latest state (IP may have been set during polling)
        var freshVm = await _dataStore.GetVmAsync(vm.Id);
        if (freshVm == null) return;

        // 1. Ingress registration
        await SafeExecuteAsync(
            () => _ingressService.OnVmStartedAsync(freshVm.Id),
            "Ingress registration", freshVm.Id);

        // 2. Auto-allocate ports from template
        if (!string.IsNullOrEmpty(freshVm.TemplateId))
        {
            await SafeExecuteAsync(
                () => AutoAllocateTemplatePortsAsync(freshVm),
                "Template port auto-allocation", freshVm.Id);

            // 3. Settle template fee
            await SafeExecuteAsync(
                () => SettleTemplateFeeAsync(freshVm),
                "Template fee settlement", freshVm.Id);
        }
    }

    /// <summary>
    /// Side effects when a VM leaves Running state (→ Stopping, Deleting, Error).
    /// Cleanup ingress registration.
    /// </summary>
    private async Task OnVmLeavingRunningAsync(VirtualMachine vm)
    {
        await SafeExecuteAsync(
            () => _ingressService.OnVmStoppedAsync(vm.Id),
            "Ingress cleanup", vm.Id);
    }

    /// <summary>
    /// Side effects when a VM transitions to Stopped.
    /// </summary>
    private async Task OnVmStoppedAsync(VirtualMachine vm)
    {
        await SafeExecuteAsync(
            () => _ingressService.OnVmStoppedAsync(vm.Id),
            "Ingress cleanup", vm.Id);
    }

    /// <summary>
    /// Side effects when a VM transitions to Deleted (terminal state).
    /// Frees node resources, user quotas, ingress, direct access ports.
    /// </summary>
    private async Task OnVmDeletedAsync(VirtualMachine vm)
    {
        // Ingress cleanup
        await SafeExecuteAsync(
            () => _ingressService.OnVmDeletedAsync(vm.Id),
            "Ingress deletion", vm.Id);

        // DirectAccess port cleanup
        var directAccessService = _serviceProvider.GetService<DirectAccessService>();
        if (directAccessService != null)
        {
            await SafeExecuteAsync(
                () => directAccessService.CleanupVmDirectAccessAsync(vm.Id),
                "DirectAccess port cleanup", vm.Id);
        }

        // Free reserved resources from node
        await FreeNodeResourcesAsync(vm);

        // Update user quotas
        await FreeUserQuotasAsync(vm);

        // Track successful completion in node reputation
        await IncrementNodeCompletionsAsync(vm);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Template Port Auto-Allocation
    // ════════════════════════════════════════════════════════════════════════

    private async Task AutoAllocateTemplatePortsAsync(VirtualMachine vm)
    {
        if (string.IsNullOrEmpty(vm.TemplateId))
            return;

        var template = await _templateService.GetTemplateByIdAsync(vm.TemplateId);
        if (template?.ExposedPorts == null || !template.ExposedPorts.Any())
        {
            _logger.LogDebug("VM {VmId} template has no exposed ports", vm.Id);
            return;
        }

        // Filter to only public ports that aren't already allocated
        // Skip http/ws protocol ports — those are handled by CentralIngress subdomain routing
        // (Caddy reverse proxy handles both HTTP and WebSocket upgrades on the same port)
        var ingressProtocols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https", "ws", "wss" };
        var portsToAllocate = template.ExposedPorts
            .Where(p => p.IsPublic)
            .Where(p => !ingressProtocols.Contains(p.Protocol ?? ""))
            .Where(p => vm.DirectAccess?.PortMappings?.Any(m => m.VmPort == p.Port) != true)
            .ToList();

        if (!portsToAllocate.Any())
        {
            _logger.LogDebug("All template ports already allocated for VM {VmId}", vm.Id);
            return;
        }

        _logger.LogInformation(
            "Auto-allocating {Count} ports for VM {VmId} from template {TemplateName}",
            portsToAllocate.Count, vm.Id, template.Name);

        var directAccessService = _serviceProvider.GetRequiredService<DirectAccessService>();

        foreach (var exposedPort in portsToAllocate)
        {
            var protocol = exposedPort.Protocol.ToLower() switch
            {
                "tcp" => PortProtocol.TCP,
                "udp" => PortProtocol.UDP,
                "both" or "tcp_and_udp" => PortProtocol.Both,
                _ => PortProtocol.TCP
            };

            try
            {
                var result = await directAccessService.AllocatePortAsync(
                    vm.Id,
                    exposedPort.Port,
                    protocol,
                    exposedPort.Description ?? exposedPort.Port.ToString());

                if (result.Success)
                {
                    _logger.LogInformation(
                        "Auto-allocated port {VmPort} ({Protocol}) -> {PublicPort} for VM {VmId}",
                        exposedPort.Port, protocol, result.PublicPort, vm.Id);
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to auto-allocate port {VmPort} for VM {VmId}: {Error}",
                        exposedPort.Port, vm.Id, result.Error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Exception during auto-allocation of port {Port} for VM {VmId}",
                    exposedPort.Port, vm.Id);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Template Fee Settlement
    // ════════════════════════════════════════════════════════════════════════

    private async Task SettleTemplateFeeAsync(VirtualMachine vm)
    {
        if (string.IsNullOrEmpty(vm.TemplateId) || string.IsNullOrEmpty(vm.OwnerId))
            return;

        var template = await _templateService.GetTemplateByIdAsync(vm.TemplateId);
        if (template == null)
            return;

        if (template.PricingModel != TemplatePricingModel.PerDeploy || template.TemplatePrice <= 0)
            return;

        if (string.IsNullOrEmpty(template.AuthorRevenueWallet))
        {
            _logger.LogWarning(
                "Paid template {TemplateId} has no revenue wallet, skipping fee settlement for VM {VmId}",
                template.Id, vm.Id);
            return;
        }

        // Don't charge the author for deploying their own templates
        if (vm.OwnerId == template.AuthorId)
        {
            _logger.LogInformation(
                "Skipping template fee for VM {VmId} — author is deploying their own template",
                vm.Id);
            return;
        }

        // Check if we already settled this fee (prevent double-charge on VM restart)
        var feeLabel = $"template_fee_settled:{template.Id}";
        if (vm.Labels.ContainsKey(feeLabel))
        {
            _logger.LogDebug("Template fee already settled for VM {VmId}", vm.Id);
            return;
        }

        try
        {
            var settlementService = _serviceProvider.GetService<Services.Settlement.ISettlementService>();
            if (settlementService == null)
            {
                _logger.LogWarning("SettlementService not available, skipping template fee for VM {VmId}", vm.Id);
                return;
            }

            // Record template fee as usage with the template author's wallet as the "node" (recipient).
            // 85% goes to author, 15% platform fee — same split as VM billing.
            var success = await settlementService.RecordUsageAsync(
                userId: vm.OwnerId,
                vmId: vm.Id,
                nodeId: template.AuthorRevenueWallet,
                amount: template.TemplatePrice,
                periodStart: DateTime.UtcNow,
                periodEnd: DateTime.UtcNow,
                attestationVerified: true);

            if (success)
            {
                // Mark as settled to prevent double-charge
                vm.Labels[feeLabel] = DateTime.UtcNow.ToString("O");
                await _dataStore.SaveVmAsync(vm);

                _logger.LogInformation(
                    "Template fee settled: {Amount} USDC from {UserId} to {AuthorWallet} for template {TemplateName} (VM {VmId})",
                    template.TemplatePrice, vm.OwnerId, template.AuthorRevenueWallet, template.Name, vm.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to record template fee for VM {VmId}, template {TemplateId}",
                    vm.Id, template.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error settling template fee for VM {VmId}, template {TemplateId}",
                vm.Id, template.Id);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Resource Cleanup (used by OnVmDeletedAsync)
    // ════════════════════════════════════════════════════════════════════════

    private async Task FreeNodeResourcesAsync(VirtualMachine vm)
    {
        if (string.IsNullOrEmpty(vm.NodeId)) return;

        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        if (node == null)
        {
            _logger.LogWarning("Node {NodeId} not found when freeing resources for VM {VmId}",
                vm.NodeId, vm.Id);
            return;
        }

        node.ReservedResources.ComputePoints = Math.Max(0,
            node.ReservedResources.ComputePoints - vm.Spec.ComputePointCost);
        node.ReservedResources.MemoryBytes = Math.Max(0,
            node.ReservedResources.MemoryBytes - vm.Spec.MemoryBytes);
        node.ReservedResources.StorageBytes = Math.Max(0,
            node.ReservedResources.StorageBytes - vm.Spec.DiskBytes);

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Released resources for VM {VmId} on node {NodeId}: " +
            "{Points} points, {MemMb}MB, {StorGb}GB",
            vm.Id, node.Id,
            vm.Spec.ComputePointCost,
            vm.Spec.MemoryBytes / (1024 * 1024),
            vm.Spec.DiskBytes / (1024 * 1024 * 1024));
    }

    private async Task FreeUserQuotasAsync(VirtualMachine vm)
    {
        if (string.IsNullOrEmpty(vm.OwnerId)) return;

        if (_dataStore.Users.TryGetValue(vm.OwnerId, out var user))
        {
            user.Quotas.CurrentVms = Math.Max(0, user.Quotas.CurrentVms - 1);
            user.Quotas.CurrentVirtualCpuCores = Math.Max(0,
                user.Quotas.CurrentVirtualCpuCores - vm.Spec.VirtualCpuCores);
            user.Quotas.CurrentMemoryBytes = Math.Max(0,
                user.Quotas.CurrentMemoryBytes - vm.Spec.MemoryBytes);
            user.Quotas.CurrentStorageBytes = Math.Max(0,
                user.Quotas.CurrentStorageBytes - vm.Spec.DiskBytes);

            await _dataStore.SaveUserAsync(user);

            _logger.LogInformation(
                "Updated quotas for user {UserId}: VMs={VMs}/{MaxVMs}",
                user.Id, user.Quotas.CurrentVms, user.Quotas.MaxVms);
        }
    }

    private async Task IncrementNodeCompletionsAsync(VirtualMachine vm)
    {
        if (string.IsNullOrEmpty(vm.NodeId)) return;

        var reputationService = _serviceProvider.GetService<INodeReputationService>();
        if (reputationService == null) return;

        try
        {
            await reputationService.IncrementSuccessfulCompletionsAsync(vm.NodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to increment completions for node {NodeId}", vm.NodeId);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // PrivateIp Polling
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Wait for a VM's PrivateIp to be set (arrives via heartbeat from the node).
    /// Polls the datastore every 2 seconds up to the timeout.
    /// </summary>
    private async Task<bool> WaitForPrivateIpAsync(string vmId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var pollInterval = TimeSpan.FromSeconds(2);

        // Check immediately (might already be set)
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm != null && !string.IsNullOrEmpty(vm.NetworkConfig?.PrivateIp))
            return true;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(pollInterval);

            vm = await _dataStore.GetVmAsync(vmId);
            if (vm == null)
            {
                _logger.LogWarning("VM {VmId} disappeared while waiting for PrivateIp", vmId);
                return false;
            }

            if (!string.IsNullOrEmpty(vm.NetworkConfig?.PrivateIp))
            {
                _logger.LogDebug("VM {VmId} PrivateIp available: {Ip}", vmId, vm.NetworkConfig.PrivateIp);
                return true;
            }
        }

        return false;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Event Emission
    // ════════════════════════════════════════════════════════════════════════

    private async Task EmitTransitionEventAsync(
        VirtualMachine vm, VmStatus from, VmStatus to, TransitionContext context)
    {
        var eventType = to switch
        {
            VmStatus.Running when from is VmStatus.Provisioning or VmStatus.Stopped
                => EventType.VmStarted,
            VmStatus.Stopped => EventType.VmStopped,
            VmStatus.Deleted => EventType.VmDeleted,
            VmStatus.Error => EventType.VmError,
            VmStatus.Provisioning => EventType.VmProvisioning,
            VmStatus.Deleting => EventType.VmDeleting,
            _ => (EventType?)null
        };

        if (eventType == null) return;

        var payload = new Dictionary<string, object>
        {
            ["fromStatus"] = from.ToString(),
            ["toStatus"] = to.ToString(),
            ["trigger"] = context.Trigger.ToString()
        };

        if (context.CommandId != null)
            payload["commandId"] = context.CommandId;
        if (context.StatusMessage != null)
            payload["message"] = context.StatusMessage;

        // Add resource info for deletion events
        if (to == VmStatus.Deleted)
        {
            payload["FreedCpu"] = vm.Spec.VirtualCpuCores;
            payload["FreedMemoryBytes"] = vm.Spec.MemoryBytes;
            payload["FreedStorageBytes"] = vm.Spec.DiskBytes;
        }

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = eventType.Value,
            ResourceType = "vm",
            ResourceId = vm.Id,
            NodeId = context.NodeId ?? vm.NodeId,
            UserId = vm.OwnerId,
            Payload = payload
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Execute a side effect with individual error handling.
    /// Never throws — logs errors and continues.
    /// </summary>
    private async Task SafeExecuteAsync(Func<Task> action, string description, string vmId)
    {
        try
        {
            await action();
            _logger.LogDebug("{Description} succeeded for VM {VmId}", description, vmId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Description} failed for VM {VmId}", description, vmId);
        }
    }
}
