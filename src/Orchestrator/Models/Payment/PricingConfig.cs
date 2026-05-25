namespace Orchestrator.Models.Payment;

/// <summary>
/// Platform pricing configuration loaded from appsettings.json.
/// Defines floor rates (minimums) and default rates for new nodes.
/// Node operators set their own rates above the floor.
///
/// Two storage rate dimensions:
///   StoragePerGbPerHour   — charged per GB of disk allocation in the compute component
///   StoragePerMbPerHour   — charged per 1 MB block per replica in the replication component
///                           Formula: blockCount × (blockSizeKb / 1024) × replicationFactor
///                                    × StoragePerMbPerHour
/// </summary>
public class PricingConfig
{
    // ── Compute resource floor rates ─────────────────────────────────────────
    // Nodes cannot charge below these.
    public decimal FloorCpuPerHour { get; set; } = 0.005m;
    public decimal FloorMemoryPerGbPerHour { get; set; } = 0.0025m;
    public decimal FloorStoragePerGbPerHour { get; set; } = 0.00005m;

    // ── Compute resource default rates ───────────────────────────────────────
    // Applied to nodes that haven't set custom pricing.
    public decimal DefaultCpuPerHour { get; set; } = 0.01m;
    public decimal DefaultMemoryPerGbPerHour { get; set; } = 0.005m;
    public decimal DefaultStoragePerGbPerHour { get; set; } = 0.0001m;

    // ── GPU VRAM rates ────────────────────────────────────────────────────────
    // Applied per GB of GPU VRAM for both Passthrough and Proxied modes.
    // Passthrough: node's total GPU VRAM × rate (stamped by scheduler).
    // Proxied: VRAM quota reserved at scheduling time × rate.
    public decimal FloorGpuVramPerGbPerHour { get; set; } = 0.003m;
    public decimal DefaultGpuVramPerGbPerHour { get; set; } = 0.006m;

    // ── Storage replication rates (block-based) ───────────────────────────────
    // Platform-set, not operator-overridable. Replication is a network duty cost,
    // not a per-node operator service.
    //
    // Rate is per 1 MB per replica per hour. Larger block sizes scale cost
    // proportionally: a 64 MB model shard block costs 64× a 1 MB VM overlay block.
    //
    // Default: $0.000001 per 1 MB block per hour per replica.
    // A VM with 4,000 × 1 MB blocks at N=3 costs: 4000 × 3 × $0.000001 = $0.012/hr.
    public decimal FloorStoragePerMbPerHour { get; set; } = 0.0000005m;
    public decimal DefaultStoragePerMbPerHour { get; set; } = 0.000001m;

    // ── Bandwidth tier rates (platform-set, not operator-overridable) ─────────
    public decimal BandwidthBasicPerHour { get; set; } = 0.002m;       // 10 Mbps
    public decimal BandwidthStandardPerHour { get; set; } = 0.008m;    // 50 Mbps
    public decimal BandwidthPerformancePerHour { get; set; } = 0.020m; // 200 Mbps
    public decimal BandwidthUnmeteredPerHour { get; set; } = 0.040m;   // Unmetered
}