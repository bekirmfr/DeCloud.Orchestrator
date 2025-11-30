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
    Task<List<Node>> GetNodesAsync(NodeStatus? statusFilter = null);
    Task<List<Node>> GetAvailableNodesForVmAsync(VmSpec spec);
    Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status);
    Task<bool> RemoveNodeAsync(string nodeId);
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
            _logger.LogInformation("Node re-registering: {NodeId} ({Name})",
                existingNode.Id, existingNode.Name);

            existingNode.Name = request.Name;
            existingNode.PublicIp = request.PublicIp;
            existingNode.AgentPort = request.AgentPort;
            existingNode.TotalResources = request.Resources;
            existingNode.AvailableResources = request.Resources;
            existingNode.AgentVersion = request.AgentVersion;
            existingNode.SupportedImages = request.SupportedImages;
            existingNode.SupportsGpu = request.SupportsGpu;
            existingNode.GpuInfo = request.GpuInfo;
            existingNode.Region = request.Region ?? "default";
            existingNode.Zone = request.Zone ?? "default";
            existingNode.Status = NodeStatus.Online;
            existingNode.LastHeartbeat = DateTime.UtcNow;

            await _dataStore.SaveNodeAsync(existingNode);

            if (!_dataStore.NodeAuthTokens.TryGetValue(existingNode.Id, out var existingTokenHash))
            {
                var newToken = GenerateAuthToken();
                var newTokenHash = HashToken(newToken);
                _dataStore.NodeAuthTokens[existingNode.Id] = newTokenHash;

                return new NodeRegistrationResponse(
                    existingNode.Id,
                    newToken,
                    TimeSpan.FromSeconds(15));
            }

            var regeneratedToken = GenerateAuthToken();
            var regeneratedHash = HashToken(regeneratedToken);
            _dataStore.NodeAuthTokens[existingNode.Id] = regeneratedHash;

            return new NodeRegistrationResponse(
                existingNode.Id,
                regeneratedToken,
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

        await SyncVmStateFromHeartbeatAsync(nodeId, heartbeat);

        var commands = _dataStore.GetAndClearPendingCommands(nodeId);

        return new NodeHeartbeatResponse(true, commands.Count > 0 ? commands : null);
    }

    private async Task SyncVmStateFromHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        try
        {
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

        if (vm.Status == VmStatus.Provisioning && reportedState == VmStatus.Running)
        {
            vm.Status = VmStatus.Running;
            vm.PowerState = VmPowerState.Running;
            vm.StartedAt ??= reported.StartedAt ?? DateTime.UtcNow;
            stateChanged = true;
        }

        if (string.IsNullOrEmpty(vm.NetworkConfig.PrivateIp) && !string.IsNullOrEmpty(reported.IpAddress))
        {
            vm.NetworkConfig.PrivateIp = reported.IpAddress;
            stateChanged = true;
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
        if (vm.Status == VmStatus.Running || vm.Status == VmStatus.Provisioning)
        {
            _logger.LogWarning(
                "VM {VmId} expected on node {NodeId} but not reported - marking as error",
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
            if (!Guid.TryParse(vmId, out _))
            {
                _logger.LogWarning("Rejecting VM recovery for {VmId} - invalid ID format", vmId);
                return;
            }

            if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            {
                _logger.LogError("Cannot recover VM {VmId} - node {NodeId} not found", vmId, nodeId);
                return;
            }

            var recoveredVm = new VirtualMachine
            {
                Id = vmId,
                Name = reported.Name ?? $"recovered-vm-{vmId[..8]}",
                NodeId = nodeId,
                OwnerId = reported.TenantId ?? "unknown",
                OwnerWallet = "unknown",
                Status = ParseVmStatus(reported.State),
                PowerState = ParsePowerState(reported.State),
                StatusMessage = "Recovered from node agent after orchestrator restart",
                CreatedAt = reported.StartedAt ?? DateTime.UtcNow.AddHours(-1),
                StartedAt = reported.StartedAt,
                UpdatedAt = DateTime.UtcNow,
                NetworkConfig = new VmNetworkConfig
                {
                    PrivateIp = reported.IpAddress,
                    Hostname = reported.Name
                },
                Spec = new VmSpec
                {
                    CpuCores = reported.VCpus ?? 2,
                    MemoryMb = (reported.MemoryBytes ?? 2147483648L) / 1024 / 1024,
                    DiskGb = (reported.DiskBytes ?? 21474836480L) / 1024 / 1024 / 1024,
                    ImageId = "unknown",
                },
                BillingInfo = new VmBillingInfo
                {
                    HourlyRateCrypto = 0.001m,
                    CryptoSymbol = "USDC",
                    LastBillingAt = DateTime.UtcNow
                },
                Labels = new Dictionary<string, string>
                {
                    { "recovered", "true" },
                    { "recovery-time", DateTime.UtcNow.ToString("O") },
                    { "recovery-node", nodeId }
                }
            };

            await _dataStore.SaveVmAsync(recoveredVm);

            _logger.LogInformation(
                "Successfully recovered VM {VmId} ({Name}) on node {NodeId}",
                vmId, recoveredVm.Name, nodeId);

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
                    Recovered = true
                }
            });

            if (recoveredVm.OwnerId == "unknown")
            {
                _logger.LogWarning(
                    "Recovered VM {VmId} has unknown owner - manual intervention may be required",
                    vmId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover VM {VmId} from node {NodeId}", vmId, nodeId);
        }
    }

    private VmStatus ParseVmStatus(string? state) => state?.ToLower() switch
    {
        "running" => VmStatus.Running,
        "stopped" => VmStatus.Stopped,
        "paused" => VmStatus.Running,
        "failed" => VmStatus.Error,
        _ => VmStatus.Error
    };

    private VmPowerState ParsePowerState(string? state) => state?.ToLower() switch
    {
        "running" => VmPowerState.Running,
        "paused" => VmPowerState.Paused,
        _ => VmPowerState.Off
    };

    public Task<Node?> GetNodeAsync(string nodeId)
    {
        _dataStore.Nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(node);
    }

    public Task<List<Node>> GetNodesAsync(NodeStatus? statusFilter = null)
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
}