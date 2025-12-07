using System.Security.Cryptography;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Services;

public interface INodeService
{
    Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request);
    Task<bool> ValidateNodeTokenAsync(string nodeId, string token);
    Task<NodeHeartbeatResponse> ProcessHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat);
    /// <summary>
    /// Process command acknowledgment from node
    /// </summary>
    Task<bool> ProcessCommandAcknowledgmentAsync(
        string nodeId,
        string commandId,
        CommandAcknowledgment ack);
    Task<Node?> GetNodeAsync(string nodeId);
    Task<List<Node>> GetAllNodesAsync(NodeStatus? statusFilter = null);

    // Enhanced scheduling methods
    Task<List<Node>> GetAvailableNodesForVmAsync(VmSpec spec, QualityTier tier = QualityTier.Standard);
    Task<Node?> SelectBestNodeForVmAsync(VmSpec spec, QualityTier tier = QualityTier.Standard);
    Task<List<ScoredNode>> GetScoredNodesForVmAsync(VmSpec spec, QualityTier tier = QualityTier.Standard);

    Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status);
    Task<bool> RemoveNodeAsync(string nodeId);
    Task CheckNodeHealthAsync();
}

public class NodeService : INodeService
{
    private readonly DataStore _dataStore;
    private readonly IEventService _eventService;
    private readonly ILogger<NodeService> _logger;
    private readonly SchedulingConfiguration _schedulingConfig;

    public NodeService(
        DataStore dataStore,
        IEventService eventService,
        ILogger<NodeService> logger,
        SchedulingConfiguration? schedulingConfig = null)
    {
        _dataStore = dataStore;
        _eventService = eventService;
        _logger = logger;
        _schedulingConfig = schedulingConfig ?? new SchedulingConfiguration();
        _schedulingConfig.Weights.Validate();
    }

    // ============================================================================
    // SMART SCHEDULING - Core Methods
    // ============================================================================

