using DeCloud.Shared.Models;
using Microsoft.Extensions.Options;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;
using Orchestrator.Services.Payment;

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
    private readonly PricingConfig _pricingConfig;
    private readonly ILogger<NodeMarketplaceService> _logger;

    public NodeMarketplaceService(
        DataStore dataStore,
        IOptions<PricingConfig> pricingConfig,
        ILogger<NodeMarketplaceService> logger)
    {
        _dataStore = dataStore;
        _pricingConfig = pricingConfig.Value;
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
                n.Locality.Region.Equals(criteria.Region, StringComparison.OrdinalIgnoreCase));
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
                (n.AllocatedResources.ComputePoints - n.UsedResources.ComputePoints - n.ReservedResources.ComputePoints) >=
                criteria.MinAvailableComputePoints.Value);
        }

        // Filter by available GPU VRAM (Proxied workloads)
        if (criteria.MinAvailableGpuVramBytes.HasValue)
        {
            nodes = nodes.Where(n =>
            {
                var totalProxiedVram = n.HardwareInventory.Gpus
                    .Where(g => g.IsAvailableForProxiedSharing)
                    .Sum(g => g.MemoryBytes);
                var availableVram = totalProxiedVram
                    - n.UsedResources.GpuVramBytes
                    - n.ReservedResources.GpuVramBytes;
                return availableVram >= criteria.MinAvailableGpuVramBytes.Value;
            });
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
                (n.TotalResources.ComputePoints - n.ReservedResources.ComputePoints - n.UsedResources.ComputePoints) > 10 &&
                (n.SystemVmObligations.Count == 0 ||
                 n.SystemVmObligations.All(o => o.Status == SystemVmStatus.Active)))
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

        if (update.Pricing != null)
        {
            // Enforce floor rates
            var p = update.Pricing;
            node.Pricing = new NodePricing
            {
                CpuPerHour = p.CpuPerHour > 0 ? Math.Max(p.CpuPerHour, _pricingConfig.FloorCpuPerHour) : 0,
                MemoryPerGbPerHour = p.MemoryPerGbPerHour > 0 ? Math.Max(p.MemoryPerGbPerHour, _pricingConfig.FloorMemoryPerGbPerHour) : 0,
                StoragePerGbPerHour = p.StoragePerGbPerHour > 0 ? Math.Max(p.StoragePerGbPerHour, _pricingConfig.FloorStoragePerGbPerHour) : 0,
                GpuVramPerGbPerHour = p.GpuVramPerGbPerHour > 0 ? Math.Max(p.GpuVramPerGbPerHour, _pricingConfig.FloorGpuVramPerGbPerHour) : 0,
                Currency = p.Currency ?? "USDC"
            };
        }

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Updated profile for node {NodeId}: Name={Name}, Tags={Tags}, HasCustomPricing={HasPricing}",
            nodeId, node.Name, string.Join(", ", node.Tags), node.Pricing.HasCustomPricing);

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
            Region = node.Locality.Region,
            Zone = node.Locality.Zone ?? "default",
            Country = node.Locality.Country,
            JurisdictionTags = node.Locality.JurisdictionTags,
            LocationMismatch = node.Locality.LocationMismatch,
            Tags = node.Tags,

            Capabilities = new NodeCapabilities
            {
                HasGpu = node.HardwareInventory.SupportsGpu,
                GpuModel = node.HardwareInventory.Gpus.FirstOrDefault()?.Model,
                GpuCount = node.HardwareInventory.Gpus.Count > 0 ? node.HardwareInventory.Gpus.Count : null,
                GpuMemoryBytes = node.HardwareInventory.Gpus.FirstOrDefault()?.MemoryBytes,
                SupportsProxiedGpu = node.HardwareInventory.HasProxiedCapableGpu,
                TotalGpuVramBytes = node.HardwareInventory.Gpus.Sum(g => g.MemoryBytes),
                AvailableGpuVramBytes = Math.Max(0,
                    node.HardwareInventory.Gpus
                        .Where(g => g.IsAvailableForProxiedSharing)
                        .Sum(g => g.MemoryBytes)
                    - node.UsedResources.GpuVramBytes
                    - node.ReservedResources.GpuVramBytes),
                HasNvmeStorage = node.HardwareInventory.Storage.Any(s => s.Type == StorageType.NVMe),
                HighBandwidth = (node.HardwareInventory.Network.BandwidthBitsPerSecond ?? 0) > 1_000_000_000,
                CpuModel = node.HardwareInventory.Cpu.Model,
                CpuCores = node.HardwareInventory.Cpu.PhysicalCores,
            },

            UptimePercentage = node.UptimePercentage,
            TotalVmsHosted = node.TotalVmsHosted,
            SuccessfulVmCompletions = node.SuccessfulVmCompletions,
            RegisteredAt = node.RegisteredAt,

            BasePrice = node.BasePrice,
            Pricing = PricingResolver.Resolve(node.Pricing, _pricingConfig),

            IsOnline = node.Status == NodeStatus.Online,
            SchedulingReady = node.SchedulingReady,
            AllocatedComputePoints = node.AllocatedResources.ComputePoints,
            AvailableComputePoints = Math.Max(0, node.AllocatedResources.ComputePoints - node.UsedResources.ComputePoints - node.ReservedResources.ComputePoints),
            AllocatedMemoryBytes = node.AllocatedResources.MemoryBytes,
            AvailableMemoryBytes = Math.Max(0, node.AllocatedResources.MemoryBytes - node.UsedResources.MemoryBytes - node.ReservedResources.MemoryBytes),
            AllocatedStorageBytes = node.AllocatedResources.StorageBytes,
            AvailableStorageBytes = Math.Max(0, node.AllocatedResources.StorageBytes - node.UsedResources.StorageBytes - node.ReservedResources.StorageBytes)
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
    public NodePricing? Pricing { get; set; }
}
