using Orchestrator.Models.Payment;

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
    /// <summary>
    /// ISO 3166-1 alpha-2 country code declared by the operator.
    /// <c>"ZZ"</c> = unknown / pre-locality-standard node.
    /// </summary>
    public string Country { get; set; } = "ZZ";

    /// <summary>
    /// Supranational membership tags derived from <see cref="Country"/>
    /// at registration, e.g. <c>["EU", "EEA", "Schengen", "NATO"]</c>.
    /// Empty for nodes with unknown country or countries with no tracked blocs.
    /// </summary>
    public List<string> JurisdictionTags { get; set; } = new();

    /// <summary>
    /// True when the node's declared country differs from the country
    /// inferred from its registration IP. Surfaced so tenants paying a
    /// premium for jurisdiction can see when network location and declared
    /// location disagree (VPN, CGNAT, leased datacenter in another country).
    /// </summary>
    public bool LocationMismatch { get; set; }


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
    public NodePricing Pricing { get; set; } = new();

    // Availability
    public bool IsOnline { get; set; }

    /// <summary>
    /// True when the operator has logged in and the node accepts new VM placements.
    /// False when the operator has logged out (online but paused).
    /// </summary>
    public bool SchedulingReady { get; set; }
    public long AllocatedComputePoints { get; set; } = 0;
    public long AvailableComputePoints { get; set; } = 0;
    public long AllocatedMemoryBytes { get; set; } = 0;
    public long AvailableMemoryBytes { get; set; } = 0;
    public long AllocatedStorageBytes { get; set; } = 0;
    public long AvailableStorageBytes { get; set; } = 0;
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
