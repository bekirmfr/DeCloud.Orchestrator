namespace Orchestrator.Models.Payment;

/// <summary>
/// Platform pricing configuration loaded from appsettings.json.
/// Defines floor rates (minimums) and default rates for new nodes.
/// Node operators set their own rates above the floor.
/// </summary>
public class PricingConfig
{
    // Floor rates - nodes cannot charge below these
    public decimal FloorCpuPerHour { get; set; } = 0.005m;
    public decimal FloorMemoryPerGbPerHour { get; set; } = 0.0025m;
    public decimal FloorStoragePerGbPerHour { get; set; } = 0.00005m;
    public decimal FloorGpuPerHour { get; set; } = 0.05m;

    // Default rates - applied to nodes that haven't set custom pricing
    public decimal DefaultCpuPerHour { get; set; } = 0.01m;
    public decimal DefaultMemoryPerGbPerHour { get; set; } = 0.005m;
    public decimal DefaultStoragePerGbPerHour { get; set; } = 0.0001m;
    public decimal DefaultGpuPerHour { get; set; } = 0.10m;
}

/// <summary>
/// Per-node pricing set by the node operator.
/// Rates must be >= platform floor rates.
/// If null/zero, platform defaults are used.
/// </summary>
public class NodePricing
{
    public decimal CpuPerHour { get; set; }
    public decimal MemoryPerGbPerHour { get; set; }
    public decimal StoragePerGbPerHour { get; set; }
    public decimal GpuPerHour { get; set; }
    public string Currency { get; set; } = "USDC";

    /// <summary>
    /// Returns true if the operator has set any custom pricing
    /// </summary>
    public bool HasCustomPricing =>
        CpuPerHour > 0 || MemoryPerGbPerHour > 0 ||
        StoragePerGbPerHour > 0 || GpuPerHour > 0;
}
