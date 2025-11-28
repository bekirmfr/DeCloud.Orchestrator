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
}

public class NodeService : INodeService
{
    private readonly OrchestratorDataStore _dataStore;
    private readonly IEventService _eventService;
    private readonly ILogger<NodeService> _logger;
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(90);

    public NodeService(
        OrchestratorDataStore dataStore,
        IEventService eventService,
        ILogger<NodeService> logger)
    {
        _dataStore = dataStore;
        _eventService = eventService;
        _logger = logger;
    }

    public async Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request)
    {
        // Check if node with same wallet already exists
        var existingNode = _dataStore.Nodes.Values
            .FirstOrDefault(n => n.WalletAddress.Equals(request.WalletAddress, StringComparison.OrdinalIgnoreCase));

        string nodeId;
        string authToken = GenerateAuthToken();

        if (existingNode != null)
        {
            // Update existing node
            nodeId = existingNode.Id;
            existingNode.Name = request.Name;
            existingNode.Endpoint = $"{request.PublicIp}:{request.AgentPort}";
            existingNode.PublicIp = request.PublicIp;
            existingNode.AgentPort = request.AgentPort;
            existingNode.TotalResources = request.Resources;
            existingNode.AvailableResources = new NodeResources
            {
                CpuCores = request.Resources.CpuCores,
                MemoryMb = request.Resources.MemoryMb,
                StorageGb = request.Resources.StorageGb,
                BandwidthMbps = request.Resources.BandwidthMbps
            };
            existingNode.SupportedImages = request.SupportedImages;
            existingNode.SupportsGpu = request.SupportsGpu;
            existingNode.GpuInfo = request.GpuInfo;
            existingNode.Region = request.Region;
            existingNode.Zone = request.Zone;
            existingNode.AgentVersion = request.AgentVersion;
            existingNode.Status = NodeStatus.Online;
            existingNode.LastHeartbeat = DateTime.UtcNow;

            _logger.LogInformation("Node re-registered: {NodeId} ({Name}) from {Wallet}",
                nodeId, request.Name, request.WalletAddress);
        }
        else
        {
            // Create new node
            nodeId = Guid.NewGuid().ToString();
            var node = new Node
            {
                Id = nodeId,
                Name = request.Name,
                WalletAddress = request.WalletAddress,
                Endpoint = $"{request.PublicIp}:{request.AgentPort}",
                PublicIp = request.PublicIp,
                AgentPort = request.AgentPort,
                Status = NodeStatus.Online,
                TotalResources = request.Resources,
                AvailableResources = new NodeResources
                {
                    CpuCores = request.Resources.CpuCores,
                    MemoryMb = request.Resources.MemoryMb,
                    StorageGb = request.Resources.StorageGb,
                    BandwidthMbps = request.Resources.BandwidthMbps
                },
                SupportedImages = request.SupportedImages,
                SupportsGpu = request.SupportsGpu,
                GpuInfo = request.GpuInfo,
                Region = request.Region,
                Zone = request.Zone,
                AgentVersion = request.AgentVersion,
                RegisteredAt = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow
            };

            _dataStore.Nodes.TryAdd(nodeId, node);

            _logger.LogInformation("New node registered: {NodeId} ({Name}) from {Wallet}",
                nodeId, request.Name, request.WalletAddress);
        }

        // Store auth token (hashed)
        var tokenHash = HashToken(authToken);
        _dataStore.NodeAuthTokens[nodeId] = tokenHash;

        // Emit event
        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.NodeRegistered,
            ResourceType = "node",
            ResourceId = nodeId,
            Payload = new { request.Name, request.WalletAddress, request.Region }
        });

        return new NodeRegistrationResponse(nodeId, authToken, _heartbeatInterval);
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

        // Update node state
        var wasOffline = node.Status == NodeStatus.Offline;
        node.Status = NodeStatus.Online;
        node.LastHeartbeat = DateTime.UtcNow;
        node.LatestMetrics = heartbeat.Metrics;
        node.AvailableResources = heartbeat.AvailableResources;

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

        // =====================================================
        // NEW: Sync VM state from heartbeat
        // =====================================================
        await SyncVmStateFromHeartbeatAsync(nodeId, heartbeat);

        // Get any pending commands for this node
        var commands = _dataStore.GetAndClearPendingCommands(nodeId);

        return new NodeHeartbeatResponse(true, commands.Count > 0 ? commands : null);
    }

    /// <summary>
    /// Synchronize VM state based on heartbeat data from node agent.
    /// This updates VMs that are in Provisioning state to Running when the node reports them as running.
    /// </summary>
    private async Task SyncVmStateFromHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        // Get all VMs assigned to this node
        var nodeVms = _dataStore.VirtualMachines.Values
            .Where(v => v.NodeId == nodeId && v.Status != VmStatus.Deleted)
            .ToList();

        if (!nodeVms.Any())
            return;

        // Create a lookup of reported VMs from the node
        var reportedVmIds = new HashSet<string>(heartbeat.ActiveVmIds ?? new List<string>());

        // Create a lookup of VM details if provided
        var vmDetails = new Dictionary<string, NodeVmInfo>();
        if (heartbeat.ActiveVms != null)
        {
            foreach (var vmInfo in heartbeat.ActiveVms)
            {
                if (!string.IsNullOrEmpty(vmInfo.VmId))
                {
                    vmDetails[vmInfo.VmId] = vmInfo;
                }
            }
        }

        foreach (var vm in nodeVms)
        {
            var isReportedByNode = reportedVmIds.Contains(vm.Id);
            vmDetails.TryGetValue(vm.Id, out var nodeVmInfo);

            // Determine node-reported state
            var nodeState = nodeVmInfo?.State;
            var nodeIpAddress = nodeVmInfo?.IpAddress;

            switch (vm.Status)
            {
                case VmStatus.Provisioning:
                    // VM was being created - check if node reports it as running
                    if (isReportedByNode &&
                        (string.Equals(nodeState, "Running", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(nodeState, "Started", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogInformation(
                            "VM {VmId} transitioned from Provisioning to Running (reported by node {NodeId})",
                            vm.Id, nodeId);

                        vm.Status = VmStatus.Running;
                        vm.PowerState = VmPowerState.Running;
                        vm.StartedAt = DateTime.UtcNow;
                        vm.UpdatedAt = DateTime.UtcNow;
                        vm.StatusMessage = null;

                        // Update IP address if reported
                        if (!string.IsNullOrEmpty(nodeIpAddress))
                        {
                            vm.NetworkConfig.PrivateIp = nodeIpAddress;
                            _logger.LogInformation("VM {VmId} IP address: {IpAddress}", vm.Id, nodeIpAddress);
                        }

                        await _eventService.EmitAsync(new OrchestratorEvent
                        {
                            Type = EventType.VmStarted,
                            ResourceType = "vm",
                            ResourceId = vm.Id,
                            Payload = new { NodeId = nodeId, IpAddress = nodeIpAddress }
                        });
                    }
                    else if (string.Equals(nodeState, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("VM {VmId} failed to provision on node {NodeId}", vm.Id, nodeId);
                        vm.Status = VmStatus.Error;
                        vm.StatusMessage = "VM creation failed on node";
                        vm.UpdatedAt = DateTime.UtcNow;

                        await _eventService.EmitAsync(new OrchestratorEvent
                        {
                            Type = EventType.VmError,
                            ResourceType = "vm",
                            ResourceId = vm.Id,
                            Payload = new { NodeId = nodeId, Error = "Provisioning failed" }
                        });
                    }
                    break;

                case VmStatus.Running:
                    // VM should be running - verify with node
                    if (!isReportedByNode)
                    {
                        // Node doesn't know about this VM - might have crashed or been deleted
                        _logger.LogWarning(
                            "VM {VmId} marked as Running but not reported by node {NodeId}",
                            vm.Id, nodeId);
                        // Don't immediately mark as error - could be transient
                    }
                    else if (string.Equals(nodeState, "Stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("VM {VmId} stopped (reported by node {NodeId})", vm.Id, nodeId);
                        vm.Status = VmStatus.Stopped;
                        vm.PowerState = VmPowerState.Off;
                        vm.StoppedAt = DateTime.UtcNow;
                        vm.UpdatedAt = DateTime.UtcNow;

                        await _eventService.EmitAsync(new OrchestratorEvent
                        {
                            Type = EventType.VmStopped,
                            ResourceType = "vm",
                            ResourceId = vm.Id,
                            Payload = new { NodeId = nodeId }
                        });
                    }
                    else
                    {
                        // Update IP if changed
                        if (!string.IsNullOrEmpty(nodeIpAddress) &&
                            vm.NetworkConfig.PrivateIp != nodeIpAddress)
                        {
                            _logger.LogInformation(
                                "VM {VmId} IP address updated: {OldIp} -> {NewIp}",
                                vm.Id, vm.NetworkConfig.PrivateIp, nodeIpAddress);
                            vm.NetworkConfig.PrivateIp = nodeIpAddress;
                            vm.UpdatedAt = DateTime.UtcNow;
                        }
                    }
                    break;

                case VmStatus.Stopping:
                    // VM is being stopped - check if node reports it as stopped
                    if (!isReportedByNode ||
                        string.Equals(nodeState, "Stopped", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("VM {VmId} stopped (reported by node {NodeId})", vm.Id, nodeId);
                        vm.Status = VmStatus.Stopped;
                        vm.PowerState = VmPowerState.Off;
                        vm.StoppedAt = DateTime.UtcNow;
                        vm.UpdatedAt = DateTime.UtcNow;

                        await _eventService.EmitAsync(new OrchestratorEvent
                        {
                            Type = EventType.VmStopped,
                            ResourceType = "vm",
                            ResourceId = vm.Id,
                            Payload = new { NodeId = nodeId }
                        });
                    }
                    break;

                case VmStatus.Stopped:
                    // VM is stopped - check if node reports it as running (manual start?)
                    if (isReportedByNode &&
                        string.Equals(nodeState, "Running", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "VM {VmId} started (reported by node {NodeId}, was stopped)",
                            vm.Id, nodeId);
                        vm.Status = VmStatus.Running;
                        vm.PowerState = VmPowerState.Running;
                        vm.StartedAt = DateTime.UtcNow;
                        vm.UpdatedAt = DateTime.UtcNow;

                        if (!string.IsNullOrEmpty(nodeIpAddress))
                        {
                            vm.NetworkConfig.PrivateIp = nodeIpAddress;
                        }

                        await _eventService.EmitAsync(new OrchestratorEvent
                        {
                            Type = EventType.VmStarted,
                            ResourceType = "vm",
                            ResourceId = vm.Id,
                            Payload = new { NodeId = nodeId, IpAddress = nodeIpAddress }
                        });
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Check health of all nodes - marks nodes as offline if heartbeat timed out.
    /// Called by NodeHealthMonitorService background service.
    /// </summary>
    public async Task CheckNodeHealthAsync()
    {
        var now = DateTime.UtcNow;
        var nodesToCheck = _dataStore.Nodes.Values
            .Where(n => n.Status == NodeStatus.Online)
            .ToList();

        foreach (var node in nodesToCheck)
        {
            var timeSinceHeartbeat = now - node.LastHeartbeat;

            if (timeSinceHeartbeat > _heartbeatTimeout)
            {
                _logger.LogWarning(
                    "Node {NodeId} ({Name}) heartbeat timeout - last seen {Seconds}s ago",
                    node.Id, node.Name, timeSinceHeartbeat.TotalSeconds);

                node.Status = NodeStatus.Offline;

                await _eventService.EmitAsync(new OrchestratorEvent
                {
                    Type = EventType.NodeOffline,
                    ResourceType = "node",
                    ResourceId = node.Id,
                    Payload = new
                    {
                        node.Name,
                        LastHeartbeat = node.LastHeartbeat,
                        TimeoutSeconds = timeSinceHeartbeat.TotalSeconds
                    }
                });
            }
        }
    }

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

    public Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status)
    {
        if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            return Task.FromResult(false);

        node.Status = status;
        return Task.FromResult(true);
    }

    public Task<bool> RemoveNodeAsync(string nodeId)
    {
        if (!_dataStore.Nodes.TryRemove(nodeId, out var node))
            return Task.FromResult(false);

        _dataStore.NodeAuthTokens.TryRemove(nodeId, out _);

        _logger.LogInformation("Node removed: {NodeId} ({Name})", nodeId, node.Name);

        return Task.FromResult(true);
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
}