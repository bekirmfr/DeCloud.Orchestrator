using System.Security.Cryptography;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Services;

public interface INodeService
{
    Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request);
    Task<bool> ValidateNodeTokenAsync(string nodeId, string token);
    Task<NodeHeartbeatResponse> ProcessHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat);
    Task<Node?> GetNodeAsync(string nodeId);
    Task<List<Node>> GetAllNodesAsync(NodeStatus? statusFilter = null);
    Task<List<Node>> GetAvailableNodesForVmAsync(VmSpec spec);
    Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status);
    Task<bool> RemoveNodeAsync(string nodeId);
    Task CheckNodeHealthAsync();
}

public class NodeService : INodeService
{
    private readonly DataStore _dataStore;
    private readonly IEventService _eventService;
    private readonly ILogger<NodeService> _logger;

    public NodeService(
        DataStore dataStore,
        IEventService eventService,
        ILogger<NodeService> logger)
    {
        _dataStore = dataStore;
        _eventService = eventService;
        _logger = logger;
    }

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
            existingNode.AvailableResources = request.Resources;
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
            AvailableResources = request.Resources,
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
            Payload = new { node.Name, node.PublicIp, node.TotalResources }
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
        node.AvailableResources = heartbeat.AvailableResources;

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

    /// <summary>
    /// Synchronize VM state based on heartbeat data from node agent.
    /// Handles three scenarios:
    /// 1. Update state for VMs we know about
    /// 2. Detect missing VMs (expected but not reported)
    /// 3. Recover orphaned VMs (reported but unknown to orchestrator)
    /// </summary>
    private async Task SyncVmStateFromHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        try
        {
            // Build dictionaries for efficient lookup
            var orchestratorVms = _dataStore.VirtualMachines.Values
                .Where(v => v.NodeId == nodeId && v.Status != VmStatus.Deleted)
                .ToDictionary(v => v.Id);

            var reportedVms = new Dictionary<string, HeartbeatVmInfo>();
            if (heartbeat.ActiveVms != null)
            {
                foreach (var vm in heartbeat.ActiveVms)
                {
                    reportedVms[vm.VmId] = vm;
                }
            }

            // SCENARIO 1: Update state for VMs we know about
            foreach (var (vmId, vm) in orchestratorVms)
            {
                if (reportedVms.TryGetValue(vmId, out var reported))
                {
                    await UpdateKnownVmStateAsync(vm, reported, nodeId);
                }
                else
                {
                    await HandleMissingVmAsync(vm, nodeId);
                }
            }

            // SCENARIO 2: Recover VMs that node has but we don't know about
            foreach (var (vmId, reported) in reportedVms)
            {
                if (!orchestratorVms.ContainsKey(vmId))
                {
                    await RecoverOrphanedVmAsync(vmId, reported, nodeId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync VM state from heartbeat for node {NodeId}", nodeId);
        }
    }

    private async Task UpdateKnownVmStateAsync(VirtualMachine vm, HeartbeatVmInfo reported, string nodeId)
    {
        var reportedState = ParseVmStatus(reported.State);
        var stateChanged = false;

        // Transition from Provisioning to Running when node reports it's running
        if (vm.Status == VmStatus.Provisioning && reportedState == VmStatus.Running)
        {
            vm.Status = VmStatus.Running;
            vm.PowerState = VmPowerState.Running;
            vm.StartedAt ??= reported.StartedAt ?? DateTime.UtcNow;
            stateChanged = true;
        }

        // Update IP address if not set
        if (string.IsNullOrEmpty(vm.NetworkConfig.PrivateIp) && !string.IsNullOrEmpty(reported.IpAddress))
        {
            vm.NetworkConfig.PrivateIp = reported.IpAddress;
            stateChanged = true;
        }

        // Update metrics if available
        if (reported.CpuUsagePercent.HasValue)
        {
            vm.LatestMetrics ??= new VmMetrics();
            vm.LatestMetrics.CpuUsagePercent = reported.CpuUsagePercent.Value;
            vm.LatestMetrics.Timestamp = DateTime.UtcNow;
        }

        if (stateChanged)
        {
            await _dataStore.SaveVmAsync(vm);

            _logger.LogInformation(
                "VM {VmId} state updated from heartbeat: Status={Status}, IP={Ip}",
                vm.Id, vm.Status, vm.NetworkConfig.PrivateIp);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmStateChanged,
                ResourceType = "vm",
                ResourceId = vm.Id,
                UserId = vm.OwnerId,
                Payload = new
                {
                    NewStatus = vm.Status.ToString(),
                    IpAddress = vm.NetworkConfig.PrivateIp,
                    NodeId = nodeId
                }
            });
        }
    }

