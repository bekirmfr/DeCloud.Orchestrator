using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Orchestrator.Models;
using Orchestrator.Models.Payment;

namespace Orchestrator.Services.Payment;

/// <summary>
/// Single source of truth for the per-VM hourly cost formula.
///
/// Two consumers:
///   - <see cref="VmService"/> at scheduling time: stamps the result's
///     <c>Total</c> onto <see cref="VmBillingInfo.HourlyRateCrypto"/>,
///     which drives recurring billing in <see cref="BillingService"/>.
///   - <see cref="Controllers.SystemController.CalculatePrice"/>: public
///     estimator for tenants previewing a configuration.
///
/// Passing <c>nodePricing = null</c> yields the platform-default version —
/// the rates a tenant would see on a node with no operator overrides.
/// Real billing passes the host node's <see cref="NodePricing"/>.
///
/// Cost components (returned as separate fields in
/// <see cref="HourlyRateBreakdown"/> so the public estimator can surface
/// each line item; billing reads only <c>Total</c>):
///
///   - Compute (operator-priced, quality-tier multiplied):
///       (vCPUs × cpuRate + memGb × memRate + diskGb × storageRate) × tierMultiplier
///   - Bandwidth (platform-priced, flat per hour by tier).
///   - GPU (operator-priced, per-GB-VRAM × allocated VRAM).
///   - Replication (platform-priced, block-count × blockSizeMB × replicationFactor × rate).
///     Zero when <c>ReplicationFactor == 0</c>. When <c>manifestBlockCount</c> is
///     null, falls back to a 5%-of-disk estimate so billing has a non-zero
///     replication line before the first lazysync cycle completes.
///
/// Rate resolution (operator → default → floor) is delegated to
/// <see cref="PricingResolver"/>: one rule, one place.
/// </summary>
public static class HourlyRateCalculator
{
    private const decimal BYTES_PER_GB = 1024m * 1024m * 1024m;

    /// <summary>
    /// Flat hourly rate for orchestrator-owned system VMs (relay, dht,
    /// blockstore). System VMs don't use operator pricing, quality tier
    /// multipliers, bandwidth tiers, GPU, or replication accounting —
    /// they are platform infrastructure billed at a fixed rate.
    /// </summary>
    public const decimal SystemVmFlatRate = 0.005m;

    /// <summary>
    /// Compute the per-hour cost breakdown for a VM with the given spec on
    /// a node with the given pricing. Pass <paramref name="nodePricing"/>=null
    /// to compute the platform-default rate (used by the public estimator).
    ///
    /// <para>
    /// <paramref name="manifestBlockCount"/> is the current lazysync-confirmed
    /// block count for the VM's overlay. Null when no manifest exists yet
    /// (newly-scheduled VM, or estimator call); falls back to a 5%-of-disk
    /// estimate. Always charges at least 1 block when ReplicationFactor &gt; 0.
    /// </para>
    /// </summary>
    public static HourlyRateBreakdown Calculate(
        VmSpec spec,
        NodePricing? nodePricing,
        PricingConfig cfg,
        int? manifestBlockCount = null,
        int manifestBlockSizeKb = BlockSizeConstants.VmOverlayKb)
    {
        // Resolve effective rates: operator's pricing where set, platform
        // defaults where not, every field clamped to floor. PricingResolver
        // is the single source of truth for this rule across the orchestrator
        // — also used by /api/nodes/me/pricing and the marketplace API.
        var rates = PricingResolver.Resolve(nodePricing, cfg);

        // Quality tier multiplier scales compute resources only. Bandwidth,
        // GPU, and replication are not tier-multiplied — they reflect
        // physical costs that don't change with reliability tier.
        var tierMultiplier = spec.QualityTier switch
        {
            QualityTier.Guaranteed => 2.5m,
            QualityTier.Standard => 1.0m,
            QualityTier.Balanced => 0.6m,
            QualityTier.Burstable => 0.4m,
            _ => 1.0m
        };

        // Bandwidth tier surcharge (platform-set, flat per hour).
        var bandwidthCost = spec.BandwidthTier switch
        {
            BandwidthTier.Basic => cfg.BandwidthBasicPerHour,
            BandwidthTier.Standard => cfg.BandwidthStandardPerHour,
            BandwidthTier.Performance => cfg.BandwidthPerformancePerHour,
            _ => cfg.BandwidthUnmeteredPerHour
        };

        var memoryGb = spec.MemoryBytes / BYTES_PER_GB;
        var diskGb = spec.DiskBytes / BYTES_PER_GB;
        var gpuVramGb = (spec.GpuVramBytes ?? 0) / BYTES_PER_GB;

        // Per-component compute costs, post-tier-multiplier (these are the
        // values that actually contribute to the bill). Mathematically
        // identical to (sum × multiplier) but split for breakdown reporting.
        var cpuCost = spec.VirtualCpuCores * rates.CpuPerHour * tierMultiplier;
        var memoryCost = memoryGb * rates.MemoryPerGbPerHour * tierMultiplier;
        var storageCost = diskGb * rates.StoragePerGbPerHour * tierMultiplier;

        // GPU: per-GB VRAM × allocated VRAM. Zero when no GPU is requested
        // or when GpuVramBytes is unset/zero (estimator with no GPU spec).
        var gpuCost = gpuVramGb * rates.GpuVramPerGbPerHour;

        // Replication (platform-priced, block-based).
        var replicationCost = CalculateReplicationCost(
            spec, cfg, manifestBlockCount, manifestBlockSizeKb);

        var total = cpuCost + memoryCost + storageCost
                  + bandwidthCost + gpuCost + replicationCost;

        return new HourlyRateBreakdown(
            CpuCost: cpuCost,
            MemoryCost: memoryCost,
            StorageCost: storageCost,
            TierMultiplier: tierMultiplier,
            BandwidthCost: bandwidthCost,
            GpuCost: gpuCost,
            ReplicationCost: replicationCost,
            Total: total,
            Currency: rates.Currency);
    }

