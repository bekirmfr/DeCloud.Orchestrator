using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services.VmScheduling;

namespace Orchestrator.Services.Reconciliation.Handlers;

/// <summary>
/// Handles vm.schedule obligations: find the best node for a pending VM,
/// reserve resources, and assign the VM to that node.
///
/// Idempotency: If the VM already has a NodeId assigned, returns Completed.
/// On failure (no suitable node): retries with backoff until nodes become available.
///
/// On success, spawns a vm.provision child obligation.
/// </summary>
public class VmScheduleHandler : IObligationHandler
{
    private readonly DataStore _dataStore;
    private readonly IVmSchedulingService _schedulingService;
    private readonly ISchedulingConfigService _configService;
    private readonly ILogger<VmScheduleHandler> _logger;

    public IReadOnlyList<string> SupportedTypes => [ObligationTypes.VmSchedule];

    public VmScheduleHandler(
        DataStore dataStore,
        IVmSchedulingService schedulingService,
        ISchedulingConfigService configService,
        ILogger<VmScheduleHandler> logger)
    {
        _dataStore = dataStore;
        _schedulingService = schedulingService;
        _configService = configService;
        _logger = logger;
    }

    public async Task<ObligationResult> ExecuteAsync(Obligation obligation, CancellationToken ct)
    {
        var vmId = obligation.ResourceId;
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
            return ObligationResult.Fail($"VM {vmId} not found");

        // Idempotency: already assigned to a node
        if (!string.IsNullOrEmpty(vm.NodeId) && vm.Status != VmStatus.Pending)
        {
            _logger.LogDebug("VM {VmId} already assigned to node {NodeId}", vmId, vm.NodeId);
            return ObligationResult.Completed($"Already assigned to {vm.NodeId}");
        }

        // Terminal states — nothing to do
        if (vm.Status is VmStatus.Deleted or VmStatus.Deleting)
            return ObligationResult.Fail($"VM {vmId} is {vm.Status}");

        // ════════════════════════════════════════════════════════════════
        // Step 1: Calculate compute point cost
        // ════════════════════════════════════════════════════════════════

        var config = await _configService.GetConfigAsync(ct);
        var tierConfig = config.Tiers[vm.Spec.QualityTier];
        var pointCost = vm.Spec.VmType == VmType.Relay
            ? vm.Spec.ComputePointCost
            : vm.Spec.VirtualCpuCores *
              (int)tierConfig.GetPointsPerVCpu(config.BaselineBenchmark, config.BaselineOvercommitRatio);

        vm.Spec.ComputePointCost = pointCost;

        // ════════════════════════════════════════════════════════════════
        // Step 2: Select best node
        // ════════════════════════════════════════════════════════════════

        Node? selectedNode;

        // Check if a specific target node was requested
        var targetNodeId = obligation.Data.GetValueOrDefault("targetNodeId");
        if (!string.IsNullOrEmpty(targetNodeId))
        {
            selectedNode = await _dataStore.GetNodeAsync(targetNodeId);
            if (selectedNode == null)
                return ObligationResult.Fail($"Target node {targetNodeId} not found");
        }
        else
        {
            selectedNode = await _schedulingService.SelectBestNodeForVmAsync(
                vm.Spec,
                vm.Spec.QualityTier,
                vm.Spec.Region,
                vm.Spec.Zone,
                ct: ct);
        }

        if (selectedNode == null)
        {
            _logger.LogDebug(
                "No suitable node for VM {VmId} (tier={Tier}, points={Points}, region={Region})",
                vmId, vm.Spec.QualityTier, pointCost, vm.Spec.Region ?? "any");
            return ObligationResult.Retry("No suitable node available — will retry");
        }

        // ════════════════════════════════════════════════════════════════
        // Step 3: Reserve resources on node
        // ════════════════════════════════════════════════════════════════

        selectedNode.ReservedResources.ComputePoints += pointCost;
        selectedNode.ReservedResources.MemoryBytes += vm.Spec.MemoryBytes;
        selectedNode.ReservedResources.StorageBytes += vm.Spec.DiskBytes;
        await _dataStore.SaveNodeAsync(selectedNode);

        // ════════════════════════════════════════════════════════════════
        // Step 4: Assign VM to node
        // ════════════════════════════════════════════════════════════════

        vm.NodeId = selectedNode.Id;
        vm.Status = VmStatus.Scheduling;
        vm.StatusMessage = $"Assigned to node {selectedNode.Id}";
        vm.UpdatedAt = DateTime.UtcNow;
        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "VM {VmId} scheduled on node {NodeId} ({Points} points, {Tier})",
            vmId, selectedNode.Id, pointCost, vm.Spec.QualityTier);

        // ════════════════════════════════════════════════════════════════
        // Step 5: Spawn vm.provision child obligation
        // ════════════════════════════════════════════════════════════════

        var provisionObligation = new Obligation
        {
            Id = $"{ObligationTypes.VmProvision}:{vmId}:{Guid.NewGuid().ToString()[..8]}",
            Type = ObligationTypes.VmProvision,
            ResourceType = "vm",
            ResourceId = vmId,
            Priority = obligation.Priority,
            Deadline = obligation.Deadline,
            Data = new Dictionary<string, string>
            {
                ["nodeId"] = selectedNode.Id,
                ["pointCost"] = pointCost.ToString()
            }
        };

        return ObligationResult.CompletedWithChildren(
            [provisionObligation],
            $"Scheduled on node {selectedNode.Id}");
    }
}
