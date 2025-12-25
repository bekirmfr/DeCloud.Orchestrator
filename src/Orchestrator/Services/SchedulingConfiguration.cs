using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Configuration for VM scheduling and resource overcommit policies.
/// 
/// OVERCOMMIT DESIGN PHILOSOPHY:
/// =============================
/// Overcommit ratios are applied to PHYSICAL CPU cores, not logical threads.
/// This aligns with industry standards (AWS, GCP, Azure) where:
/// - 1 vCPU = 1 hyperthread in most cases
/// - Overcommit determines how many vCPUs can be sold per physical core
/// 
/// Example: Server with 16 physical cores, 32 threads (hyperthreading)
/// 
/// Tier        | Ratio | Effective vCPUs | vCPU:Physical | Reality
/// ------------|-------|-----------------|---------------|------------------
/// Guaranteed  | 1.0   | 16              | 1:1           | Dedicated core per vCPU
/// Standard    | 2.0   | 32              | 2:1           | Shared, good for most workloads
/// Burstable   | 4.0   | 64              | 4:1           | Heavily shared, burst capacity
/// 
/// Note: Standard tier at 2:1 effectively matches the hyperthread count,
/// which provides good performance for typical workloads.
/// </summary>
public class SchedulingConfiguration
{
    /// <summary>
    /// Quality tier definitions with different overcommit guarantees
    /// </summary>
    public Dictionary<QualityTier, TierPolicy> TierPolicies { get; set; } = new()
    {
        [QualityTier.Guaranteed] = new TierPolicy
        {
            CpuOvercommitRatio = 1.0,      // 1:1 - No overcommit
            MemoryOvercommitRatio = 1.0,   // 1:1 - No overcommit
            StorageOvercommitRatio = 1.0,  // 1:1 - No overcommit
            PriceMultiplier = 2.0m,         // 2x price premium
            Description = "Dedicated physical , guaranteed performance"
        },
        [QualityTier.Standard] = new TierPolicy
        {
            CpuOvercommitRatio = 2.0,      // 2:1 - Moderate overcommit
            MemoryOvercommitRatio = 1.2,   // 1.2:1 - Slight overcommit
            StorageOvercommitRatio = 1.5,  // 1.5:1 - Storage can be overcommitted more
            PriceMultiplier = 1.0m,         // Base price
            Description = "Shared cores (2:1), balanced performance and cost"
        },
        [QualityTier.Burstable] = new TierPolicy
        {
            CpuOvercommitRatio = 4.0,      // 4:1 - Aggressive overcommit
            MemoryOvercommitRatio = 1.5,   // 1.5:1 - Moderate memory overcommit
            StorageOvercommitRatio = 2.0,  // 2:1 - Storage overcommit
            PriceMultiplier = 0.5m,         // 50% cheaper
            Description = "Heavily shared cores (4:1), burst capacity, variable performance"
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
    /// CPU overcommit ratio applied to PHYSICAL cores.
    /// Example: 2.0 means 2 vCPUs can be allocated per physical core.
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
/// Quality tier for VM scheduling
/// </summary>
public enum QualityTier
{
    /// <summary>
    /// No overcommit, guaranteed resources, highest cost
    /// </summary>
    Guaranteed,

    /// <summary>
    /// Moderate overcommit, balanced, standard pricing
    /// </summary>
    Standard,

    /// <summary>
    /// Aggressive overcommit, lowest cost, variable performance
    /// </summary>
    Burstable
}

/// <summary>
/// Calculated resource availability for a node considering overcommit
/// </summary>
public class NodeResourceAvailability
{
    public string NodeId { get; set; } = string.Empty;
    public QualityTier Tier { get; set; }

    // Raw physical resources (from node registration)
    public int PhysicalCpuCores { get; set; }
    public int LogicalCpuThreads { get; set; }

    // Effective capacity with overcommit applied
    public double EffectiveCpuCapacity { get; set; }
    public double EffectiveMemoryCapacity { get; set; }
    public double EffectiveStorageCapacity { get; set; }

    // Currently allocated (sum of VM specs)
    public double AllocatedCpu { get; set; }
    public double AllocatedMemory { get; set; }
    public double AllocatedStorage { get; set; }

    // Remaining capacity after allocation
    public double RemainingCpu => EffectiveCpuCapacity - AllocatedCpu;
    public double RemainingMemory => EffectiveMemoryCapacity - AllocatedMemory;
    public double RemainingStorage => EffectiveStorageCapacity - AllocatedStorage;

    // Utilization percentages
    public double CpuUtilization => EffectiveCpuCapacity > 0
        ? (AllocatedCpu / EffectiveCpuCapacity) * 100
        : 0;
    public double MemoryUtilization => EffectiveMemoryCapacity > 0
        ? (AllocatedMemory / EffectiveMemoryCapacity) * 100
        : 0;

    // Can this node fit the VM?
    public bool CanFit(VmSpec spec)
    {
        return RemainingCpu >= spec.CpuCores
            && RemainingMemory >= spec.MemoryMb
            && RemainingStorage >= spec.DiskGb;
    }

    // Utilization after adding this VM
    public double ProjectedCpuUtilization(VmSpec spec)
    {
        return EffectiveCpuCapacity > 0
            ? ((AllocatedCpu + spec.CpuCores) / EffectiveCpuCapacity) * 100
            : 100;
    }

    public double ProjectedMemoryUtilization(VmSpec spec)
    {
        return EffectiveMemoryCapacity > 0
            ? ((AllocatedMemory + spec.MemoryMb) / EffectiveMemoryCapacity) * 100
            : 100;
    }

    /// <summary>
    /// Generate a human-readable capacity summary
    /// </summary>
    public string GetCapacitySummary()
    {
        return $"Physical: {PhysicalCpuCores}c/{LogicalCpuThreads}t | " +
               $"Effective: {EffectiveCpuCapacity:F1} vCPUs ({Tier}) | " +
               $"Allocated: {AllocatedCpu:F1} | " +
               $"Remaining: {RemainingCpu:F1}";
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