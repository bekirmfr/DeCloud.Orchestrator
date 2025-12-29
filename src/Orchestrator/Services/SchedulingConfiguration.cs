using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Configuration for VM scheduling and resource overcommit policies
/// </summary>
public class SchedulingConfiguration
{
    /// <summary>
    /// Quality tier definitions with point-based CPU allocation
    /// Each physical core = 8 compute points
    /// </summary>
    public Dictionary<QualityTier, TierPolicy> TierPolicies { get; set; } = new()
    {
        [QualityTier.Guaranteed] = new TierPolicy
        {
            PointsPerVCpu = 8,              // 1:1 - Full core per vCPU
            CpuOvercommitRatio = 1.0,       // No overcommit
            MemoryOvercommitRatio = 1.0,    // No overcommit
            StorageOvercommitRatio = 1.0,   // No overcommit
            PriceMultiplier = 2.5m,         // Premium pricing
            Description = "Dedicated resources, guaranteed 1:1 CPU performance"
        },
        [QualityTier.Standard] = new TierPolicy
        {
            PointsPerVCpu = 4,              // 2:1 - Half core per vCPU
            CpuOvercommitRatio = 2.0,       // 2:1 overcommit
            MemoryOvercommitRatio = 1.2,    // Slight memory overcommit
            StorageOvercommitRatio = 1.5,   // Storage overcommit
            PriceMultiplier = 1.0m,          // Base price
            Description = "Balanced performance and cost, 2:1 CPU overcommit"
        },
        [QualityTier.Balanced] = new TierPolicy
        {
            PointsPerVCpu = 2,              // 4:1 - Quarter core per vCPU
            CpuOvercommitRatio = 4.0,       // 4:1 overcommit
            MemoryOvercommitRatio = 1.5,    // Moderate memory overcommit
            StorageOvercommitRatio = 2.0,   // Storage overcommit
            PriceMultiplier = 0.6m,          // Reduced pricing
            Description = "Cost-optimized for consistent workloads, 4:1 CPU overcommit"
        },
        [QualityTier.Burstable] = new TierPolicy
        {
            PointsPerVCpu = 1,              // 8:1 - Eighth core per vCPU
            CpuOvercommitRatio = 8.0,       // 8:1 aggressive overcommit
            MemoryOvercommitRatio = 2.0,    // Significant memory overcommit
            StorageOvercommitRatio = 2.5,   // High storage overcommit
            PriceMultiplier = 0.3m,          // Lowest pricing
            Description = "Best-effort, lowest cost, 8:1 CPU overcommit"
        }
    };

    /// <summary>
    /// Safety buffer - Don't schedule if node would exceed this utilization
    /// </summary>
    public double MaxUtilizationPercent { get; set; } = 90.0;

    /// <summary>
    /// Minimum free memory to keep on node (in MB)
    /// </summary>
    public long MinFreeMemoryMb { get; set; } = 512;

    /// <summary>
    /// Maximum CPU load average before avoiding node
    /// </summary>
    public double MaxLoadAverage { get; set; } = 8.0;

    /// <summary>
    /// Prefer nodes in same region
    /// </summary>
    public bool PreferLocalRegion { get; set; } = true;

    /// <summary>
    /// Scoring weights for node selection
    /// </summary>
    public ScoringWeights Weights { get; set; } = new();
}

public class TierPolicy
{
    /// <summary>
    /// Compute points required per vCPU for this tier
    /// - Guaranteed: 8 points (1:1 ratio)
    /// - Standard: 4 points (2:1 ratio)
    /// - Balanced: 2 points (4:1 ratio)
    /// - Burstable: 1 point (8:1 ratio)
    /// </summary>
    public int PointsPerVCpu { get; set; }

    /// <summary>
    /// CPU overcommit ratio (kept for reference and backward compat)
    /// </summary>
    public double CpuOvercommitRatio { get; set; }

