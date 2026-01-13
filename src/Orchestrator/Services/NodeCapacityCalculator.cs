using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Background;

/// <summary>
/// Calculates node capacity with tier-specific overcommit ratios
/// Uses database-backed configuration from SchedulingConfigService
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
    /// Calculate total node capacity across all tiers
    /// Uses Burstable tier (highest overcommit) as maximum theoretical capacity
    /// </summary>
    public async Task<NodeTotalCapacity> CalculateTotalCapacityAsync(
        Node node,
        CancellationToken ct = default)
    {
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
        var config = await _configService.GetConfigAsync(ct);

        var physicalCores = node.HardwareInventory.Cpu.PhysicalCores;
        var physicalMemory = node.HardwareInventory.Memory.AllocatableBytes;
        var physicalStorage = node.HardwareInventory.Storage.Sum(s => s.TotalBytes);

        // Get Burstable tier (maximum overcommit)
        var burstableTier = config.Tiers[QualityTier.Burstable];

        // ========================================
        // CPU CAPACITY (using Burstable overcommit)
        // ========================================
        var basePointsPerCore = evaluation.PointsPerCore;
        var totalComputePoints = (int)(
            physicalCores *
            basePointsPerCore *
            burstableTier.CpuOvercommitRatio);

        // ========================================
        // MEMORY CAPACITY (NO overcommit - physical only)
        // ========================================
        var totalMemoryBytes = physicalMemory;

        // ========================================
        // STORAGE CAPACITY (using Burstable overcommit)
        // ========================================
        var totalStorageBytes = (long)(
            physicalStorage *
            burstableTier.StorageOvercommitRatio);

        var capacity = new NodeTotalCapacity
        {
            NodeId = node.Id,
            IsAcceptable = true,

            // Physical resources
            PhysicalCores = physicalCores,
            PhysicalMemoryBytes = physicalMemory,
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
    /// Calculate tier-specific capacity for a given tier
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
            return new TierSpecificCapacity
            {
                NodeId = node.Id,
                Tier = tier,
                IsEligible = false,
                IneligibilityReason = evaluation?.TierCapabilities[tier]?.IneligibilityReason
                    ?? "Node not evaluated"
            };
        }

        var tierConfig = config.Tiers[tier];
        var physicalCores = node.HardwareInventory.Cpu.PhysicalCores;
        var physicalMemory = node.HardwareInventory.Memory.AllocatableBytes;
        var physicalStorage = node.HardwareInventory.Storage.Sum(s => s.TotalBytes);

        var basePointsPerCore = evaluation.PointsPerCore;

        // ========================================
        // TIER-SPECIFIC CPU CAPACITY
        // ========================================
        var tierComputePoints = (int)(
            physicalCores *
            basePointsPerCore *
            tierConfig.CpuOvercommitRatio);

        // ========================================
        // TIER-SPECIFIC MEMORY (always physical)
        // ========================================
        var tierMemoryBytes = physicalMemory;

        // ========================================
        // TIER-SPECIFIC STORAGE
        // ========================================
        var tierStorageBytes = (long)(
            physicalStorage *
            tierConfig.StorageOvercommitRatio);

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

    private void LogCapacityReport(NodeTotalCapacity capacity, Node node)
    {
        _logger.LogInformation(
            "Node {NodeId} Total Capacity: {Points} compute points, {Memory}GB RAM, {Storage}GB storage " +
            "(Physical: {PhysicalCores} cores, {PhysicalMem}GB RAM, {PhysicalStorage}GB storage) " +
            "CPU Overcommit: {CpuOvercommit}x, Storage Overcommit: {StorageOvercommit}x",
            capacity.NodeId,
            capacity.TotalComputePoints,
            capacity.TotalMemoryBytes / (1024 * 1024 * 1024),
            capacity.TotalStorageBytes / (1024 * 1024 * 1024),
            capacity.PhysicalCores,
            capacity.PhysicalMemoryBytes / (1024 * 1024 * 1024),
            capacity.PhysicalStorageBytes / (1024 * 1024 * 1024),
            capacity.CpuOvercommitRatio,
            capacity.StorageOvercommitRatio);
    }
}

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