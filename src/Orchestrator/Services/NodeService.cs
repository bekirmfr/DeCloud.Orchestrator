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
            existingNode.AgentVersion = request.AgentVersion;
            existingNode.SupportedImages = request.SupportedImages;
            existingNode.SupportsGpu = request.SupportsGpu;
            existingNode.GpuInfo = request.GpuInfo;
            existingNode.Region = request.Region;
            existingNode.Zone = request.Zone;
            existingNode.Status = NodeStatus.Online;
            existingNode.LastHeartbeat = DateTime.UtcNow;

            _logger.LogInformation("Node re-registered: {NodeId} ({Name})", nodeId, request.Name);
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
                TotalResources = request.Resources,
                AvailableResources = new NodeResources
                {
                    CpuCores = request.Resources.CpuCores,
                    MemoryMb = request.Resources.MemoryMb,
                    StorageGb = request.Resources.StorageGb,
                    BandwidthMbps = request.Resources.BandwidthMbps
                },
                AgentVersion = request.AgentVersion,
                SupportedImages = request.SupportedImages,
                SupportsGpu = request.SupportsGpu,
                GpuInfo = request.GpuInfo,
                Region = request.Region,
                Zone = request.Zone,
                Status = NodeStatus.Online,
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

        // Get any pending commands for this node
        var commands = _dataStore.GetAndClearPendingCommands(nodeId);

        return new NodeHeartbeatResponse(true, commands.Count > 0 ? commands : null);
    }

    public Task<Node?> GetNodeAsync(string nodeId)
    {
        _dataStore.Nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(node);
    }

    public Task<List<Node>> GetAllNodesAsync(NodeStatus? statusFilter = null)
    {
        var nodes = _dataStore.Nodes.Values.AsEnumerable();
        
        if (statusFilter.HasValue)
        {
            nodes = nodes.Where(n => n.Status == statusFilter.Value);
        }

        return Task.FromResult(nodes.OrderBy(n => n.Name).ToList());
    }

    public Task<List<Node>> GetAvailableNodesForVmAsync(VmSpec spec)
    {
        var eligibleNodes = _dataStore.Nodes.Values
            .Where(n => n.Status == NodeStatus.Online)
            .Where(n => n.AvailableResources.CpuCores >= spec.CpuCores)
            .Where(n => n.AvailableResources.MemoryMb >= spec.MemoryMb)
            .Where(n => n.AvailableResources.StorageGb >= spec.DiskGb)
            .Where(n => n.SupportedImages.Contains(spec.ImageId) || !string.IsNullOrEmpty(spec.ImageUrl))
            .Where(n => !spec.RequiresGpu || n.SupportsGpu);

        // Apply region/zone preferences
        if (!string.IsNullOrEmpty(spec.PreferredRegion))
        {
            var regionNodes = eligibleNodes.Where(n => n.Region == spec.PreferredRegion).ToList();
            if (regionNodes.Any())
                eligibleNodes = regionNodes.AsEnumerable();
        }

        if (!string.IsNullOrEmpty(spec.PreferredZone))
        {
            var zoneNodes = eligibleNodes.Where(n => n.Zone == spec.PreferredZone).ToList();
            if (zoneNodes.Any())
                eligibleNodes = zoneNodes.AsEnumerable();
        }

        // Prefer specific node if requested
        if (!string.IsNullOrEmpty(spec.PreferredNodeId))
        {
            var preferred = eligibleNodes.FirstOrDefault(n => n.Id == spec.PreferredNodeId);
            if (preferred != null)
                return Task.FromResult(new List<Node> { preferred });
        }

        // Sort by available resources (best fit)
        var sorted = eligibleNodes
            .OrderByDescending(n => n.UptimePercentage)
            .ThenBy(n => n.AvailableResources.CpuCores - spec.CpuCores)
            .ThenBy(n => n.AvailableResources.MemoryMb - spec.MemoryMb)
            .ToList();

        return Task.FromResult(sorted);
    }

    public async Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status)
    {
        if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            return false;

        var oldStatus = node.Status;
        node.Status = status;

        _logger.LogInformation("Node {NodeId} status changed: {OldStatus} -> {NewStatus}", 
            nodeId, oldStatus, status);

        if (status == NodeStatus.Offline)
        {
            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.NodeOffline,
                ResourceType = "node",
                ResourceId = nodeId
            });
        }

        return true;
    }

    public Task<bool> RemoveNodeAsync(string nodeId)
    {
        var removed = _dataStore.Nodes.TryRemove(nodeId, out _);
        _dataStore.NodeAuthTokens.TryRemove(nodeId, out _);
        _dataStore.PendingNodeCommands.TryRemove(nodeId, out _);
        
        return Task.FromResult(removed);
    }

    /// <summary>
    /// Check for nodes that haven't sent heartbeats and mark them offline
    /// </summary>
    public async Task CheckNodeHealthAsync()
    {
        var now = DateTime.UtcNow;
        
        foreach (var node in _dataStore.Nodes.Values)
        {
            if (node.Status == NodeStatus.Online && 
                now - node.LastHeartbeat > _heartbeatTimeout)
            {
                _logger.LogWarning("Node {NodeId} missed heartbeat, marking offline", node.Id);
                await UpdateNodeStatusAsync(node.Id, NodeStatus.Offline);
            }
        }
    }

    private static string GenerateAuthToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    private static string HashToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