    public double MemoryOvercommitRatio { get; set; }
    public double StorageOvercommitRatio { get; set; }
    public decimal PriceMultiplier { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Weights for multi-factor node scoring (must sum to 1.0)
/// </summary>
public class ScoringWeights
{
    /// <summary>
    /// Weight for available capacity (0.0 - 1.0)
    /// </summary>
    public double Capacity { get; set; } = 0.40;

    /// <summary>
    /// Weight for current load/utilization (0.0 - 1.0)
    /// </summary>
    public double Load { get; set; } = 0.25;

    /// <summary>
    /// Weight for node reputation/reliability (0.0 - 1.0)
    /// </summary>
    public double Reputation { get; set; } = 0.20;

    /// <summary>
    /// Weight for geographical proximity (0.0 - 1.0)
    /// </summary>
    public double Locality { get; set; } = 0.15;

    public void Validate()
    {
        var sum = Capacity + Load + Reputation + Locality;
        if (Math.Abs(sum - 1.0) > 0.001)
        {
            throw new InvalidOperationException(
                $"Scoring weights must sum to 1.0 (current: {sum})");
        }
    }
}

/// <summary>
/// Quality tier for VM scheduling with point-based allocation
/// </summary>
public enum QualityTier
{
    /// <summary>
    /// No overcommit, guaranteed resources, highest cost
    /// 8 points per vCPU (1:1 ratio)
    /// </summary>
    Guaranteed,

    /// <summary>
    /// Moderate overcommit, balanced, standard pricing
    /// 4 points per vCPU (2:1 ratio)
    /// </summary>
    Standard,

    /// <summary>
    /// Cost-optimized for consistent workloads
    /// 2 points per vCPU (4:1 ratio)
    /// </summary>
    Balanced,

    /// <summary>
    /// Aggressive overcommit, lowest cost, variable performance
    /// 1 point per vCPU (8:1 ratio)
    /// </summary>
    Burstable
}

/// <summary>
/// Calculated resource availability for a node considering overcommit and points
/// </summary>
public class NodeResourceAvailability
{
    public string NodeId { get; set; } = string.Empty;
    public QualityTier Tier { get; set; }

    // ========================================
    // POINT-BASED CPU TRACKING
    // ========================================

    /// <summary>
    /// Point cost required for the VM being evaluated
    /// </summary>
    public int RequiredComputePoints { get; set; }

    // ========================================
    // LEGACY: EFFECTIVE CAPACITY (kept for backward compat)
    // ========================================
    public int TotalComputePoints { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long TotalStorageBytes { get; set; }

    // Currently allocated (sum of VM specs)
    public int AllocatedComputePoints { get; set; }
    public long AllocatedMemoryBytes { get; set; }
    public long AllocatedStorageBytes { get; set; }

    // Remaining capacity after allocation
    public int RemainingComputePoints => TotalComputePoints - AllocatedComputePoints;
    public long RemainingMemoryBytes => TotalMemoryBytes - AllocatedMemoryBytes;
    public long RemainingStorageBytes => TotalStorageBytes - AllocatedStorageBytes;

    // Utilization percentages
    public double ComputeUtilization => TotalComputePoints > 0
        ? ((double)AllocatedComputePoints / TotalComputePoints) * 100
        : 0;

    public double MemoryUtilization => TotalMemoryBytes > 0
        ? (AllocatedMemoryBytes / TotalMemoryBytes) * 100
        : 0;

    // ========================================
    // CAPACITY CHECK (POINT-BASED)
    // ========================================

    /// <summary>
    /// Can this node fit the VM based on point-based allocation?
    /// </summary>
    public bool CanFit(VmSpec spec)
    {
        return RemainingComputePoints >= spec.ComputePointCost
            && RemainingMemoryBytes >= spec.MemoryBytes
            && RemainingStorageBytes >= spec.DiskBytes;
    }

    // Utilization after adding this VM
    public double ProjectedCpuUtilization(VmSpec spec)
    {
        return TotalComputePoints > 0
            ? ((double)(AllocatedComputePoints + spec.ComputePointCost) / TotalComputePoints) * 100
            : 100;
    }

    public double ProjectedMemoryUtilization(VmSpec spec)
    {
        return TotalMemoryBytes > 0
            ? ((AllocatedMemoryBytes + spec.MemoryBytes) / TotalMemoryBytes) * 100
            : 100;
    }
}

/// <summary>
/// Scored node candidate for scheduling
/// </summary>
public class ScoredNode
{
    public Node Node { get; set; } = null!;
    public NodeResourceAvailability Availability { get; set; } = null!;
    public double TotalScore { get; set; }
    public NodeScores ComponentScores { get; set; } = new();
    public string? RejectionReason { get; set; }
}

public class NodeScores
{
    public double CapacityScore { get; set; }
    public double LoadScore { get; set; }
    public double ReputationScore { get; set; }
    public double LocalityScore { get; set; }
}