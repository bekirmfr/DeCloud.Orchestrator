using Orchestrator.Models;

namespace Orchestrator.Background;

/// <summary>
/// Calculates node capacity with tier-specific overcommit ratios
/// Unified approach: Base capacity from performance, overcommit per tier
/// </summary>
public class NodeCapacityCalculator
{
    private readonly ILogger<NodeCapacityCalculator> _logger;

    public NodeCapacityCalculator(
        ILogger<NodeCapacityCalculator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculate total node capacity across all tiers
    /// Uses Burstable tier (highest overcommit) as maximum theoretical capacity
    /// </summary>
    public NodeTotalCapacity CalculateTotalCapacity(Node node)
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

        var physicalCores = node.HardwareInventory.Cpu.PhysicalCores;
        var physicalMemory = node.HardwareInventory.Memory.AllocatableBytes;
        var physicalStorage = node.HardwareInventory.Storage.Sum(s => s.TotalBytes);

        // Get Burstable tier (maximum overcommit)
        var burstableTier = SchedulingConfiguration.TierRequirements[QualityTier.Burstable];

        // ========================================
        // CPU CAPACITY (using Burstable overcommit)
        // ========================================
        // Total points = base points/core × physical cores × burstable overcommit
        // This represents maximum theoretical capacity (single currency)
        var basePointsPerCore = evaluation.PointsPerCore;
        var totalComputePoints = (int)(
            physicalCores *
            basePointsPerCore *
            burstableTier.CpuOvercommitRatio);

        // ========================================
        // MEMORY CAPACITY (NO overcommit - physical only)
        // ========================================
        // Memory cannot be time-sliced - use physical capacity
        // VMs get full memory allocation at boot
        var totalMemoryBytes = physicalMemory;

        // ========================================
        // STORAGE CAPACITY (using Burstable overcommit)
        // ========================================
        // Safe with qcow2 thin provisioning
        // Use Burstable tier's storage overcommit as maximum
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
    public TierSpecificCapacity CalculateTierCapacity(
        Node node,
        QualityTier tier)
    {
        var evaluation = node.PerformanceEvaluation;
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

        var tierRequirements = SchedulingConfiguration.TierRequirements[tier];
        var physicalCores = node.HardwareInventory.Cpu.PhysicalCores;
        var physicalMemory = node.HardwareInventory.Memory.AllocatableBytes;
        var physicalStorage = node.HardwareInventory.Storage.Sum(s => s.TotalBytes);

        var basePointsPerCore = evaluation.PointsPerCore;

        // ========================================
        // TIER-SPECIFIC CPU CAPACITY
        // ========================================
        // Total points for this tier = base × cores × tier's overcommit
        var tierComputePoints = (int)(
            physicalCores *
            basePointsPerCore *
            tierRequirements.CpuOvercommitRatio);

        // ========================================
        // TIER-SPECIFIC MEMORY (always physical)
        // ========================================
        var tierMemoryBytes = physicalMemory;

        // ========================================
        // TIER-SPECIFIC STORAGE
        // ========================================
        var tierStorageBytes = (long)(
            physicalStorage *
            tierRequirements.StorageOvercommitRatio);

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

            CpuOvercommitRatio = tierRequirements.CpuOvercommitRatio,
            StorageOvercommitRatio = tierRequirements.StorageOvercommitRatio,

            PointsPerVCpu = tierRequirements.GetPointsPerVCpu(),
            MaxVCpus = (int)(tierComputePoints / tierRequirements.GetPointsPerVCpu()),
            PriceMultiplier = tierRequirements.PriceMultiplier
        };
    }

    private void LogCapacityReport(NodeTotalCapacity capacity, Node node)
    {
        _logger.LogInformation(
            "═══════════════════════════════════════════════════════════\n" +
            "NODE CAPACITY CALCULATION\n" +
            "═══════════════════════════════════════════════════════════\n" +
            "Node ID:         {NodeId}\n" +
            "CPU:             {Model}\n" +
            "Performance:     {Perf:F2}x baseline\n" +
            "───────────────────────────────────────────────────────────\n" +
            "PHYSICAL RESOURCES:\n" +
            "  Cores:         {Cores}\n" +
            "  Memory:        {Memory:N0} bytes ({MemoryGB:F1} GB)\n" +
            "  Storage:       {Storage:N0} bytes ({StorageGB:F1} GB)\n" +
            "───────────────────────────────────────────────────────────\n" +
            "BASE PERFORMANCE:\n" +
            "  Points/Core:   {PointsPerCore:F2}\n" +
            "───────────────────────────────────────────────────────────\n" +
            "TOTAL CAPACITY (Burstable overcommit):\n" +
            "  Compute:       {TotalPoints} points (CPU overcommit: {CpuOver}x)\n" +
            "  Memory:        {TotalMemory:N0} bytes (overcommit: {MemOver}x)\n" +
            "  Storage:       {TotalStorage:N0} bytes (overcommit: {StorOver}x)\n" +
            "───────────────────────────────────────────────────────────\n" +
            "MAX VMs (if all Burstable):\n" +
            "  vCPUs:         {MaxVCpus}\n" +
            "═══════════════════════════════════════════════════════════",
            capacity.NodeId,
            node.HardwareInventory.Cpu.Model,
            capacity.PerformanceMultiplier,
            capacity.PhysicalCores,
            capacity.PhysicalMemoryBytes,
            capacity.PhysicalMemoryBytes / 1024.0 / 1024.0 / 1024.0,
            capacity.PhysicalStorageBytes,
            capacity.PhysicalStorageBytes / 1024.0 / 1024.0 / 1024.0,
            capacity.BasePointsPerCore,
            capacity.TotalComputePoints,
            capacity.CpuOvercommitRatio,
            capacity.TotalMemoryBytes,
            capacity.MemoryOvercommitRatio,
            capacity.TotalStorageBytes,
            capacity.StorageOvercommitRatio,
            capacity.TotalComputePoints / 1.0); // Burstable = 1 point per vCPU
    }
}

/// <summary>
/// Total node capacity (using Burstable tier overcommit as maximum)
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
/// Tier-specific capacity calculation
/// </summary>
public class TierSpecificCapacity
{
    public string NodeId { get; set; } = string.Empty;
    public QualityTier Tier { get; set; }
    public bool IsEligible { get; set; }
    public string? IneligibilityReason { get; set; }

    public int PhysicalCores { get; set; }
    public double BasePointsPerCore { get; set; }

    // Tier-specific capacity
    public int TierComputePoints { get; set; }
    public long TierMemoryBytes { get; set; }
    public long TierStorageBytes { get; set; }

    // Tier overcommit ratios
    public double CpuOvercommitRatio { get; set; }
    public double StorageOvercommitRatio { get; set; }

    // Tier requirements
    public double PointsPerVCpu { get; set; }
    public int MaxVCpus { get; set; }
    public decimal PriceMultiplier { get; set; }
}