using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Text.Json;

namespace Orchestrator.Services.Reconciliation.Handlers;

/// <summary>
/// Handles vm.delete obligations: send a DeleteVm command to the node
/// and wait for acknowledgment. On ack, the VmLifecycleManager handles
/// resource cleanup, quota adjustment, and terminal state transition.
///
/// Idempotency: If VM is already Deleted, returns Completed.
///              If no node assigned, transitions to Deleted immediately.
/// </summary>
public class VmDeleteHandler : IObligationHandler
{
    private readonly DataStore _dataStore;
    private readonly INodeCommandService _commandService;
    private readonly IVmLifecycleManager _lifecycleManager;
    private readonly ILogger<VmDeleteHandler> _logger;

    public IReadOnlyList<string> SupportedTypes => [ObligationTypes.VmDelete];

    public VmDeleteHandler(
        DataStore dataStore,
        INodeCommandService commandService,
        IVmLifecycleManager lifecycleManager,
        ILogger<VmDeleteHandler> logger)
    {
        _dataStore = dataStore;
        _commandService = commandService;
        _lifecycleManager = lifecycleManager;
        _logger = logger;
    }

    public async Task<ObligationResult> ExecuteAsync(Obligation obligation, CancellationToken ct)
    {
        var vmId = obligation.ResourceId;
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
            return ObligationResult.Completed("VM not found — already cleaned up");

        // Idempotency: already deleted
        if (vm.Status == VmStatus.Deleted)
            return ObligationResult.Completed("VM already deleted");

        // ════════════════════════════════════════════════════════════════
        // No node assigned — delete immediately
        // ════════════════════════════════════════════════════════════════

        if (string.IsNullOrEmpty(vm.NodeId))
        {
            await _lifecycleManager.TransitionAsync(
                vmId,
                VmStatus.Deleted,
                TransitionContext.Manual("No node — immediate deletion via reconciliation"));

            return ObligationResult.Completed("No node assigned — deleted immediately");
        }

        // ════════════════════════════════════════════════════════════════
        // Check if a delete command is already in flight
        // ════════════════════════════════════════════════════════════════

        if (!string.IsNullOrEmpty(vm.ActiveCommandId) &&
            vm.ActiveCommandType == NodeCommandType.DeleteVm)
        {
            var commandAge = DateTime.UtcNow - (vm.ActiveCommandIssuedAt ?? DateTime.UtcNow);
            if (commandAge < TimeSpan.FromMinutes(5))
            {
                return ObligationResult.WaitForSignal(
                    SignalKeys.CommandAck(vm.ActiveCommandId),
                    $"Waiting for DeleteVm ack (command {vm.ActiveCommandId}, age: {commandAge.TotalSeconds:F0}s)");
            }

            // Timed out — clear and re-send
            _logger.LogWarning(
                "DeleteVm command {CommandId} for VM {VmId} timed out after {Age}s — retrying",
                vm.ActiveCommandId, vmId, commandAge.TotalSeconds);

            vm.ActiveCommandId = null;
            vm.ActiveCommandType = null;
            vm.ActiveCommandIssuedAt = null;
        }

        // ════════════════════════════════════════════════════════════════
        // Mark as Deleting and send command
        // ════════════════════════════════════════════════════════════════

        if (vm.Status != VmStatus.Deleting)
        {
            vm.Status = VmStatus.Deleting;
            vm.StatusMessage = "Deletion initiated by reconciliation loop";
            vm.UpdatedAt = DateTime.UtcNow;
        }

        var command = new NodeCommand(
            CommandId: Guid.NewGuid().ToString(),
            Type: NodeCommandType.DeleteVm,
            Payload: JsonSerializer.Serialize(new { VmId = vmId }),
            RequiresAck: true,
            TargetResourceId: vmId
        );

        _dataStore.RegisterCommand(command.CommandId, vmId, vm.NodeId, NodeCommandType.DeleteVm);

        vm.ActiveCommandId = command.CommandId;
        vm.ActiveCommandType = NodeCommandType.DeleteVm;
        vm.ActiveCommandIssuedAt = DateTime.UtcNow;
        vm.StatusMessage = $"DeleteVm command {command.CommandId} sent";

        await _commandService.DeliverCommandAsync(vm.NodeId, command, ct);
        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "Sent DeleteVm command {CommandId} for VM {VmId} to node {NodeId}",
            command.CommandId, vmId, vm.NodeId);

        return ObligationResult.WaitForSignal(
            SignalKeys.CommandAck(command.CommandId),
            $"DeleteVm command {command.CommandId} sent — waiting for ack",
            new Dictionary<string, string> { ["commandId"] = command.CommandId });
    }
}
