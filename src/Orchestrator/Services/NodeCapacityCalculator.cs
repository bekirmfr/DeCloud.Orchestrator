// src/Orchestrator/Services/NodeCapacityCalculator.cs
//
// Calculates node capacity with tier-specific overcommit ratios.
// Allocation is resolved into concrete values at allocate time and stored
// on node.AllocatedResources (ResourceSnapshot). The calculator applies
// tier-specific overcommit ratios on top.

using DeCloud.Shared.Models;
using Orchestrator.Models;
using Orchestrator.Services.VmScheduling;

namespace Orchestrator.Services;

/// <summary>
/// Calculates node capacity with tier-specific overcommit ratios.
/// Uses database-backed configuration from SchedulingConfigService.
///
/// <para>Allocation is resolved into concrete values at allocate time
/// and stored on <c>node.AllocatedResources</c> (ResourceSnapshot).
/// The calculator applies tier-specific overcommit ratios on top.</para>
///
/// <para>Resource model:</para>
/// <list type="bullet">
///   <item><c>TotalResources</c> — physical totals (set at evaluate)</item>
///   <item><c>AllocationConfig</c> — raw percentages (set at allocate)</item>
///   <item><c>AllocatedResources</c> — concrete values = TotalResources × percentages (set at allocate)</item>
///   <item><c>UsedResources</c> — running VM sum (set at heartbeat)</item>
///   <item><c>ReservedResources</c> — transient scheduling holds</item>
/// </list>
/// </summary>
public class NodeCapacityCalculator
{
    private readonly ILogger<NodeCapacityCalculator> _logger;
    private readonly ISchedulingConfigService _configService;