    private async Task HandleMissingVmAsync(VirtualMachine vm, string nodeId)
    {
        // Only flag as error if we expected it to be running
        if (vm.Status == VmStatus.Running || vm.Status == VmStatus.Provisioning)
        {
            _logger.LogWarning(
                "VM {VmId} expected on node {NodeId} but not reported in heartbeat - marking as error",
                vm.Id, nodeId);

            vm.Status = VmStatus.Error;
            vm.StatusMessage = "VM not found on assigned node";
            await _dataStore.SaveVmAsync(vm);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "vm",
                ResourceId = vm.Id,
                UserId = vm.OwnerId,
                Payload = new
                {
                    NodeId = nodeId,
                    Error = "VM missing from node",
                    ExpectedState = vm.Status.ToString()
                }
            });
        }
    }

    private async Task RecoverOrphanedVmAsync(string vmId, HeartbeatVmInfo reported, string nodeId)
    {
        _logger.LogWarning(
            "Discovered unknown VM {VmId} on node {NodeId} - attempting state recovery",
            vmId, nodeId);

        try
        {
            // =====================================================
            // SECURITY VALIDATION: Critical checks before recovery
            // =====================================================

            // Validation 1: VM ID format
            if (!Guid.TryParse(vmId, out _))
            {
                _logger.LogWarning(
                    "Rejecting VM recovery for {VmId} - invalid UUID format",
                    vmId);
                return;
            }

            // Validation 2: Node exists and is valid
            if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            {
                _logger.LogError(
                    "Cannot recover VM {VmId} - node {NodeId} not found in registry",
                    vmId, nodeId);
                return;
            }

            // Validation 3: Tenant/Owner validation
            if (!string.IsNullOrEmpty(reported.TenantId))
            {
                if (!_dataStore.Users.TryGetValue(reported.TenantId, out var owner))
                {
                    _logger.LogWarning(
                        "Rejecting VM {VmId} recovery - unknown or invalid tenant {TenantId}",
                        vmId, reported.TenantId);
                    return;
                }

                // Validation 4: Check tenant is active (not suspended/banned)
                if (owner.Status != UserStatus.Active)
                {
                    _logger.LogWarning(
                        "Rejecting VM {VmId} recovery - tenant {TenantId} has status {Status}",
                        vmId, reported.TenantId, owner.Status);
                    return;
                }
            }

            // Validation 5: Resource limits validation
            if (reported.VCpus.HasValue && reported.VCpus > node.TotalResources.CpuCores)
            {
                _logger.LogWarning(
                    "Rejecting VM {VmId} - reported vCPUs ({VCpus}) exceed node capacity ({NodeCpus})",
                    vmId, reported.VCpus, node.TotalResources.CpuCores);
                return;
            }

            if (reported.MemoryBytes.HasValue)
            {
                var memoryMb = reported.MemoryBytes.Value / 1024 / 1024;
                if (memoryMb > node.TotalResources.MemoryMb)
                {
                    _logger.LogWarning(
                        "Rejecting VM {VmId} - reported memory ({MemoryMb}MB) exceeds node capacity ({NodeMemMb}MB)",
                        vmId, memoryMb, node.TotalResources.MemoryMb);
                    return;
                }
            }

            // Validation 6: State validation
            var vmState = ParseVmStatus(reported.State);
            if (vmState == VmStatus.Error || vmState == VmStatus.Deleted)
            {
                _logger.LogWarning(
                    "Rejecting VM {VmId} recovery - invalid state: {State}",
                    vmId, reported.State);
                return;
            }

            // =====================================================
            // Recovery: Create VM record from node's data
            // =====================================================

            var recoveredVm = new VirtualMachine
            {
                Id = vmId,
                Name = reported.Name ?? $"recovered-vm-{vmId[..8]}",
                NodeId = nodeId,
                OwnerId = reported.TenantId ?? "unknown",
                OwnerWallet = "recovered",
                Status = vmState,
                PowerState = ParsePowerState(reported.State),
                CreatedAt = reported.StartedAt ?? DateTime.UtcNow.AddMinutes(-5),
                StartedAt = reported.StartedAt,
                NetworkConfig = new VmNetworkConfig
                {
                    PrivateIp = reported.IpAddress,
                    Hostname = reported.Name ?? $"vm-{vmId[..8]}"
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

            // Update latest metrics if available
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
                Payload = new
                {
                    NodeId = nodeId,
                    State = recoveredVm.Status.ToString(),
                    IpAddress = recoveredVm.NetworkConfig.PrivateIp,
                    RecoveryTimestamp = DateTime.UtcNow
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

    public Task<List<Node>> GetAvailableNodesForVmAsync(VmSpec spec)
    {
        var nodes = _dataStore.Nodes.Values
            .Where(n => n.Status == NodeStatus.Online)
            .Where(n => n.AvailableResources.CpuCores >= spec.CpuCores)
            .Where(n => n.AvailableResources.MemoryMb >= spec.MemoryMb)
            .Where(n => n.AvailableResources.StorageGb >= spec.DiskGb)
            .Where(n => !spec.RequiresGpu || n.SupportsGpu)
            .OrderByDescending(n => n.AvailableResources.CpuCores + n.AvailableResources.MemoryMb / 1024)
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

    /// <summary>
    /// Check health of all nodes and mark offline if heartbeat timeout exceeded
    /// Called periodically by NodeHealthMonitorService background service
    /// </summary>
    public async Task CheckNodeHealthAsync()
    {
        var heartbeatTimeout = TimeSpan.FromMinutes(2); // 2 minutes without heartbeat = offline
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
                    Payload = new
                    {
                        LastHeartbeat = node.LastHeartbeat,
                        TimeoutMinutes = timeSinceLastHeartbeat.TotalMinutes
                    }
                });

                // Mark all VMs on this node as in error state
                await MarkNodeVmsAsErrorAsync(node.Id);
            }
        }
    }

    /// <summary>
    /// Mark all VMs on an offline node as in error state
    /// </summary>
    private async Task MarkNodeVmsAsErrorAsync(string nodeId)
    {
        var nodeVms = _dataStore.VirtualMachines.Values
            .Where(v => v.NodeId == nodeId && v.Status == VmStatus.Running)
            .ToList();

        foreach (var vm in nodeVms)
        {
            _logger.LogWarning(
                "VM {VmId} on offline node {NodeId} marked as error",
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
                Payload = new
                {
                    Reason = "Node offline",
                    NodeId = nodeId
                }
            });
        }
    }
}