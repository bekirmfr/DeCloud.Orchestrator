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
            _logger.LogWarning("Heartbeat from unknown node {NodeId}", nodeId);
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
                ResourceId = nodeId,
                Payload = new { node.Name, WasOffline = true }
            });
        }

        // =====================================================
        //  Sync VM state from heartbeat
        // =====================================================
        await SyncVmStateFromHeartbeatAsync(nodeId, heartbeat);

        // Get any pending commands for this node
        var commands = _dataStore.GetAndClearPendingCommands(nodeId);

        return new NodeHeartbeatResponse(true, commands.Count > 0 ? commands : null);
    }

    /// <summary>
    /// Synchronize VM state between orchestrator and node agent.
    /// Handles three scenarios:
    /// 1. State updates for known VMs (Provisioning -> Running)
    /// 2. Recovery of VMs that exist on node but not in orchestrator (after restart)
    /// 3. Detection of missing VMs that should be on the node
    /// </summary>
    private async Task SyncVmStateFromHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        try
        {
            // Get all VMs assigned to this node in our records
            var orchestratorVms = _dataStore.VirtualMachines.Values
                .Where(v => v.NodeId == nodeId && v.Status != VmStatus.Deleted)
                .ToDictionary(v => v.Id);

            // Build lookup of VMs reported by the node
            var reportedVms = new Dictionary<string, HeartbeatVmInfo>();

            // Support both old format (activeVmIds) and new format (activeVms)
            if (heartbeat.ActiveVms != null && heartbeat.ActiveVms.Any())
            {
                foreach (var vm in heartbeat.ActiveVms)
                {
                    reportedVms[vm.VmId] = vm;
                }
            }

            _logger.LogDebug("Node {NodeId} reports {Count} active VMs", nodeId, reportedVms.Count);

            // ===================================================================
            // SCENARIO 1: Update state for VMs we know about
            // ===================================================================
            foreach (var (vmId, vm) in orchestratorVms)
            {
                if (reportedVms.TryGetValue(vmId, out var reported))
                {
                    await UpdateKnownVmStateAsync(vm, reported, nodeId);
                }
                else
                {
                    // VM exists in our records but node doesn't report it
                    await HandleMissingVmAsync(vm, nodeId);
                }
            }

            // ===================================================================
            // SCENARIO 2: Recover VMs that node has but we don't know about
            // This handles orchestrator restart without MongoDB persistence
            // ===================================================================
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

    /// <summary>
    /// Update state for VMs we're already tracking
    /// </summary>
    private async Task UpdateKnownVmStateAsync(VirtualMachine vm, HeartbeatVmInfo reported, string nodeId)
    {
        var reportedState = ParseVmStatus(reported.State);
        var stateChanged = false;

        // Transition from Provisioning to Running when node confirms it's up
        if (vm.Status == VmStatus.Provisioning && reportedState == VmStatus.Running)
        {
            vm.Status = VmStatus.Running;
            vm.PowerState = VmPowerState.Running;
            vm.StartedAt ??= reported.StartedAt ?? DateTime.UtcNow;
            stateChanged = true;

            _logger.LogInformation("VM {VmId} ({Name}) transitioned to Running on node {NodeId}",
                vm.Id, vm.Name, nodeId);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmStarted,  // FIXED: Use VmStarted instead of VmRunning
                ResourceType = "vm",
                ResourceId = vm.Id,
                UserId = vm.OwnerId,
                Payload = new
                {
                    vm.Name,
                    IpAddress = reported.IpAddress,
                    NodeId = nodeId  // FIXED: NodeId in Payload, not as direct property
                }
            });
        }
        // Update IP address if we don't have it
        else if (string.IsNullOrEmpty(vm.NetworkConfig.PrivateIp) && !string.IsNullOrEmpty(reported.IpAddress))
        {
            vm.NetworkConfig.PrivateIp = reported.IpAddress;
            stateChanged = true;

            _logger.LogInformation("VM {VmId} IP address updated to {Ip}", vm.Id, reported.IpAddress);
        }
        // Handle state mismatches
        else if (vm.Status == VmStatus.Running && reportedState != VmStatus.Running)
        {
            _logger.LogWarning("VM {VmId} state mismatch - Orchestrator: {OurState}, Node: {NodeState}",
                vm.Id, vm.Status, reportedState);

            // Trust the node's state for running VMs
            vm.Status = reportedState;
            vm.PowerState = ParsePowerState(reported.State);
            stateChanged = true;
        }

        if (stateChanged)
        {
            vm.UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Handle VMs that should be on the node but aren't reported
    /// </summary>
    private async Task HandleMissingVmAsync(VirtualMachine vm, string nodeId)
    {
        // Only flag as error if we expected it to be running
        if (vm.Status == VmStatus.Running || vm.Status == VmStatus.Provisioning)
        {
            _logger.LogWarning(
                "VM {VmId} ({Name}) expected on node {NodeId} but not reported in heartbeat - marking as error",
                vm.Id, vm.Name, nodeId);

            vm.Status = VmStatus.Error;
            vm.PowerState = VmPowerState.Off;
            vm.StatusMessage = "VM not found on assigned node during heartbeat";
            vm.UpdatedAt = DateTime.UtcNow;

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "vm",
                ResourceId = vm.Id,
                UserId = vm.OwnerId,
                Payload = new
                {
                    vm.Name,
                    NodeId = nodeId,  // FIXED: NodeId in Payload, not as direct property
                    Error = "VM missing from node",
                    ExpectedState = vm.Status.ToString()
                }
            });
        }
    }

    /// <summary>
    /// Recover VMs that exist on node but not in orchestrator records
    /// This is critical for state recovery after orchestrator restart
    /// </summary>
    private async Task RecoverOrphanedVmAsync(string vmId, HeartbeatVmInfo reported, string nodeId)
    {
        _logger.LogWarning(
            "Discovered unknown VM {VmId} on node {NodeId} - attempting state recovery",
            vmId, nodeId);

        try
        {
            // Validate the VM ID is a proper GUID
            if (!Guid.TryParse(vmId, out _))
            {
                _logger.LogWarning("Rejecting VM recovery for {VmId} - invalid ID format", vmId);
                return;
            }

            // Get node info for context
            if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            {
                _logger.LogError("Cannot recover VM {VmId} - node {NodeId} not found", vmId, nodeId);
                return;
            }

            // Create recovered VM record with information from heartbeat
            var recoveredVm = new VirtualMachine
            {
                Id = vmId,
                Name = reported.Name ?? $"recovered-vm-{vmId.Substring(0, 8)}",
                NodeId = nodeId,

                // Ownership - try to extract from heartbeat, otherwise mark as unknown
                OwnerId = reported.TenantId ?? "unknown",
                OwnerWallet = "unknown",  // Will need to be corrected manually or via user lookup

                // State based on what node reports
                Status = ParseVmStatus(reported.State),
                PowerState = ParsePowerState(reported.State),
                StatusMessage = "Recovered from node agent after orchestrator restart",

                // Timestamps
                CreatedAt = reported.StartedAt ?? DateTime.UtcNow.AddHours(-1), // Best guess
                StartedAt = reported.StartedAt,
                UpdatedAt = DateTime.UtcNow,

                // Network configuration
                NetworkConfig = new VmNetworkConfig
                {
                    PrivateIp = reported.IpAddress,
                    Hostname = reported.Name
                },

                // Spec - use defaults or info from heartbeat if available
                Spec = new VmSpec
                {
                    CpuCores = reported.VCpus ?? 2,
                    MemoryMb = (reported.MemoryBytes ?? 2147483648L) / 1024 / 1024,
                    DiskGb = (reported.DiskBytes ?? 21474836480L) / 1024 / 1024 / 1024,
                    ImageId = "unknown",  // Can't recover this
                },

                // Billing - initialize with defaults
                BillingInfo = new VmBillingInfo
                {
                    HourlyRateCrypto = 0.001m, // Default rate
                    CryptoSymbol = "USDC",
                    LastBillingAt = DateTime.UtcNow
                },

                // Add recovery label
                Labels = new Dictionary<string, string>
                {
                    { "recovered", "true" },
                    { "recovery-time", DateTime.UtcNow.ToString("O") },
                    { "recovery-node", nodeId }
                }
            };

            // Add to datastore
            if (_dataStore.VirtualMachines.TryAdd(vmId, recoveredVm))
            {
                _logger.LogInformation(
                    "Successfully recovered VM {VmId} ({Name}) on node {NodeId} - State: {State}, IP: {Ip}",
                    vmId, recoveredVm.Name, nodeId, recoveredVm.Status, reported.IpAddress ?? "none");

                // Emit recovery event
                await _eventService.EmitAsync(new OrchestratorEvent
                {
                    Type = EventType.VmRecovered,
                    ResourceType = "vm",
                    ResourceId = vmId,
                    UserId = recoveredVm.OwnerId,
                    Payload = new
                    {
                        recoveredVm.Name,
                        NodeId = nodeId,
                        State = recoveredVm.Status.ToString(),
                        IpAddress = reported.IpAddress,
                        VCpus = recoveredVm.Spec.CpuCores,
                        MemoryMb = recoveredVm.Spec.MemoryMb,
                        RecoveryReason = "Found on node but not in orchestrator",
                        Recovered = true  // Flag to indicate this is a recovery
                    }
                });

                // If owner is unknown, try to notify admins
                if (recoveredVm.OwnerId == "unknown")
                {
                    _logger.LogWarning(
                        "Recovered VM {VmId} has unknown owner - manual intervention may be required",
                        vmId);
                }
            }
            else
            {
                _logger.LogWarning("Failed to add recovered VM {VmId} to datastore", vmId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover VM {VmId} from node {NodeId}", vmId, nodeId);
        }
    }

    /// <summary>
    /// Parse VM state string to VmStatus enum
    /// </summary>
    private VmStatus ParseVmStatus(string? state)
    {
        if (string.IsNullOrEmpty(state)) return VmStatus.Error;

        return state.ToLower() switch
        {
            "running" => VmStatus.Running,
            "stopped" => VmStatus.Stopped,
            "paused" => VmStatus.Running,  // Paused VMs are still considered running
            "stopping" => VmStatus.Stopping,
            "starting" => VmStatus.Provisioning,
            "creating" => VmStatus.Provisioning,
            "failed" => VmStatus.Error,
            _ => VmStatus.Error
        };
    }

    /// <summary>
    /// Parse VM state string to VmPowerState enum
    /// </summary>
    private VmPowerState ParsePowerState(string? state)
    {
        if (string.IsNullOrEmpty(state)) return VmPowerState.Off;

        return state.ToLower() switch
        {
            "running" => VmPowerState.Running,
            "paused" => VmPowerState.Paused,
            "stopping" => VmPowerState.ShuttingDown,
            "starting" => VmPowerState.Starting,
            _ => VmPowerState.Off
        };
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