    public NodeCapacityCalculator(
        ILogger<NodeCapacityCalculator> logger,
        ISchedulingConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    /// <summary>
    /// Calculate total node capacity across all tiers.
    /// Uses Burstable tier (highest overcommit) as maximum theoretical capacity.
    /// </summary>
    public async Task<NodeTotalCapacity> CalculateTotalCapacityAsync(
         Node node, CancellationToken ct = default)
    {
        var config = await _configService.GetConfigAsync(ct);
        var evaluation = node.PerformanceEvaluation;

        if (evaluation == null || !evaluation.IsAcceptable)
        {
            return new NodeTotalCapacity
            {
                NodeId = node.Id,
                IsAcceptable = false,
                RejectionReason = evaluation?.RejectionReason ?? "No evaluation"
            };
        }

        // AllocatedResources already holds concrete values resolved at
        // allocate time (percentages × TotalResources). The calculator
        // only applies overcommit ratios on top.
        var alloc = node.AllocatedResources;
        var burstableTier = config.Tiers[QualityTier.Burstable];

        // CPU: allocated points × overcommit
        var totalComputePoints = (int)(alloc.ComputePoints * burstableTier.CpuOvercommitRatio);

        // Memory: no overcommit — always physical
        var totalMemoryBytes = alloc.MemoryBytes;

        // Storage: allocated × overcommit
        var totalStorageBytes = (long)(alloc.StorageBytes * burstableTier.StorageOvercommitRatio);

        var capacity = new NodeTotalCapacity
        {
            NodeId = node.Id,
            IsAcceptable = true,

            // Physical resources
            PhysicalCores = node.HardwareInventory.Cpu.PhysicalCores,
            PhysicalMemoryBytes = node.HardwareInventory.Memory.TotalBytes,
            PhysicalStorageBytes = node.HardwareInventory.Storage.Sum(s => s.TotalBytes),

            // Base performance
            BasePointsPerCore = evaluation.PointsPerCore,
            PerformanceMultiplier = evaluation.PerformanceMultiplier,

            // Total capacity (with Burstable overcommit)
            TotalComputePoints = totalComputePoints,
            TotalMemoryBytes = totalMemoryBytes,
            TotalStorageBytes = totalStorageBytes,

            // Overcommit ratios applied
            CpuOvercommitRatio = burstableTier.CpuOvercommitRatio,
            MemoryOvercommitRatio = 1.0, // Never overcommit memory
            StorageOvercommitRatio = burstableTier.StorageOvercommitRatio
        };

        LogCapacityReport(capacity, node);

        return capacity;
    }

    /// <summary>
    /// Calculate tier-specific capacity for a given tier.
    /// </summary>
    public async Task<TierSpecificCapacity> CalculateTierCapacityAsync(
        Node node,
        QualityTier tier,
        CancellationToken ct = default)
    {
        var evaluation = node.PerformanceEvaluation;
        var config = await _configService.GetConfigAsync(ct);

        if (evaluation == null || !evaluation.EligibleTiers.Contains(tier))
        {
            var reason = evaluation == null
                ? "Node not evaluated"
                : evaluation.TierCapabilities.TryGetValue(tier, out var cap)
                    ? cap.IneligibilityReason ?? $"Not eligible for tier {tier}"
                    : $"Not eligible for tier {tier}";

            return new TierSpecificCapacity
            {
                NodeId = node.Id,
                Tier = tier,
                IsEligible = false,
                IneligibilityReason = reason
            };
        }

        if (!config.Tiers.TryGetValue(tier, out var tierConfig))
        {
            return new TierSpecificCapacity
            {
                NodeId = node.Id,
                Tier = tier,
                IsEligible = false,
                IneligibilityReason = $"Tier {tier} not configured"
            };
        }

        // AllocatedResources already holds concrete values resolved at
        // allocate time. Apply tier-specific overcommit ratios.
        var alloc = node.AllocatedResources;

        // CPU: allocated × tier overcommit
        var tierComputePoints = (int)(alloc.ComputePoints * tierConfig.CpuOvercommitRatio);

        // Memory: no overcommit
        var tierMemoryBytes = alloc.MemoryBytes;

        // Storage: allocated × tier overcommit
        var tierStorageBytes = (long)(alloc.StorageBytes * tierConfig.StorageOvercommitRatio);

        return new TierSpecificCapacity
        {
            NodeId = node.Id,
            Tier = tier,
            IsEligible = true,

            PhysicalCores = node.HardwareInventory.Cpu.PhysicalCores,
            BasePointsPerCore = evaluation.PointsPerCore,

            TierComputePoints = tierComputePoints,
            TierMemoryBytes = tierMemoryBytes,
            TierStorageBytes = tierStorageBytes,

            CpuOvercommitRatio = tierConfig.CpuOvercommitRatio,
            StorageOvercommitRatio = tierConfig.StorageOvercommitRatio
        };
    }

    // =========================================================================
    // Logging
    // =========================================================================

    private void LogCapacityReport(NodeTotalCapacity capacity, Node node)
    {
        var cfg = node.AllocationConfig;
        var allocInfo = cfg != null
            ? $"CPU={cfg.EffectiveCpuPercent:P0}, Mem={cfg.EffectiveMemoryPercent:P0}, " +
              $"Stor={cfg.EffectiveStoragePercent:P0}"
            : "default (90%)";

        _logger.LogInformation(
            "Node {NodeId} Total Capacity: {Points} compute points, {Memory}GB RAM, {Storage}GB storage " +
            "(Physical: {PhysicalCores} cores, {PhysicalMem}GB RAM, {PhysicalStorage}GB storage) " +
            "CPU Overcommit: {CpuOvercommit}x, Storage Overcommit: {StorageOvercommit}x, " +
            "Allocation: {AllocInfo}",
            capacity.NodeId,
            capacity.TotalComputePoints,
            capacity.TotalMemoryBytes / (1024 * 1024 * 1024),
            capacity.TotalStorageBytes / (1024 * 1024 * 1024),
            capacity.PhysicalCores,
            capacity.PhysicalMemoryBytes / (1024 * 1024 * 1024),
            capacity.PhysicalStorageBytes / (1024 * 1024 * 1024),
            capacity.CpuOvercommitRatio,
            capacity.StorageOvercommitRatio,
            allocInfo);
    }
}

// =========================================================================
// Model classes
// =========================================================================

/// <summary>
/// Total capacity for a node (using maximum overcommit).
/// </summary>
public class NodeTotalCapacity
{
    public string NodeId { get; set; } = string.Empty;
    public bool IsAcceptable { get; set; }
    public string? RejectionReason { get; set; }

    // Physical resources
    public int PhysicalCores { get; set; }
    public long PhysicalMemoryBytes { get; set; }
    public long PhysicalStorageBytes { get; set; }

    // Performance metrics
    public double BasePointsPerCore { get; set; }
    public double PerformanceMultiplier { get; set; }

    // Total capacity (with overcommit)
    public int TotalComputePoints { get; set; }
    public long TotalMemoryBytes { get; set; }
    public long TotalStorageBytes { get; set; }

    // Overcommit ratios applied
    public double CpuOvercommitRatio { get; set; }
    public double MemoryOvercommitRatio { get; set; }
    public double StorageOvercommitRatio { get; set; }
}

/// <summary>
/// Tier-specific capacity for a node.
/// </summary>
public class TierSpecificCapacity
{
    public string NodeId { get; set; } = string.Empty;
    public QualityTier Tier { get; set; }
    public bool IsEligible { get; set; }
    public string? IneligibilityReason { get; set; }

    // Physical resources
    public int PhysicalCores { get; set; }
    public double BasePointsPerCore { get; set; }

    // Tier-specific capacity
    public int TierComputePoints { get; set; }
    public long TierMemoryBytes { get; set; }
    public long TierStorageBytes { get; set; }

    // Tier configuration
    public double CpuOvercommitRatio { get; set; }
    public double StorageOvercommitRatio { get; set; }
}