    /// <summary>
    /// Storage replication cost: chargeable when the VM has a non-zero
    /// replication factor. Falls back to a 5%-of-disk block estimate when
    /// no manifest exists yet (first lazysync cycle hasn't completed). At
    /// least 1 block is charged so a newly-scheduled non-ephemeral VM
    /// isn't free for its first audit interval.
    /// </summary>
    private static decimal CalculateReplicationCost(
        VmSpec spec, PricingConfig cfg,
        int? manifestBlockCount, int manifestBlockSizeKb)
    {
        if (spec.ReplicationFactor == 0)
            return 0m;

        var effectiveBlockSizeKb = manifestBlockSizeKb > 0
            ? manifestBlockSizeKb
            : BlockSizeConstants.VmOverlayKb;

        var blockCount = manifestBlockCount ?? (int)(
            (spec.DiskBytes * 0.05m) / (effectiveBlockSizeKb * 1024m));
        blockCount = Math.Max(blockCount, 1); // at least 1 block

        // cost_per_mb = StoragePerMbPerHour (platform floor, not operator rate)
        var costPerMb = Math.Max(cfg.DefaultReplicationPerMbPerHour, cfg.FloorReplicationPerMbPerHour);

        // blockSizeMb = blockSizeKb / 1024 (e.g., 1.0 for VmOverlay, 64.0 for ModelShard)
        var blockSizeMb = (decimal)effectiveBlockSizeKb / 1024m;

        return blockCount * blockSizeMb * spec.ReplicationFactor * costPerMb;
    }
}

/// <summary>
/// Detailed breakdown of a VM's hourly cost.
///
/// <para>
/// The sum of <see cref="CpuCost"/>, <see cref="MemoryCost"/>,
/// <see cref="StorageCost"/>, <see cref="BandwidthCost"/>, <see cref="GpuCost"/>,
/// and <see cref="ReplicationCost"/> equals <see cref="Total"/>.
/// </para>
///
/// <para>
/// <see cref="TierMultiplier"/> is informational — it is already applied
/// to the compute components (<see cref="CpuCost"/>, <see cref="MemoryCost"/>,
/// <see cref="StorageCost"/>). Surfaced separately so the public estimator
/// can show users why their cost scales with quality tier.
/// </para>
/// </summary>
public record HourlyRateBreakdown(
    decimal CpuCost,
    decimal MemoryCost,
    decimal StorageCost,
    decimal TierMultiplier,
    decimal BandwidthCost,
    decimal GpuCost,
    decimal ReplicationCost,
    decimal Total,
    string Currency);