    public async Task<List<Node>> GetAvailableNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard)
    {
        var scoredNodes = await GetScoredNodesForVmAsync(spec, tier);

        return scoredNodes
            .Where(sn => sn.RejectionReason == null)
            .Select(sn => sn.Node)
            .ToList();
    }

    public async Task<Node?> SelectBestNodeForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard)
    {
        var scoredNodes = await GetScoredNodesForVmAsync(spec, tier);

        var best = scoredNodes
            .Where(sn => sn.RejectionReason == null)
            .OrderByDescending(sn => sn.TotalScore)
            .FirstOrDefault();

        if (best != null)
        {
            _logger.LogInformation(
                "Selected node {NodeId} ({NodeName}) for VM - Score: {Score:F2}, " +
                "CPU: {Cpu}%, MEM: {Mem}%, Tier: {Tier}",
                best.Node.Id, best.Node.Name, best.TotalScore,
                best.Availability.ProjectedCpuUtilization(spec),
                best.Availability.ProjectedMemoryUtilization(spec),
                tier);
        }
        else
        {
            _logger.LogWarning(
                "No suitable node found for VM - CPU: {Cpu}, MEM: {Mem}MB, DISK: {Disk}GB, Tier: {Tier}",
                spec.CpuCores, spec.MemoryMb, spec.DiskGb, tier);
        }

        return best?.Node;
    }

    public async Task<List<ScoredNode>> GetScoredNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard)
    {
        var policy = _schedulingConfig.TierPolicies[tier];
        var allNodes = _dataStore.Nodes.Values.ToList();
        _logger.LogInformation("Scoring {TotalNodesCount} nodes", allNodes.Count);
        var scoredNodes = new List<ScoredNode>();

        foreach (var node in allNodes)
        {
            var scored = await ScoreNodeForVmAsync(node, spec, tier, policy);
            _logger.LogInformation("Scoring result:");
            // Add scroring logs
             _logger.LogInformation("Node {NodeId} - Total Score: {TotalScore:F2}, " +
                 "Capacity: {CapacityScore:F2}, Load: {LoadScore:F2}, " +
                 "Reputation: {ReputationScore:F2}, Locality: {LocalityScore:F2}, " +
                 "Rejection: {RejectionReason}",
                 node.Id,
                 scored.TotalScore,
                 scored.ComponentScores.CapacityScore,
                 scored.ComponentScores.LoadScore,
                 scored.ComponentScores.ReputationScore,
                 scored.ComponentScores.LocalityScore,
                 scored.RejectionReason ?? "None");

            scoredNodes.Add(scored);
        }

        return scoredNodes
            .OrderByDescending(sn => sn.TotalScore)
            .ToList();
    }

    // ============================================================================
    // SMART SCHEDULING - Scoring & Filtering
    // ============================================================================

    private async Task<ScoredNode> ScoreNodeForVmAsync(
        Node node,
        VmSpec spec,
        QualityTier tier,
        TierPolicy policy)
    {
        try
        {
            var scored = new ScoredNode { Node = node };

            // Step 1: Hard filters (must pass or node is rejected)
            var rejection = ApplyHardFilters(node, spec, policy);
            if (rejection != null)
            {
                _logger.LogDebug("Node {NodeId} rejected: {Reason}", node.Id, rejection);
                scored.RejectionReason = rejection;
                scored.TotalScore = 0;
                return scored;
            }

            // Step 2: Calculate resource availability with overcommit
            var availability = CalculateResourceAvailability(node, tier, policy);
            scored.Availability = availability;

            // Step 3: Check if VM fits after overcommit calculation
            if (!availability.CanFit(spec))
            {
                scored.RejectionReason = $"Insufficient resources after overcommit " +
                    $"(CPU: {availability.RemainingCpu:F1}/{spec.CpuCores}, " +
                    $"MEM: {availability.RemainingMemory:F0}/{spec.MemoryMb}MB, " +
                    $"DISK: {availability.RemainingStorage:F0}/{spec.DiskGb}GB)";
                scored.TotalScore = 0;
                _logger.LogDebug("Node {NodeId} rejected: {Reason}", node.Id, scored.RejectionReason);
                return scored;
            }

            // Step 4: Check utilization safety threshold
            var projectedCpuUtil = availability.ProjectedCpuUtilization(spec);
            var projectedMemUtil = availability.ProjectedMemoryUtilization(spec);

            if (projectedCpuUtil > _schedulingConfig.MaxUtilizationPercent ||
                projectedMemUtil > _schedulingConfig.MaxUtilizationPercent)
            {
                scored.RejectionReason = $"Would exceed max utilization " +
                    $"(CPU: {projectedCpuUtil:F1}%, MEM: {projectedMemUtil:F1}% > {_schedulingConfig.MaxUtilizationPercent}%)";
                scored.TotalScore = 0;
                _logger.LogDebug("Node {NodeId} rejected: {Reason}", node.Id, scored.RejectionReason);
                return scored;
            }

            // Step 5: Calculate component scores
            var scores = new NodeScores
            {
                CapacityScore = CalculateCapacityScore(availability, spec),
                LoadScore = CalculateLoadScore(node, availability),
                ReputationScore = CalculateReputationScore(node),
                LocalityScore = CalculateLocalityScore(node, spec)
            };

            scored.ComponentScores = scores;

            // Step 6: Calculate weighted total score
            var weights = _schedulingConfig.Weights;
            scored.TotalScore =
                (scores.CapacityScore * weights.Capacity) +
                (scores.LoadScore * weights.Load) +
                (scores.ReputationScore * weights.Reputation) +
                (scores.LocalityScore * weights.Locality);

            return await Task.FromResult(scored);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to score node {NodeId}", node.Id);
            throw;
        }
        
    }

    private string? ApplyHardFilters(Node node, VmSpec spec, TierPolicy policy)
    {
        // Filter 1: Node must be online
        if (node.Status != NodeStatus.Online)
            return $"Node offline (status: {node.Status})";

        // Filter 2: Must support required image
        if (!string.IsNullOrEmpty(spec.ImageId) &&
            node.SupportedImages != null &&
            !node.SupportedImages.Contains(spec.ImageId))
            return $"Image {spec.ImageId} not supported";

        // Filter 3: GPU requirement
        if (spec.RequiresGpu && !node.SupportsGpu)
            return "GPU required but not available";

        // Filter 4: Minimum total resources check (physical capacity)
        if (node.TotalResources.MemoryMb < _schedulingConfig.MinFreeMemoryMb)
            return $"Node total memory too low ({node.TotalResources.MemoryMb}MB < {_schedulingConfig.MinFreeMemoryMb}MB)";

        // Filter 5: Load average check (if metrics available)
        if (node.LatestMetrics?.LoadAverage > _schedulingConfig.MaxLoadAverage)
            return $"Load too high ({node.LatestMetrics.LoadAverage:F2} > {_schedulingConfig.MaxLoadAverage})";

        // Filter 6: Region requirement (if specified)
        if (!string.IsNullOrEmpty(spec.PreferredRegion) &&
            spec.RequirePreferredRegion &&
            node.Region != spec.PreferredRegion)
            return $"Region mismatch (required: {spec.PreferredRegion}, node: {node.Region})";

        return null; // Passed all filters
    }

    /// <summary>
    /// This is the source of truth for scheduling decisions
    /// </summary>
    private NodeResourceAvailability CalculateResourceAvailability(
        Node node,
        QualityTier tier,
        TierPolicy policy)
    {
        // Calculate effective capacity with overcommit ratios
        var effectiveCpu = node.TotalResources.CpuCores * policy.CpuOvercommitRatio;
        var effectiveMemory = node.TotalResources.MemoryMb * policy.MemoryOvercommitRatio;
        var effectiveStorage = node.TotalResources.StorageGb * policy.StorageOvercommitRatio;

        // This ensures scheduling decisions are based on reserved resources
        var allocatedCpu = (double)node.ReservedResources.CpuCores;
        var allocatedMemory = (double)node.ReservedResources.MemoryMb;
        var allocatedStorage = (double)node.ReservedResources.StorageGb;

        return new NodeResourceAvailability
        {
            NodeId = node.Id,
            Tier = tier,
            EffectiveCpuCapacity = effectiveCpu,
            EffectiveMemoryCapacity = effectiveMemory,
            EffectiveStorageCapacity = effectiveStorage,
            AllocatedCpu = allocatedCpu,
            AllocatedMemory = allocatedMemory,
            AllocatedStorage = allocatedStorage
        };
    }

    private double CalculateCapacityScore(NodeResourceAvailability availability, VmSpec spec)
    {
        // Calculate how much capacity remains AFTER placing this VM
        var cpuHeadroom = availability.RemainingCpu - spec.CpuCores;
        var memoryHeadroom = availability.RemainingMemory - spec.MemoryMb;

        // Normalize to 0-1 range (prefer nodes that will have 50%+ capacity remaining)
        var cpuScore = Math.Min(1.0, cpuHeadroom / (availability.EffectiveCpuCapacity * 0.5));
        var memoryScore = Math.Min(1.0, memoryHeadroom / (availability.EffectiveMemoryCapacity * 0.5));

        // Average of CPU and memory scores
        return (cpuScore + memoryScore) / 2.0;
    }

    private double CalculateLoadScore(Node node, NodeResourceAvailability availability)
    {
        // Use current utilization as primary indicator
        var cpuUtil = availability.CpuUtilization;
        var memoryUtil = availability.MemoryUtilization;

        // Convert utilization to score (inverse relationship)
        var cpuScore = 1.0 - (cpuUtil / 100.0);
        var memoryScore = 1.0 - (memoryUtil / 100.0);

        // Factor in load average if available
        double loadScore = 1.0;
        if (node.LatestMetrics?.LoadAverage != null)
        {
            // Normalize load average (assume 1.0 per core is normal)
            var normalizedLoad = node.LatestMetrics.LoadAverage / node.TotalResources.CpuCores;
            loadScore = Math.Max(0, 1.0 - normalizedLoad);
        }

        // Weighted average: utilization (70%), load average (30%)
        return (cpuScore * 0.35) + (memoryScore * 0.35) + (loadScore * 0.30);
    }

    private double CalculateReputationScore(Node node)
    {
        double score = 0.5; // Start with neutral score

        // Uptime bonus (max +0.3)
        var uptime = DateTime.UtcNow - node.RegisteredAt;
        var uptimeBonus = Math.Min(0.3, uptime.TotalDays / 30.0 * 0.3); // Max after 30 days
        score += uptimeBonus;

        // Recent heartbeat bonus (max +0.2)
        var timeSinceHeartbeat = DateTime.UtcNow - node.LastHeartbeat;
        if (timeSinceHeartbeat.TotalSeconds < 30)
            score += 0.2; // Very recent
        else if (timeSinceHeartbeat.TotalMinutes < 2)
            score += 0.1; // Recent

        return Math.Clamp(score, 0.0, 1.0);
    }

    private double CalculateLocalityScore(Node node, VmSpec spec)
    {
        if (string.IsNullOrEmpty(spec.PreferredRegion))
            return 0.5; // Neutral if no preference

        // Same region and zone
        if (node.Region == spec.PreferredRegion &&
            node.Zone == spec.PreferredZone)
            return 1.0;

        // Same region, different zone
        if (node.Region == spec.PreferredRegion)
            return 0.7;

        // Different region
        return 0.3;
    }

    // ============================================================================
    // Registration and Heartbeat
    // ============================================================================

    public async Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request)
    {
        var existingNode = _dataStore.Nodes.Values
            .FirstOrDefault(n => n.WalletAddress == request.WalletAddress);

        if (existingNode != null)
        {
            _logger.LogInformation("Node re-registration: {NodeId} ({Wallet})",
                existingNode.Id, request.WalletAddress);

            existingNode.Name = request.Name;
            existingNode.PublicIp = request.PublicIp;
            existingNode.AgentPort = request.AgentPort;
            existingNode.TotalResources = request.Resources;
            existingNode.AgentVersion = request.AgentVersion;
            existingNode.SupportedImages = request.SupportedImages;
            existingNode.SupportsGpu = request.SupportsGpu;
            existingNode.GpuInfo = request.GpuInfo;
            existingNode.Status = NodeStatus.Online;
            existingNode.LastHeartbeat = DateTime.UtcNow;

            await _dataStore.SaveNodeAsync(existingNode);

            var existingToken = GenerateAuthToken();
            _dataStore.NodeAuthTokens[existingNode.Id] = HashToken(existingToken);

            return new NodeRegistrationResponse(
                existingNode.Id,
                existingToken,
                TimeSpan.FromSeconds(15));
        }

        var node = new Node
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            WalletAddress = request.WalletAddress,
            PublicIp = request.PublicIp,
            AgentPort = request.AgentPort,
            Status = NodeStatus.Online,
            TotalResources = request.Resources,
            AvailableResources = request.Resources, // ✓ OK for new nodes
            ReservedResources = new NodeResources(),
            AgentVersion = request.AgentVersion,
            SupportedImages = request.SupportedImages,
            SupportsGpu = request.SupportsGpu,
            GpuInfo = request.GpuInfo,
            Region = request.Region ?? "default",
            Zone = request.Zone ?? "default",
            RegisteredAt = DateTime.UtcNow,
            LastHeartbeat = DateTime.UtcNow
        };

        var authToken = GenerateAuthToken();
        var tokenHash = HashToken(authToken);

        await _dataStore.SaveNodeAsync(node);
        _dataStore.NodeAuthTokens[node.Id] = tokenHash;

        _logger.LogInformation("New node registered: {NodeId} ({Name}) - {Wallet}",
            node.Id, node.Name, node.WalletAddress);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.NodeRegistered,
            ResourceType = "node",
            ResourceId = node.Id,
            Payload = new Dictionary<string, object>
            {
                ["name"] = node.Name,
                ["publicIp"] = node.PublicIp,
                ["cpuCores"] = node.TotalResources.CpuCores,
                ["memoryMb"] = node.TotalResources.MemoryMb,
                ["storageGb"] = node.TotalResources.StorageGb
            }
        });

        return new NodeRegistrationResponse(
            node.Id,
            authToken,
            TimeSpan.FromSeconds(15));
    }

    public Task<bool> ValidateNodeTokenAsync(string nodeId, string token)
    {
        if (!_dataStore.NodeAuthTokens.TryGetValue(nodeId, out var storedHash))
            return Task.FromResult(false);

        var tokenHash = HashToken(token);
        return Task.FromResult(storedHash == tokenHash);
    }

    /// <summary>
    /// Process heartbeat without overwriting orchestrator resource tracking
    /// </summary>
    public async Task<NodeHeartbeatResponse> ProcessHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
        {
            return new NodeHeartbeatResponse(false, null);
        }

        var wasOffline = node.Status == NodeStatus.Offline;
        node.Status = NodeStatus.Online;
        node.LastHeartbeat = DateTime.UtcNow;
        node.LatestMetrics = heartbeat.Metrics;

        // Optional: Log discrepancy between node-reported and orchestrator-tracked resources
        var nodeReportedFree = heartbeat.AvailableResources;
        var orchestratorTrackedFree = new NodeResources
        {
            CpuCores = node.TotalResources.CpuCores - node.ReservedResources.CpuCores,
            MemoryMb = node.TotalResources.MemoryMb - node.ReservedResources.MemoryMb,
            StorageGb = node.TotalResources.StorageGb - node.ReservedResources.StorageGb
        };

        var cpuDiff = Math.Abs(nodeReportedFree.CpuCores - orchestratorTrackedFree.CpuCores);
        var memDiff = Math.Abs(nodeReportedFree.MemoryMb - orchestratorTrackedFree.MemoryMb);

        if (cpuDiff > 1 || memDiff > 1024)
        {
            _logger.LogDebug(
                "Resource tracking drift on node {NodeId}: Node reports {NodeCpu}c/{NodeMem}MB free, " +
                "Orchestrator tracks {OrcCpu}c/{OrcMem}MB free (Reserved: {ResCpu}c/{ResMem}MB)",
                nodeId,
                nodeReportedFree.CpuCores, nodeReportedFree.MemoryMb,
                orchestratorTrackedFree.CpuCores, orchestratorTrackedFree.MemoryMb,
                node.ReservedResources.CpuCores, node.ReservedResources.MemoryMb);
        }

        await _dataStore.SaveNodeAsync(node);

        if (wasOffline)
        {
            _logger.LogInformation("Node {NodeId} came back online", nodeId);
            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.NodeOnline,
                ResourceType = "node",
                ResourceId = nodeId
            });
        }

        // Synchronize VM state from heartbeat
        await SyncVmStateFromHeartbeatAsync(nodeId, heartbeat);

        // Get pending commands for this node
        var commands = _dataStore.GetAndClearPendingCommands(nodeId);

        return new NodeHeartbeatResponse(true, commands.Count > 0 ? commands : null);
    }

    public async Task<bool> ProcessCommandAcknowledgmentAsync(
    string nodeId,
    string commandId,
    CommandAcknowledgment ack)
    {
        _logger.LogInformation(
            "Processing acknowledgment for command {CommandId} from node {NodeId}: Success={Success}",
            commandId, nodeId, ack.Success);

        // Get full command from acknowledgment tracking
        var command = _dataStore.CompleteCommandAck(commandId);

        if (command == null)
        {
            _logger.LogWarning(
                "Command {CommandId} not found in acknowledgment tracking - may have expired or already processed",
                commandId);
            return true; // Still return true - ack received
        }

        // Log processing time
        var processingTime = DateTime.UtcNow - command.QueuedAt;
        _logger.LogInformation(
            "Command {CommandId} ({Type}) processed in {Duration}ms",
            commandId, command.Type, processingTime.TotalMilliseconds);

        // Get the target resource (VM)
        var resourceId = command.TargetResourceId!;
        if (!_dataStore.VirtualMachines.TryGetValue(resourceId, out var vm))
        {
            _logger.LogWarning(
                "VM {VmId} associated with command {CommandId} not found - may have been deleted",
                resourceId, commandId);
            return true;
        }

        // Validate node matches
        if (vm.NodeId != nodeId)
        {
            _logger.LogWarning(
                "Command {CommandId} ack from node {NodeId} but VM {VmId} is on node {VmNodeId}",
                commandId, nodeId, resourceId, vm.NodeId);
            return false;
        }

        if (!ack.Success)
        {
            // Command failed
            _logger.LogError(
                "Command {CommandId} ({Type}) failed for VM {VmId}: {Error}",
                commandId, command.Type, resourceId, ack.ErrorMessage ?? "Unknown error");

            if (vm.Status == VmStatus.Deleting || vm.Status == VmStatus.Provisioning)
            {
                vm.Status = VmStatus.Error;
                vm.StatusMessage = $"{command.Type} failed: {ack.ErrorMessage ?? "Unknown error"}";
                vm.UpdatedAt = DateTime.UtcNow;
                await _dataStore.SaveVmAsync(vm);
            }

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "vm",
                ResourceId = vm.Id,
                NodeId = nodeId,
                UserId = vm.OwnerId,
                Payload = new Dictionary<string, object>
                {
                    ["CommandId"] = commandId,
                    ["CommandType"] = command.Type.ToString(),
                    ["Error"] = ack.ErrorMessage ?? "Unknown error",
                    ["ProcessingTimeMs"] = processingTime.TotalMilliseconds
                }
            });

            return true;
        }

        // Command succeeded - handle based on command type
        switch (command.Type)
        {
            case NodeCommandType.CreateVm:
                await HandleCreateVmAckAsync(vm, commandId, ack, processingTime);
                break;

            case NodeCommandType.DeleteVm:
                await HandleDeleteVmAckAsync(vm, commandId, processingTime);
                break;

            case NodeCommandType.StartVm:
            case NodeCommandType.StopVm:
                await HandleVmActionAckAsync(vm, command.Type, commandId);
                break;

            default:
                _logger.LogWarning(
                    "Unhandled command type {Type} for acknowledgment",
                    command.Type);
                break;
        }

        return true;
    }

    private async Task HandleCreateVmAckAsync(
        VirtualMachine vm,
        string commandId,
        CommandAcknowledgment ack,
        TimeSpan processingTime)
    {
        _logger.LogInformation(
            "VM {VmId} creation confirmed (provisioning took {Duration}s)",
            vm.Id, processingTime.TotalSeconds);

        vm.Status = VmStatus.Running;
        vm.PowerState = VmPowerState.Running;
        vm.StartedAt = ack.CompletedAt;
        vm.StatusMessage = null;
        vm.UpdatedAt = DateTime.UtcNow;
        await _dataStore.SaveVmAsync(vm);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmCreated,
            ResourceType = "vm",
            ResourceId = vm.Id,
            NodeId = vm.NodeId,
            UserId = vm.OwnerId,
            Payload = new Dictionary<string, object>
            {
                ["ProvisioningTimeSeconds"] = processingTime.TotalSeconds
            }
        });
    }

    private async Task HandleDeleteVmAckAsync(
        VirtualMachine vm,
        string commandId,
        TimeSpan processingTime)
    {
        _logger.LogInformation(
            "VM {VmId} deletion confirmed (deletion took {Duration}s) - completing cleanup",
            vm.Id, processingTime.TotalSeconds);

        await CompleteVmDeletionAsync(vm);
    }

    private async Task HandleVmActionAckAsync(
        VirtualMachine vm,
        NodeCommandType commandType,
        string commandId)
    {
        _logger.LogInformation(
            "VM {VmId} action {Action} confirmed",
            vm.Id, commandType);

        // Update VM state based on action
        // (Implementation depends on your state machine)
    }

    /// <summary>
    /// Complete VM deletion after node confirmation
    /// This is called by NodeService only - no circular dependency!
    /// </summary>
    private async Task CompleteVmDeletionAsync(VirtualMachine vm)
    {
        _logger.LogInformation(
            "Completing deletion for VM {VmId} (Owner: {Owner}, Node: {Node})",
            vm.Id, vm.OwnerId, vm.NodeId ?? "none");

        // Step 1: Mark as Deleted
        vm.Status = VmStatus.Deleted;
        vm.StatusMessage = "Deletion confirmed by node";
        vm.StoppedAt = DateTime.UtcNow;
        vm.UpdatedAt = DateTime.UtcNow;
        await _dataStore.SaveVmAsync(vm);

        // Step 2: Free reserved resources
        if (!string.IsNullOrEmpty(vm.NodeId) &&
            _dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            node.ReservedResources.CpuCores = Math.Max(0,
                node.ReservedResources.CpuCores - vm.Spec.CpuCores);
            node.ReservedResources.MemoryMb = Math.Max(0,
                node.ReservedResources.MemoryMb - vm.Spec.MemoryMb);
            node.ReservedResources.StorageGb = Math.Max(0,
                node.ReservedResources.StorageGb - vm.Spec.DiskGb);

            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "Released reserved resources for VM {VmId} on node {NodeId}: " +
                "{CpuCores}c, {MemoryMb}MB, {StorageGb}GB",
                vm.Id, node.Id, vm.Spec.CpuCores, vm.Spec.MemoryMb, vm.Spec.DiskGb);
        }

        // Step 3: Update user quotas
        if (_dataStore.Users.TryGetValue(vm.OwnerId, out var user))
        {
            user.Quotas.CurrentVms = Math.Max(0, user.Quotas.CurrentVms - 1);
            user.Quotas.CurrentCpuCores = Math.Max(0,
                user.Quotas.CurrentCpuCores - vm.Spec.CpuCores);
            user.Quotas.CurrentMemoryMb = Math.Max(0,
                user.Quotas.CurrentMemoryMb - vm.Spec.MemoryMb);
            user.Quotas.CurrentStorageGb = Math.Max(0,
                user.Quotas.CurrentStorageGb - vm.Spec.DiskGb);

            await _dataStore.SaveUserAsync(user);

            _logger.LogInformation(
                "Updated quotas for user {UserId}: VMs={VMs}, CPU={CPU}c, MEM={MEM}MB",
                user.Id, user.Quotas.CurrentVms, user.Quotas.CurrentCpuCores,
                user.Quotas.CurrentMemoryMb);
        }

        // Step 4: Emit completion event
        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmDeleted,
            ResourceType = "vm",
            ResourceId = vm.Id,
            NodeId = vm.NodeId,
            UserId = vm.OwnerId
        });

        _logger.LogInformation("VM {VmId} deletion completed successfully", vm.Id);
    }

    /// <summary>
    /// Synchronize VM state reported by node agent with orchestrator's view
    /// Handles VM state updates and orphan VM recovery
    /// </summary>
    private async Task SyncVmStateFromHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        if (heartbeat.ActiveVms == null || !heartbeat.ActiveVms.Any())
            return;

        var knownVmIds = _dataStore.VirtualMachines.Values
            .Where(v => v.NodeId == nodeId)
            .Select(v => v.Id)
            .ToHashSet();

        foreach (var reported in heartbeat.ActiveVms)
        {
            var vmId = reported.VmId;

            if (_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
            {
                // Update existing VM state
                var newStatus = ParseVmStatus(reported.State);
                var newPowerState = ParsePowerState(reported.State);

                if (vm.Status != newStatus || vm.PowerState != newPowerState)
                {
                    vm.Status = newStatus;
                    vm.PowerState = newPowerState;

                    if (newStatus == VmStatus.Running && vm.StartedAt == null)
                        vm.StartedAt = reported.StartedAt ?? DateTime.UtcNow;

                    await _dataStore.SaveVmAsync(vm);

                    _logger.LogInformation(
                        "VM {VmId} state updated from heartbeat: {Status}/{PowerState}",
                        vmId, newStatus, newPowerState);
                }

                // Update metrics if provided
                if (reported.CpuUsagePercent.HasValue)
                {
                    vm.LatestMetrics = new VmMetrics
                    {
                        CpuUsagePercent = reported.CpuUsagePercent.Value,
                        Timestamp = DateTime.UtcNow
                    };
                    await _dataStore.SaveVmAsync(vm);
                }

                // Update access info if available
                if (!string.IsNullOrEmpty(reported.IpAddress) &&
                    vm.NetworkConfig.PrivateIp != reported.IpAddress)
                {
                    vm.NetworkConfig.PrivateIp = reported.IpAddress;
                    await _dataStore.SaveVmAsync(vm);
                }
            }
            else if (!string.IsNullOrEmpty(reported.TenantId))
            {
                // Orphaned VM recovery
                await RecoverOrphanedVmAsync(nodeId, reported);
            }
        }
    }

    /// <summary>
    /// Recover VMs that exist on node but are unknown to orchestrator
    /// This handles orchestrator restarts where node state persists
    /// </summary>
    private async Task RecoverOrphanedVmAsync(string nodeId, HeartbeatVmInfo reported)
    {
        var vmId = reported.VmId;

        try
        {
            _logger.LogInformation(
                "Recovering orphaned VM {VmId} on node {NodeId} (Owner: {OwnerId}, State: {State})",
                vmId, nodeId, reported.TenantId, reported.State);

            var recoveredVm = new VirtualMachine
            {
                Id = vmId,
                Name = reported.Name ?? $"recovered-{vmId[..8]}",
                OwnerId = reported.TenantId ?? "unknown",
                NodeId = nodeId,
                Status = ParseVmStatus(reported.State),
                PowerState = ParsePowerState(reported.State),
                StartedAt = reported.StartedAt,
                CreatedAt = reported.StartedAt ?? DateTime.UtcNow,
                NetworkConfig = new VmNetworkConfig
                {
                    PrivateIp = reported.IpAddress ?? "",
                    Hostname = reported.Name ?? ""
                },
                Spec = new VmSpec
                {
                    CpuCores = reported.VCpus ?? 1,
                    MemoryMb = reported.MemoryBytes.HasValue
                        ? reported.MemoryBytes.Value / 1024 / 1024
                        : 1024,
                    DiskGb = reported.DiskBytes.HasValue
                        ? reported.DiskBytes.Value / 1024 / 1024 / 1024
                        : 20,
                    ImageId = "unknown"
                },
                StatusMessage = "Recovered from node heartbeat after orchestrator restart",
                Labels = new Dictionary<string, string>
                {
                    ["recovered"] = "true",
                    ["recovery-date"] = DateTime.UtcNow.ToString("O"),
                    ["recovery-node"] = nodeId
                }
            };

            if (reported.CpuUsagePercent.HasValue)
            {
                recoveredVm.LatestMetrics = new VmMetrics
                {
                    CpuUsagePercent = reported.CpuUsagePercent.Value,
                    Timestamp = DateTime.UtcNow
                };
            }

            await _dataStore.SaveVmAsync(recoveredVm);

            _logger.LogInformation(
                "✓ Successfully recovered VM {VmId} on node {NodeId} (Owner: {OwnerId}, State: {State})",
                vmId, nodeId, recoveredVm.OwnerId, recoveredVm.Status);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmRecovered,
                ResourceType = "vm",
                ResourceId = vmId,
                NodeId = nodeId,
                UserId = recoveredVm.OwnerId,
                Payload = new Dictionary<string, object>
                {
                    ["nodeId"] = nodeId,
                    ["state"] = recoveredVm.Status.ToString(),
                    ["ipAddress"] = recoveredVm.NetworkConfig.PrivateIp ?? "",
                    ["recoveryTimestamp"] = DateTime.UtcNow
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to recover orphaned VM {VmId} on node {NodeId}",
                vmId, nodeId);
        }
    }

    private VmStatus ParseVmStatus(string? state) => state?.ToLower() switch
    {
        "running" => VmStatus.Running,
        "stopped" => VmStatus.Stopped,
        "paused" => VmStatus.Running,
        "stopping" => VmStatus.Stopping,
        "failed" => VmStatus.Error,
        "error" => VmStatus.Error,
        _ => VmStatus.Error
    };

    private VmPowerState ParsePowerState(string? state) => state?.ToLower() switch
    {
        "running" => VmPowerState.Running,
        "paused" => VmPowerState.Paused,
        "stopped" => VmPowerState.Off,
        _ => VmPowerState.Off
    };

    // ============================================================================
    // Simple Getters
    // ============================================================================

    public Task<Node?> GetNodeAsync(string nodeId)
    {
        _dataStore.Nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(node);
    }

    public Task<List<Node>> GetAllNodesAsync(NodeStatus? statusFilter = null)
    {
        var nodes = _dataStore.Nodes.Values
            .Where(n => !statusFilter.HasValue || n.Status == statusFilter.Value)
            .OrderBy(n => n.Name)
            .ToList();

        return Task.FromResult(nodes);
    }

    public async Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status)
    {
        if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            return false;

        node.Status = status;
        await _dataStore.SaveNodeAsync(node);
        return true;
    }

    public async Task<bool> RemoveNodeAsync(string nodeId)
    {
        if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            return false;

        await _dataStore.DeleteNodeAsync(nodeId);
        _logger.LogInformation("Node removed: {NodeId} ({Name})", nodeId, node.Name);
        return true;
    }

    // ============================================================================
    // Health Monitoring
    // ============================================================================

    public async Task CheckNodeHealthAsync()
    {
        var heartbeatTimeout = TimeSpan.FromMinutes(2);
        var now = DateTime.UtcNow;

        var onlineNodes = _dataStore.Nodes.Values
            .Where(n => n.Status == NodeStatus.Online)
            .ToList();

        foreach (var node in onlineNodes)
        {
            var timeSinceLastHeartbeat = now - node.LastHeartbeat;

            if (timeSinceLastHeartbeat > heartbeatTimeout)
            {
                _logger.LogWarning(
                    "Node {NodeId} ({Name}) marked as offline - no heartbeat for {Minutes:F1} minutes",
                    node.Id, node.Name, timeSinceLastHeartbeat.TotalMinutes);

                node.Status = NodeStatus.Offline;
                await _dataStore.SaveNodeAsync(node);

                await _eventService.EmitAsync(new OrchestratorEvent
                {
                    Type = EventType.NodeOffline,
                    ResourceType = "node",
                    ResourceId = node.Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["lastHeartbeat"] = node.LastHeartbeat,
                        ["timeoutMinutes"] = timeSinceLastHeartbeat.TotalMinutes
                    }
                });

                await MarkNodeVmsAsErrorAsync(node.Id);
            }
        }
    }

    private async Task MarkNodeVmsAsErrorAsync(string nodeId)
    {
        var nodeVms = _dataStore.VirtualMachines.Values
            .Where(v => v.NodeId == nodeId && v.Status == VmStatus.Running)
            .ToList();

        foreach (var vm in nodeVms)
        {
            _logger.LogWarning("VM {VmId} on offline node {NodeId} marked as error",
                vm.Id, nodeId);

            vm.Status = VmStatus.Error;
            vm.StatusMessage = "Node went offline";
            vm.UpdatedAt = DateTime.UtcNow;

            await _dataStore.SaveVmAsync(vm);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "vm",
                ResourceId = vm.Id,
                UserId = vm.OwnerId,
                NodeId = nodeId,
                Payload = new Dictionary<string, object>
                {
                    ["reason"] = "Node offline",
                    ["nodeId"] = nodeId
                }
            });
        }
    }

    // ============================================================================
    // Utilities
    // ============================================================================

    private static string GenerateAuthToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string HashToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string SafeSubstring(string s, int maxLength)
    {
        return s.Length > maxLength ? s[..maxLength] : s;
    }
}