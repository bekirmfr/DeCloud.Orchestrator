using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Text.Json;

namespace Orchestrator.Services.Reconciliation.Handlers;

/// <summary>
/// Handles vm.provision obligations: send a CreateVm command to the assigned node
/// and wait for the command acknowledgment.
///
/// Preconditions: VM must have a NodeId assigned (by VmScheduleHandler).
/// Idempotency: If VM is already Running, returns Completed.
///              If a CreateVm command is already in flight, waits for its signal.
///
/// On success (command acked), spawns:
///   - vm.register-ingress (if ingress is enabled)
///   - vm.allocate-ports (if template has exposed ports)
/// </summary>
public class VmProvisionHandler : IObligationHandler
{
    private readonly DataStore _dataStore;
    private readonly INodeCommandService _commandService;
    private readonly IVmLifecycleManager _lifecycleManager;
    private readonly ILogger<VmProvisionHandler> _logger;

    public IReadOnlyList<string> SupportedTypes => [ObligationTypes.VmProvision];

    public VmProvisionHandler(
        DataStore dataStore,
        INodeCommandService commandService,
        IVmLifecycleManager lifecycleManager,
        ILogger<VmProvisionHandler> logger)
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
            return ObligationResult.Fail($"VM {vmId} not found");

        // Idempotency: already running
        if (vm.Status == VmStatus.Running)
            return ObligationResult.Completed("VM already running");

        // Terminal states
        if (vm.Status is VmStatus.Deleted or VmStatus.Deleting)
            return ObligationResult.Fail($"VM {vmId} is {vm.Status}");

        // Must have a node assignment
        if (string.IsNullOrEmpty(vm.NodeId))
            return ObligationResult.Retry("VM has no node assignment — waiting for scheduling");

        var nodeId = vm.NodeId;
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return ObligationResult.Fail($"Assigned node {nodeId} not found");

        // ════════════════════════════════════════════════════════════════
        // Check if a command is already in flight
        // ════════════════════════════════════════════════════════════════

        if (!string.IsNullOrEmpty(vm.ActiveCommandId) &&
            vm.ActiveCommandType == NodeCommandType.CreateVm)
        {
            // Command already sent — check if it's been too long
            var commandAge = DateTime.UtcNow - (vm.ActiveCommandIssuedAt ?? DateTime.UtcNow);
            if (commandAge < TimeSpan.FromMinutes(5))
            {
                // Still within timeout — wait
                return ObligationResult.WaitForSignal(
                    SignalKeys.CommandAck(vm.ActiveCommandId),
                    $"Waiting for CreateVm ack (command {vm.ActiveCommandId}, age: {commandAge.TotalSeconds:F0}s)",
                    new Dictionary<string, string> { ["commandId"] = vm.ActiveCommandId });
            }

            // Command timed out — clear it and retry
            _logger.LogWarning(
                "CreateVm command {CommandId} for VM {VmId} timed out after {Age}s — retrying",
                vm.ActiveCommandId, vmId, commandAge.TotalSeconds);

            vm.ActiveCommandId = null;
            vm.ActiveCommandType = null;
            vm.ActiveCommandIssuedAt = null;
            await _dataStore.SaveVmAsync(vm);
        }

        // ════════════════════════════════════════════════════════════════
        // If VM is already Provisioning from the existing inline path,
        // just monitor it rather than sending a duplicate command.
        // ════════════════════════════════════════════════════════════════

        if (vm.Status == VmStatus.Provisioning && vm.ActiveCommandId != null)
        {
            return ObligationResult.WaitForSignal(
                SignalKeys.CommandAck(vm.ActiveCommandId),
                $"Monitoring existing provisioning command {vm.ActiveCommandId}");
        }

        // ════════════════════════════════════════════════════════════════
        // Send CreateVm command
        // ════════════════════════════════════════════════════════════════

        var command = new NodeCommand(
            CommandId: Guid.NewGuid().ToString(),
            Type: NodeCommandType.CreateVm,
            Payload: JsonSerializer.Serialize(new
            {
                VmId = vm.Id,
                Name = vm.Name,
                VmType = (int)vm.VmType,
                OwnerId = vm.OwnerId,
                OwnerWallet = vm.OwnerWallet,
                VirtualCpuCores = vm.Spec.VirtualCpuCores,
                MemoryBytes = vm.Spec.MemoryBytes,
                DiskBytes = vm.Spec.DiskBytes,
                QualityTier = (int)vm.Spec.QualityTier,
                ComputePointCost = vm.Spec.ComputePointCost,
                SshPublicKey = vm.Spec.SshPublicKey ?? "",
                Network = new
                {
                    MacAddress = "",
                    IpAddress = vm.NetworkConfig?.PrivateIp ?? "",
                    Gateway = "",
                    VxlanVni = 0,
                    AllowedPorts = new List<int>()
                },
                UserData = vm.Spec.UserData,
                Labels = vm.Labels,
                Services = vm.Services.Select(s => new
                {
                    s.Name,
                    s.Port,
                    s.Protocol,
                    CheckType = s.CheckType.ToString(),
                    s.HttpPath,
                    s.ExecCommand,
                    s.TimeoutSeconds
                }).ToList()
            }),
            RequiresAck: true,
            TargetResourceId: vm.Id
        );

        // Register command for ack tracking
        _dataStore.RegisterCommand(command.CommandId, vm.Id, nodeId, NodeCommandType.CreateVm);

        vm.ActiveCommandId = command.CommandId;
        vm.ActiveCommandType = NodeCommandType.CreateVm;
        vm.ActiveCommandIssuedAt = DateTime.UtcNow;
        vm.Status = VmStatus.Provisioning;
        vm.StatusMessage = $"CreateVm command {command.CommandId} sent";
        vm.UpdatedAt = DateTime.UtcNow;

        await _commandService.DeliverCommandAsync(nodeId, command, ct);
        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "Sent CreateVm command {CommandId} for VM {VmId} to node {NodeId}",
            command.CommandId, vmId, nodeId);

        return ObligationResult.WaitForSignal(
            SignalKeys.CommandAck(command.CommandId),
            $"CreateVm command {command.CommandId} sent — waiting for ack",
            new Dictionary<string, string> { ["commandId"] = command.CommandId });
    }
}
