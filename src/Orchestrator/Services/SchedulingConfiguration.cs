using Orchestrator.Models;

namespace Orchestrator.Background;

/// <summary>
/// Unified scheduling configuration based on performance benchmarks
/// All tier requirements calculated relative to BurstableBaselineBenchmark
/// </summary>
public class SchedulingConfiguration
{
    /// <summary>
    /// Foundation of entire point system - evolves with technology
    /// Current: 1000 (Intel i3-10100 baseline)
    /// Future: Will increase as hardware improves (2000, 5000, 50000, etc.)
    /// </summary>
    public int BurstableBaselineBenchmark { get; set; } = 1000;

    /// <summary>
    /// Maximum performance multiplier cap (prevents excessive advantage)
    /// Example: 5.0x means max 5000 benchmark score counts as 5000, not higher
    /// </summary>
    public double MaxPerformanceMultiplier { get; set; } = 20.0;

    /// <summary>
    /// Tier requirements - PointsPerVCpu calculated automatically from benchmarks
    /// Formula: PointsPerVCpu = TierMinimumBenchmark / BurstableBaselineBenchmark
    /// </summary>
    public Dictionary<QualityTier, TierRequirements> TierRequirements { get; set; } = new()
    {
        [QualityTier.Burstable] = new TierRequirements
        {
            MinimumBenchmark = 1000,        // Entry-level (Intel i3-10100)
            CpuOvercommitRatio = 8.0,       // 8:1 aggressive overcommit
            StorageOvercommitRatio = 2.5,   // High storage overcommit
            PriceMultiplier = 0.3m,
            Description = "Best-effort performance, entry-level hardware, 8:1 CPU overcommit",
            TargetUseCase = "Development, testing, light workloads"
        },
        [QualityTier.Balanced] = new TierRequirements
        {
            MinimumBenchmark = 1500,        // Mid-range (Intel i5-12400)
            CpuOvercommitRatio = 4.0,       // 4:1 overcommit
            StorageOvercommitRatio = 2.0,   // Moderate storage overcommit
            PriceMultiplier = 0.6m,
            Description = "Balanced performance for production workloads, 4:1 CPU overcommit",
            TargetUseCase = "Web servers, databases, AI inference"
        },
        [QualityTier.Standard] = new TierRequirements
        {
            MinimumBenchmark = 2500,        // High-end (AMD Ryzen 5 7600X)
            CpuOvercommitRatio = 2.0,       // 2:1 overcommit
            StorageOvercommitRatio = 1.5,   // Conservative storage overcommit
            PriceMultiplier = 1.0m,
            Description = "High performance for demanding applications, 2:1 CPU overcommit",
            TargetUseCase = "High-traffic apps, real-time processing, model training"
        },
        [QualityTier.Guaranteed] = new TierRequirements
        {
            MinimumBenchmark = 4000,        // Enthusiast/Server (AMD Ryzen 9 7950X)
            CpuOvercommitRatio = 1.0,       // No overcommit - dedicated cores
            StorageOvercommitRatio = 1.0,   // No storage overcommit
            PriceMultiplier = 2.5m,
            Description = "Dedicated high-performance resources, guaranteed 1:1 CPU performance",
            TargetUseCase = "Mission-critical apps, large models, financial trading"
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

    /// <summary>
    /// Last baseline update timestamp
    /// </summary>
    public DateTime BaselineLastUpdated { get; set; } = new DateTime(2025, 1, 1);
}

/// <summary>
/// Tier performance requirements
/// PointsPerVCpu automatically calculated from benchmark ratio
/// </summary>
public class TierRequirements
{
    /// <summary>
    /// Minimum benchmark score required to host this tier
    /// </summary>
    public int MinimumBenchmark { get; set; }

    /// <summary>
    /// CPU overcommit ratio for this tier
    /// Guaranteed: 1.0 (no overcommit), Standard: 2.0, Balanced: 4.0, Burstable: 8.0
    /// </summary>
    public double CpuOvercommitRatio { get; set; }

    /// <summary>
    /// Storage overcommit ratio for this tier (safe with qcow2 thin provisioning)
    /// </summary>
    public double StorageOvercommitRatio { get; set; }

    /// <summary>
    /// Price multiplier for this tier
    /// </summary>
    public decimal PriceMultiplier { get; set; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Target use case for this tier
    /// </summary>
    public string TargetUseCase { get; set; } = string.Empty;

    /// <summary>
    /// Calculate points required per vCPU for this tier
    /// Formula: MinimumBenchmark / BurstableBaseline
    /// Example: Standard tier (2500) / Burstable baseline (1000) = 2.5 points/vCPU
    /// </summary>
    public double GetPointsPerVCpu(int burstableBaseline)
    {
        if (burstableBaseline <= 0)
            throw new ArgumentException("Burstable baseline must be positive", nameof(burstableBaseline));

        return (double)MinimumBenchmark / burstableBaseline;
    }
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
/// Calculated resource availability for a node considering overcommit and points
/// </summary>
public class NodeResourceAvailability
{
    public string NodeId { get; set; } = string.Empty;
    public QualityTier Tier { get; set; }

    // Total capacity
    public int TotalComputePoints { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long TotalStorageBytes { get; set; }

    // Currently allocated (sum of VM specs)
    public int AllocatedComputePoints { get; set; }
    public long AllocatedMemoryBytes { get; set; }
    public long AllocatedStorageBytes { get; set; }

    // Required for current VM being evaluated
    public int RequiredComputePoints { get; set; }

    // Remaining capacity after allocation
    public int RemainingComputePoints => TotalComputePoints - AllocatedComputePoints;
    public long RemainingMemoryBytes => TotalMemoryBytes - AllocatedMemoryBytes;
    public long RemainingStorageBytes => TotalStorageBytes - AllocatedStorageBytes;

    // Utilization percentages
    public double ComputeUtilization => TotalComputePoints > 0
        ? ((double)AllocatedComputePoints / TotalComputePoints) * 100
        : 0;

    public double MemoryUtilization => TotalMemoryBytes > 0
        ? ((double)AllocatedMemoryBytes / TotalMemoryBytes) * 100
        : 0;

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
            ? ((double)(AllocatedMemoryBytes + spec.MemoryBytes) / TotalMemoryBytes) * 100
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
    public NodeScores Scores { get; set; } = new();
    public double TotalScore { get; set; }
    public string? RejectionReason { get; set; }
}

/// <summary>
/// Individual scoring components for a node
/// </summary>
public class NodeScores
{
    public double CapacityScore { get; set; }
    public double LoadScore { get; set; }
    public double ReputationScore { get; set; }
    public double LocalityScore { get; set; }
}

/// <summary>
/// Quality tier for VM scheduling with benchmark-based allocation
/// </summary>
public enum QualityTier
{
    /// <summary>
    /// Dedicated resources, guaranteed performance
    /// Requires highest-performance nodes (4000+ benchmark)
    /// </summary>
    Guaranteed = 0,

    /// <summary>
    /// High performance for demanding applications
    /// Requires high-end nodes (2500+ benchmark)
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Balanced performance for production workloads
    /// Requires mid-range nodes (1500+ benchmark)
    /// </summary>
    Balanced = 2,

    /// <summary>
    /// Best-effort, lowest cost
    /// Minimum acceptable performance (1000+ benchmark)
    /// </summary>
    Burstable = 3
}