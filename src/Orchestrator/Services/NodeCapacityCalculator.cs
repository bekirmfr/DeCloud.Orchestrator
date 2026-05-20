// src/Orchestrator/Services/NodeCapacityCalculator.cs
// Updated to read percentage-based allocation (AllocatedResources schema v2)
// while remaining backward-compatible with v1 absolute values.

using DeCloud.Shared.Models;
using Orchestrator.Models;
using Orchestrator.Services.VmScheduling;

namespace Orchestrator.Services;

/// <summary>
/// Calculates node capacity with tier-specific overcommit ratios.
/// Uses database-backed configuration from SchedulingConfigService.
///
/// <para><b>Allocation model (v2):</b> Operator allocation is expressed as
/// percentages of physical capacity. The calculator resolves percentages to
/// concrete byte/point values. Legacy v1 absolute values are accepted via
/// <see cref="DeCloud.Shared.Models.AllocatedResources.IsLegacyFormat"/> and are
/// used directly (capped at physical).</para>
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
        Node node,
        CancellationToken ct = default)
    {
        var config = await _configService.GetConfigAsync(ct);
        var evaluation = node.PerformanceEvaluation;
        if (evaluation == null || !evaluation.IsAcceptable)
        {
            return new NodeTotalCapacity
            {
                NodeId = node.Id,
                TotalComputePoints = 0,
                TotalMemoryBytes = 0,
                TotalStorageBytes = 0,
                IsAcceptable = false,
                RejectionReason = evaluation?.RejectionReason ?? "No performance evaluation"
            };
        }

        // Load current configuration
        var physicalCores = node.HardwareInventory.Cpu.PhysicalCores;
        var rawPhysicalMemory = node.HardwareInventory.Memory.TotalBytes;
        var physicalStorage = node.HardwareInventory.Storage.Sum(s => s.TotalBytes);

        // Get Burstable tier (maximum overcommit)
        var burstableTier = config.Tiers[QualityTier.Burstable];

        var basePointsPerCore = evaluation.PointsPerCore;

        // ========================================
        // CPU CAPACITY (using Burstable overcommit)
        // ========================================
        var hardwareMaxComputePoints = (int)(
            physicalCores *
            basePointsPerCore *
            config.BaselineOvercommitRatio);

        var totalComputePoints = ResolveComputePoints(
            node.AllocatedResources, hardwareMaxComputePoints);

        // ========================================
        // MEMORY CAPACITY (NO overcommit - physical only)
        // ========================================
        var totalMemoryBytes = ResolveMemoryBytes(
            node.AllocatedResources, rawPhysicalMemory);

        // ========================================
        // STORAGE CAPACITY (using Burstable overcommit)
        // Operator allocation is pre-overcommit; overcommit applied on top.
        // ========================================
        var operatorStorage = ResolveStorageBytes(
            node.AllocatedResources, physicalStorage);
        var totalStorageBytes = (long)(
            operatorStorage *
            burstableTier.StorageOvercommitRatio);

        var capacity = new NodeTotalCapacity
        {
            NodeId = node.Id,
            IsAcceptable = true,

            // Physical resources
            PhysicalCores = physicalCores,
            PhysicalMemoryBytes = rawPhysicalMemory,
            PhysicalStorageBytes = physicalStorage,

            // Base performance
            BasePointsPerCore = basePointsPerCore,
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

        // Load current configuration
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

        var physicalCores = node.HardwareInventory.Cpu.PhysicalCores;
        var rawPhysicalMemory = node.HardwareInventory.Memory.TotalBytes;
        var physicalStorage = node.HardwareInventory.Storage.Sum(s => s.TotalBytes);

        var basePointsPerCore = evaluation.PointsPerCore;

        // ========================================
        // TIER-SPECIFIC CPU CAPACITY
        // ========================================
        var hardwareTierPoints = (int)(
            physicalCores *
            basePointsPerCore *
            tierConfig.CpuOvercommitRatio);

        var tierComputePoints = ResolveComputePoints(
            node.AllocatedResources, hardwareTierPoints);

        // ========================================
        // TIER-SPECIFIC MEMORY (always physical, operator-bounded)
        // ========================================
        var tierMemoryBytes = ResolveMemoryBytes(
            node.AllocatedResources, rawPhysicalMemory);

        // ========================================
        // TIER-SPECIFIC STORAGE (operator-bounded, then overcommit)
        // ========================================
        var operatorStorage = ResolveStorageBytes(
            node.AllocatedResources, physicalStorage);
        var tierStorageBytes = (long)(operatorStorage * tierConfig.StorageOvercommitRatio);

        return new TierSpecificCapacity
        {
            NodeId = node.Id,
            Tier = tier,
            IsEligible = true,

            PhysicalCores = physicalCores,
            BasePointsPerCore = basePointsPerCore,

            TierComputePoints = tierComputePoints,
            TierMemoryBytes = tierMemoryBytes,
            TierStorageBytes = tierStorageBytes,

            CpuOvercommitRatio = tierConfig.CpuOvercommitRatio,
            StorageOvercommitRatio = tierConfig.StorageOvercommitRatio
        };
    }

    // =========================================================================
    // Allocation resolution helpers
    //
    // Each method supports both schema v2 (percentages) and v1 (absolute).
    // v2: multiply percentage × physical max, cap at physical.
    // v1: use absolute value directly, cap at physical.
    // null/missing: apply platform default (90%).
    // =========================================================================

    /// <summary>
    /// Resolve CPU compute points from operator allocation.
    /// </summary>
    private static int ResolveComputePoints(
        AllocatedResources? alloc,
        int hardwareMaxPoints)
    {
        if (alloc == null)
            return (int)(hardwareMaxPoints * AllocatedResources.DefaultPercent);

        if (!alloc.IsLegacyFormat)
        {
            // Schema v2: percentage-based
            var pct = alloc.EffectiveCpuPercent;
            return (int)(hardwareMaxPoints * pct);
        }

        // Schema v1: absolute value (legacy)
        return alloc.ComputePoints != null
            ? Math.Min(alloc.ComputePoints.Value, hardwareMaxPoints)
            : (int)(hardwareMaxPoints * AllocatedResources.DefaultPercent);
    }

    /// <summary>
    /// Resolve memory allocation in bytes from operator allocation.
    /// </summary>
    private static long ResolveMemoryBytes(
        AllocatedResources? alloc,
        long physicalMemoryBytes)
    {
        if (alloc == null)
            return (long)(physicalMemoryBytes * AllocatedResources.DefaultPercent);

        if (!alloc.IsLegacyFormat)
        {
            // Schema v2: percentage-based
            var pct = alloc.EffectiveMemoryPercent;
            return (long)(physicalMemoryBytes * pct);
        }

        // Schema v1: absolute value (legacy)
        return alloc.MemoryBytes != null
            ? Math.Min(alloc.MemoryBytes.Value, physicalMemoryBytes)
            : (long)(physicalMemoryBytes * AllocatedResources.DefaultPercent);
    }

    /// <summary>
    /// Resolve storage allocation in bytes (pre-overcommit) from operator allocation.
    /// </summary>
    private static long ResolveStorageBytes(
        AllocatedResources? alloc,
        long physicalStorageBytes)
    {
        if (alloc == null)
            return (long)(physicalStorageBytes * AllocatedResources.DefaultPercent);

        if (!alloc.IsLegacyFormat)
        {
            // Schema v2: percentage-based
            var pct = alloc.EffectiveStoragePercent;
            return (long)(physicalStorageBytes * pct);
        }

        // Schema v1: absolute value (legacy)
        return alloc.StorageBytes != null
            ? Math.Min(alloc.StorageBytes.Value, physicalStorageBytes)
            : (long)(physicalStorageBytes * AllocatedResources.DefaultPercent);
    }

    // =========================================================================
    // Logging
    // =========================================================================

    private void LogCapacityReport(NodeTotalCapacity capacity, Node node)
    {
        var allocInfo = node.AllocatedResources != null
            ? node.AllocatedResources.IsLegacyFormat
                ? "v1/absolute"
                : $"v2/percent (CPU={node.AllocatedResources.EffectiveCpuPercent:P0}, " +
                  $"Mem={node.AllocatedResources.EffectiveMemoryPercent:P0}, " +
                  $"Stor={node.AllocatedResources.EffectiveStoragePercent:P0})"
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
// Model classes (originally defined in this file)
// =========================================================================

/// <summary>
/// Total capacity for a node (using maximum overcommit)
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
/// Tier-specific capacity for a node
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