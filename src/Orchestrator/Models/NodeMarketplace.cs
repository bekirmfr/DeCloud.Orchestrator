namespace Orchestrator.Models;

/// <summary>
/// Node advertisement for marketplace discovery
/// Transforms Node model into user-friendly marketplace listing
/// </summary>
public class NodeAdvertisement
{
    public string NodeId { get; set; } = string.Empty;
    public string OperatorName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Region { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    
    // Tags for discovery
    public List<string> Tags { get; set; } = new();
    
    // Hardware capabilities (from HardwareInventory)
    public NodeCapabilities Capabilities { get; set; } = new();
    
    // Trust signals
    public double UptimePercentage { get; set; }
    public int TotalVmsHosted { get; set; }
    public int SuccessfulVmCompletions { get; set; }
    public DateTime RegisteredAt { get; set; }
    
    // Pricing
    public decimal BasePrice { get; set; }
    
    // Availability
    public bool IsOnline { get; set; }
    public int AvailableComputePoints { get; set; }
    public long AvailableMemoryBytes { get; set; }
    public long AvailableStorageBytes { get; set; }
}

/// <summary>
/// Simplified hardware capabilities for marketplace display
/// </summary>
public class NodeCapabilities
{
    public bool HasGpu { get; set; }
    public string? GpuModel { get; set; }
    public int? GpuCount { get; set; }
    public long? GpuMemoryBytes { get; set; }
    public bool HasNvmeStorage { get; set; }
    public bool HighBandwidth { get; set; } // >1Gbps
    public string CpuModel { get; set; } = string.Empty;
    public int CpuCores { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long TotalStorageBytes { get; set; }
}

/// <summary>
/// Search criteria for node marketplace
/// </summary>
public class NodeSearchCriteria
{
    /// <summary>
    /// Filter by tags (e.g., "gpu", "nvme", "high-memory")
    /// Nodes must have ALL specified tags
    /// </summary>
    public List<string>? Tags { get; set; }
    
    /// <summary>
    /// Filter by region (e.g., "us-east", "eu-west")
    /// </summary>
    public string? Region { get; set; }
    
    /// <summary>
    /// Filter by jurisdiction for compliance (e.g., "us", "eu")
    /// </summary>
    public string? Jurisdiction { get; set; }
    
    /// <summary>
    /// Maximum acceptable price (USDC per compute point per hour)
    /// </summary>
    public decimal? MaxPricePerPoint { get; set; }
    
    /// <summary>
    /// Minimum uptime percentage (0-100)
    /// </summary>
    public double? MinUptimePercent { get; set; }
    
    /// <summary>
    /// Require GPU availability
    /// </summary>
    public bool? RequiresGpu { get; set; }
    
    /// <summary>
    /// Minimum available compute points
    /// </summary>
    public int? MinAvailableComputePoints { get; set; }
    
    /// <summary>
    /// Only show online nodes
    /// </summary>
    public bool OnlineOnly { get; set; } = true;
    
    /// <summary>
    /// Sort by (e.g., "price", "uptime", "capacity")
    /// </summary>
    public string? SortBy { get; set; }
    
    /// <summary>
    /// Sort descending
    /// </summary>
    public bool SortDescending { get; set; }
}
