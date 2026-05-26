using DeCloud.Shared.Contracts;
using DeCloud.Shared.Enums;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;

namespace Orchestrator.Models;

/// <summary>
/// Database-backed scheduling configuration
/// All scheduling parameters stored in MongoDB for dynamic updates
/// </summary>
public class SchedulingConfig
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = "default";

    /// <summary>
    /// Configuration version for tracking changes
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// When this configuration was first created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who made the last update (for audit trail)
    /// </summary>
    public string UpdatedBy { get; set; } = "system";

    // ========================================
    // BASELINE CONFIGURATION
    // ========================================

    /// <summary>
    /// Foundation of entire point system - evolves with technology
    /// Current: 1000 (Intel i3-10100 baseline)
    /// Future: Will increase as hardware improves (2000, 5000, 50000, etc.)
    /// </summary>
    public int BaselineBenchmark { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the baseline overcommit ratio used for resource allocation calculations.
    /// </summary>
    /// <remarks>This value determines how much resource overcommitment is allowed relative to the baseline
    /// capacity. Adjust this ratio to control the level of overcommitment permitted in resource planning
    /// scenarios.</remarks>
    public double BaselineOvercommitRatio { get; set; } = 4.0;

    /// <summary>
    /// Maximum performance multiplier cap (prevents excessive advantage)
    /// Example: 20.0x means max 20000 benchmark score counts as 20000, not higher
    /// </summary>
    public double MaxPerformanceMultiplier { get; set; } = 20.0;

    // ========================================
    // TIER CONFIGURATIONS
    // ========================================

    /// <summary>
    /// Configuration for each quality tier
    /// </summary>
    [BsonDictionaryOptions(DictionaryRepresentation.ArrayOfDocuments)] //MongoDB enum dictionary serialization
    public Dictionary<QualityTier, TierConfiguration> Tiers { get; set; } = new();

    // ========================================
    // SCHEDULING LIMITS
    // ========================================

    /// <summary>
    /// Safety limits and operational parameters
    /// </summary>
    public SchedulingLimits Limits { get; set; } = new();

    // ========================================
    // SCORING WEIGHTS
    // ========================================

    /// <summary>
    /// Weights for multi-factor node scoring (must sum to 1.0)
    /// </summary>
    public ScoringWeightsConfig Weights { get; set; } = new();

    /// <summary>
    /// Convert full SchedulingConfig to lightweight AgentSchedulingConfig
    /// Only includes fields that Node Agent actually needs for CPU quota calculations
    /// 
    /// What's included:
    /// - BaselineBenchmark: For calculating nodePointsPerCore
    /// - BaselineOvercommitRatio: From Burstable tier for quota calculations
    /// - MaxPerformanceMultiplier: Cap on performance advantage
    /// - Version: For change tracking
    /// 
    /// What's excluded (not needed by agents):
    /// - Full tier configurations (Standard, Balanced, Guaranteed)
    /// - Scheduling limits (MaxUtilization, MinFreeMem, MaxLoad)
    /// - Scoring weights (Capacity, Load, Reputation, Locality)
    /// - Price multipliers
    /// - Descriptions and target use cases
    /// </summary>
    public AgentSchedulingConfig MapToAgentConfig()
    {
        return new AgentSchedulingConfig
        {
            Version = Version,
            BaselineBenchmark = BaselineBenchmark,
            BaselineOvercommitRatio = BaselineOvercommitRatio,
            MaxPerformanceMultiplier = MaxPerformanceMultiplier,
            Tiers = Tiers,
            UpdatedAt = UpdatedAt
        };
    }
}

/// <summary>
/// Operational limits and safety parameters
/// </summary>
public class SchedulingLimits
{
    /// <summary>
    /// Maximum utilization threshold for scheduling.
    /// With operator-controlled allocation limits (docs/RESOURCE-ALLOCATION.md),
    /// the operator's allocation IS the safety boundary — the 10% holdback is
    /// already baked into TotalResources. Default 100% means "fill to the
    /// operator's stated capacity." Override below 100% only if additional
    /// per-deployment headroom is needed beyond the operator's allocation.
    /// </summary>
    public double MaxUtilizationPercent { get; set; } = 100.0;

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
}

/// <summary>
/// Scoring weights for node selection (must sum to 1.0)
/// </summary>
public class ScoringWeightsConfig
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
}

/// <summary>
/// Extension methods for working with SchedulingConfig
/// </summary>
public static class SchedulingConfigExtensions
{
    /// <summary>
    /// Calculate points required per vCPU for a tier
    /// Formula: (MinimumBenchmark / BurstableBaseline) × (BurstableOvercommit / TierOvercommit)
    /// </summary>
    public static double GetPointsPerVCpu(
        this TierConfiguration tierConfig,
        int baselineBenchmark,
        double baselineOvercommitRatio)
    {
        if (baselineBenchmark <= 0)
            throw new ArgumentException("Baseline benchmark must be positive", nameof(baselineBenchmark));

        if (baselineOvercommitRatio <= 0)
            throw new ArgumentException("Baseline overcommit ratio must be positive", nameof(baselineOvercommitRatio));

        return ((double)tierConfig.MinimumBenchmark / baselineBenchmark) *
               (baselineOvercommitRatio / tierConfig.CpuOvercommitRatio);
    }

    /// <summary>
    /// Validate scoring weights sum to 1.0
    /// </summary>
    public static bool IsValid(this ScoringWeightsConfig weights, out string? error)
    {
        var sum = weights.Capacity + weights.Load + weights.Reputation + weights.Locality;

        if (Math.Abs(sum - 1.0) > 0.001)
        {
            error = $"Scoring weights must sum to 1.0 (current: {sum:F3})";
            return false;
        }

        if (weights.Capacity < 0 || weights.Load < 0 ||
            weights.Reputation < 0 || weights.Locality < 0)
        {
            error = "All weights must be non-negative";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// Bandwidth tier for VM network rate limiting.
/// Enforced via libvirt QoS on the VM's virtio network interface.
/// </summary>
public enum BandwidthTier
{
    /// <summary>10 Mbps average, 20 Mbps burst - Light browsing, text</summary>
    Basic = 0,

    /// <summary>50 Mbps average, 100 Mbps burst - General browsing, streaming</summary>
    Standard = 1,

    /// <summary>200 Mbps average, 400 Mbps burst - HD video, downloads</summary>
    Performance = 2,

    /// <summary>No artificial cap - limited only by host NIC</summary>
    Unmetered = 3
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