using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

public interface INodeMarketplaceService
{
    /// <summary>
    /// Search nodes based on criteria
    /// </summary>
    Task<List<NodeAdvertisement>> SearchNodesAsync(NodeSearchCriteria criteria);
    
    /// <summary>
    /// Get featured/recommended nodes (editorial picks)
    /// </summary>
    Task<List<NodeAdvertisement>> GetFeaturedNodesAsync();
    
    /// <summary>
    /// Get detailed advertisement for a specific node
    /// </summary>
    Task<NodeAdvertisement?> GetNodeAdvertisementAsync(string nodeId);
    
    /// <summary>
    /// Update node profile (name, description, tags)
    /// </summary>
    Task<bool> UpdateNodeProfileAsync(string nodeId, NodeProfileUpdate update);
}

public class NodeMarketplaceService : INodeMarketplaceService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<NodeMarketplaceService> _logger;

    public NodeMarketplaceService(
        DataStore dataStore,
        ILogger<NodeMarketplaceService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    public async Task<List<NodeAdvertisement>> SearchNodesAsync(NodeSearchCriteria criteria)
    {
        var nodes = (await _dataStore.GetAllNodesAsync()).AsEnumerable();

        // Filter by online status
        if (criteria.OnlineOnly)
        {
            nodes = nodes.Where(n => n.Status == NodeStatus.Online);
        }

        // Filter by tags (node must have ALL specified tags)
        if (criteria.Tags?.Any() == true)
        {
            nodes = nodes.Where(n => 
                criteria.Tags.All(tag => 
                    n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }

        // Filter by region
        if (!string.IsNullOrEmpty(criteria.Region))
        {
            nodes = nodes.Where(n => 
                n.Region.Equals(criteria.Region, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by GPU requirement
        if (criteria.RequiresGpu == true)
        {
            nodes = nodes.Where(n => n.HardwareInventory.SupportsGpu);
        }

        // Filter by uptime
        if (criteria.MinUptimePercent.HasValue)
        {
            nodes = nodes.Where(n => n.UptimePercentage >= criteria.MinUptimePercent.Value);
        }

        // Filter by price
        if (criteria.MaxPricePerPoint.HasValue)
        {
            nodes = nodes.Where(n => n.BasePrice <= criteria.MaxPricePerPoint.Value);
        }

        // Filter by available capacity
        if (criteria.MinAvailableComputePoints.HasValue)
        {
            nodes = nodes.Where(n => 
                (n.TotalResources.ComputePoints - n.ReservedResources.ComputePoints) >= 
                criteria.MinAvailableComputePoints.Value);
        }

        // Convert to advertisements
        var advertisements = nodes.Select(n => ToAdvertisement(n)).ToList();

        // Sort
        advertisements = criteria.SortBy?.ToLower() switch
        {
            "price" => criteria.SortDescending 
                ? advertisements.OrderByDescending(a => a.BasePrice).ToList()
                : advertisements.OrderBy(a => a.BasePrice).ToList(),
            "uptime" => criteria.SortDescending
                ? advertisements.OrderByDescending(a => a.UptimePercentage).ToList()
                : advertisements.OrderBy(a => a.UptimePercentage).ToList(),
            "capacity" => criteria.SortDescending
                ? advertisements.OrderByDescending(a => a.AvailableComputePoints).ToList()
                : advertisements.OrderBy(a => a.AvailableComputePoints).ToList(),
            _ => advertisements.OrderByDescending(a => a.UptimePercentage).ToList() // Default sort by uptime
        };

        return await Task.FromResult(advertisements);
    }

    public async Task<List<NodeAdvertisement>> GetFeaturedNodesAsync()
    {
        // Featured nodes criteria:
        // 1. High uptime (>95%)
        // 2. Good capacity
        // 3. Online
        // 4. Has description (curated)
        
        var featuredNodes = _dataStore.ActiveNodes.Values
            .Where(n => 
                n.Status == NodeStatus.Online &&
                n.UptimePercentage >= 95.0 &&
                !string.IsNullOrEmpty(n.Description) &&
                (n.TotalResources.ComputePoints - n.ReservedResources.ComputePoints) > 10)
            .OrderByDescending(n => n.UptimePercentage)
            .ThenByDescending(n => n.TotalVmsHosted)
            .Take(10)
            .Select(n => ToAdvertisement(n))
            .ToList();

        return await Task.FromResult(featuredNodes);
    }

    public async Task<NodeAdvertisement?> GetNodeAdvertisementAsync(string nodeId)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return null;

        return ToAdvertisement(node);
    }

    public async Task<bool> UpdateNodeProfileAsync(string nodeId, NodeProfileUpdate update)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
        {
            _logger.LogWarning("Cannot update profile for non-existent node {NodeId}", nodeId);
            return false;
        }

        // Update fields if provided
        if (!string.IsNullOrEmpty(update.Name))
        {
            node.Name = update.Name;
        }

        if (update.Description != null)
        {
            node.Description = update.Description;
        }

        if (update.Tags != null)
        {
            node.Tags = update.Tags;
        }

        if (update.BasePrice.HasValue && update.BasePrice.Value > 0)
        {
            node.BasePrice = update.BasePrice.Value;
        }

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Updated profile for node {NodeId}: Name={Name}, Tags={Tags}",
            nodeId, node.Name, string.Join(", ", node.Tags));

        return true;
    }

    /// <summary>
    /// Convert Node to NodeAdvertisement (DTO for marketplace)
    /// </summary>
    private NodeAdvertisement ToAdvertisement(Node node)
    {
        return new NodeAdvertisement
        {
            NodeId = node.Id,
            OperatorName = node.Name,
            Description = node.Description,
            Region = node.Region,
            Zone = node.Zone,
            Tags = node.Tags,
            
            Capabilities = new NodeCapabilities
            {
                HasGpu = node.HardwareInventory.SupportsGpu,
                GpuModel = node.HardwareInventory.Gpus.FirstOrDefault()?.Model,
                GpuCount = node.HardwareInventory.Gpus.Count > 0 ? node.HardwareInventory.Gpus.Count : null,
                GpuMemoryBytes = node.HardwareInventory.Gpus.FirstOrDefault()?.MemoryBytes,
                HasNvmeStorage = node.HardwareInventory.Storage
                    .Any(s => s.Type == StorageType.NVMe),
                HighBandwidth = (node.HardwareInventory.Network.BandwidthBitsPerSecond ?? 0) 
                    > 1_000_000_000, // > 1 Gbps
                CpuModel = node.HardwareInventory.Cpu.Model,
                CpuCores = node.HardwareInventory.Cpu.PhysicalCores,
                TotalMemoryBytes = node.HardwareInventory.Memory.TotalBytes,
                TotalStorageBytes = node.HardwareInventory.Storage.Sum(s => s.TotalBytes)
            },
            
            UptimePercentage = node.UptimePercentage,
            TotalVmsHosted = node.TotalVmsHosted,
            SuccessfulVmCompletions = node.SuccessfulVmCompletions,
            RegisteredAt = node.RegisteredAt,
            
            BasePrice = node.BasePrice,
            
            IsOnline = node.Status == NodeStatus.Online,
            AvailableComputePoints = node.TotalResources.ComputePoints - node.ReservedResources.ComputePoints,
            AvailableMemoryBytes = node.TotalResources.MemoryBytes - node.ReservedResources.MemoryBytes,
            AvailableStorageBytes = node.TotalResources.StorageBytes - node.ReservedResources.StorageBytes
        };
    }
}

/// <summary>
/// Request to update node profile
/// </summary>
public class NodeProfileUpdate
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
    public decimal? BasePrice { get; set; }
}
