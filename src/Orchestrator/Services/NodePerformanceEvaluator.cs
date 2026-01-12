using MongoDB.Bson.Serialization.Attributes;
using Orchestrator.Models;

namespace Orchestrator.Background;

/// <summary>
/// Evaluates node performance and determines tier eligibility
/// Uses unified benchmark-based formula: PointsPerCore = NodeBenchmark / BurstableBaseline
/// </summary>
public class NodePerformanceEvaluator
{
    private readonly ILogger<NodePerformanceEvaluator> _logger;

    public NodePerformanceEvaluator(
        ILogger<NodePerformanceEvaluator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Evaluate node performance and calculate tier eligibility
    /// </summary>
    public NodePerformanceEvaluation EvaluateNode(Node node)
    {
        var benchmarkScore = node.HardwareInventory.Cpu.BenchmarkScore;
        var cpuModel = node.HardwareInventory.Cpu.Model;
        var burstableBaseline = SchedulingConfiguration.BaselineBenchmark;

        // Apply performance cap if configured
        var cappedScore = Math.Min(
            benchmarkScore,
            (int)(burstableBaseline * SchedulingConfiguration.MaxPerformanceMultiplier));

        // Single source of truth: points per core
        var pointsPerCore = (double)cappedScore / burstableBaseline;

        var evaluation = new NodePerformanceEvaluation
        {
            NodeId = node.Id,
            CpuModel = cpuModel,
            BenchmarkScore = benchmarkScore,
            CappedBenchmarkScore = cappedScore,
            BurstableBaseline = burstableBaseline,
            PointsPerCore = pointsPerCore,
            PerformanceMultiplier = (double)benchmarkScore / burstableBaseline,
            CappedPerformanceMultiplier = pointsPerCore,
            EligibleTiers = new List<QualityTier>(),
            TierCapabilities = new Dictionary<QualityTier, TierCapability>()
        };

        // Check eligibility for each tier (from highest to lowest)
        foreach (var (tier, requirements) in SchedulingConfiguration.TierRequirements
            .OrderByDescending(kvp => kvp.Value.MinimumBenchmark))
        {
            var requiredPointsPerVCpu = requirements.GetPointsPerVCpu();

            // Simple comparison: can this node provide enough points?
            var isEligible = pointsPerCore >= requiredPointsPerVCpu;

            if (isEligible)
            {
                evaluation.EligibleTiers.Add(tier);

                evaluation.TierCapabilities[tier] = new TierCapability
                {
                    Tier = tier,
                    MinimumBenchmark = requirements.MinimumBenchmark,
                    RequiredPointsPerVCpu = requiredPointsPerVCpu,
                    NodePointsPerCore = pointsPerCore,
                    MaxVCpusPerCore = (int)(pointsPerCore / requiredPointsPerVCpu),
                    PriceMultiplier = requirements.PriceMultiplier,
                    Description = requirements.Description,
                    IsEligible = true
                };
            }
            else
            {
                var gap = requiredPointsPerVCpu - pointsPerCore;
                var percentageGap = (gap / requiredPointsPerVCpu) * 100;

                evaluation.TierCapabilities[tier] = new TierCapability
                {
                    Tier = tier,
                    MinimumBenchmark = requirements.MinimumBenchmark,
                    RequiredPointsPerVCpu = requiredPointsPerVCpu,
                    NodePointsPerCore = pointsPerCore,
                    PriceMultiplier = requirements.PriceMultiplier,
                    IsEligible = false,
                    IneligibilityReason = $"Node provides {pointsPerCore:F2} points/core, " +
                                         $"tier requires {requiredPointsPerVCpu:F2} " +
                                         $"(gap: {percentageGap:F1}%)"
                };
            }
        }

        // Node must support at least Burstable tier
        evaluation.IsAcceptable = evaluation.EligibleTiers.Any();

        if (!evaluation.IsAcceptable)
        {
            var burstableRequirement = SchedulingConfiguration.TierRequirements[QualityTier.Burstable]
                .GetPointsPerVCpu();
            evaluation.RejectionReason =
                $"Performance {pointsPerCore:F2} points/core below minimum " +
                $"Burstable requirement {burstableRequirement:F2}";
        }
        else
        {
            evaluation.HighestTier = evaluation.EligibleTiers.First();
            evaluation.PerformanceClass = ClassifyPerformance(pointsPerCore, burstableBaseline);
        }

        LogEligibilityReport(evaluation);

        return evaluation;
    }

    private PerformanceClass ClassifyPerformance(double pointsPerCore, int baseline)
    {
        var multiplier = pointsPerCore;
        var guaranteedReq = (double)SchedulingConfiguration.TierRequirements[QualityTier.Guaranteed].MinimumBenchmark / baseline;
        var standardReq = (double)SchedulingConfiguration.TierRequirements[QualityTier.Standard].MinimumBenchmark / baseline;
        var balancedReq = (double)SchedulingConfiguration.TierRequirements[QualityTier.Balanced].MinimumBenchmark / baseline;
        var burstableReq = (double)SchedulingConfiguration.TierRequirements[QualityTier.Burstable].MinimumBenchmark / baseline;

        if (multiplier >= guaranteedReq)
            return PerformanceClass.UltraHighEnd;
        if (multiplier >= standardReq)
            return PerformanceClass.HighEnd;
        if (multiplier >= balancedReq)
            return PerformanceClass.MidRange;
        if (multiplier >= burstableReq)
            return PerformanceClass.Budget;

        return PerformanceClass.BelowMinimum;
    }

    private void LogEligibilityReport(NodePerformanceEvaluation evaluation)
    {
        _logger.LogInformation(
            "═══════════════════════════════════════════════════════════\n" +
            "NODE TIER ELIGIBILITY REPORT\n" +
            "═══════════════════════════════════════════════════════════\n" +
            "Node ID:      {NodeId}\n" +
            "CPU:          {Model}\n" +
            "Benchmark:    {Score} (capped: {Capped})\n" +
            "Performance:  {Mult:F2}x baseline (capped: {CappedMult:F2}x)\n" +
            "Points/Core:  {PointsPerCore:F2}\n" +
            "Class:        {Class}\n" +
            "Status:       {Status}\n" +
            "───────────────────────────────────────────────────────────",
            evaluation.NodeId,
            evaluation.CpuModel,
            evaluation.BenchmarkScore,
            evaluation.CappedBenchmarkScore,
            evaluation.PerformanceMultiplier,
            evaluation.CappedPerformanceMultiplier,
            evaluation.PointsPerCore,
            evaluation.PerformanceClass,
            evaluation.IsAcceptable ? "✓ ACCEPTED" : "✗ REJECTED");

        if (!evaluation.IsAcceptable)
        {
            _logger.LogWarning("Rejection Reason: {Reason}", evaluation.RejectionReason);
            return;
        }

        _logger.LogInformation("Eligible Tiers: {Tiers}",
            string.Join(", ", evaluation.EligibleTiers));
        _logger.LogInformation("Highest Tier:   {Tier}", evaluation.HighestTier);

        foreach (var (tier, capability) in evaluation.TierCapabilities
            .OrderByDescending(kvp => kvp.Value.MinimumBenchmark))
        {
            if (capability.IsEligible)
            {
                _logger.LogInformation(
                    "  ✓ {Tier,-12} | {Points:F2} pts/vCPU | Max {MaxVCpus}:1 overcommit | ${Price}x pricing",
                    tier,
                    capability.RequiredPointsPerVCpu,
                    capability.MaxVCpusPerCore,
                    capability.PriceMultiplier);
            }
            else
            {
                _logger.LogDebug(
                    "  ✗ {Tier,-12} | {Reason}",
                    tier,
                    capability.IneligibilityReason);
            }
        }

        _logger.LogInformation(
            "═══════════════════════════════════════════════════════════");
    }
}

/// <summary>
/// Result of node performance evaluation
/// </summary>
public class NodePerformanceEvaluation
{
    public string NodeId { get; set; } = string.Empty;
    public string CpuModel { get; set; } = string.Empty;
    public int BenchmarkScore { get; set; }
    public int CappedBenchmarkScore { get; set; }
    public int BurstableBaseline { get; set; }

    /// <summary>
    /// Single source of truth: How many points this node provides per physical core
    /// Formula: CappedBenchmarkScore / BurstableBaseline
    /// </summary>
    public double PointsPerCore { get; set; }

    /// <summary>
    /// Performance multiplier before capping
    /// </summary>
    public double PerformanceMultiplier { get; set; }

    /// <summary>
    /// Performance multiplier after capping (same as PointsPerCore)
    /// </summary>
    public double CappedPerformanceMultiplier { get; set; }

    public bool IsAcceptable { get; set; }
    public string? RejectionReason { get; set; }

    public List<QualityTier> EligibleTiers { get; set; } = new();
    public QualityTier? HighestTier { get; set; }
    public PerformanceClass PerformanceClass { get; set; }
    /// <summary>
    /// Tier capabilities - stored as array in MongoDB to avoid enum key serialization issues
    /// </summary>
    [BsonDictionaryOptions(MongoDB.Bson.Serialization.Options.DictionaryRepresentation.ArrayOfArrays)]
    public Dictionary<QualityTier, TierCapability> TierCapabilities { get; set; } = new();
}

/// <summary>
/// Capability for a specific tier
/// </summary>
public class TierCapability
{
    public QualityTier Tier { get; set; }
    public bool IsEligible { get; set; }

    /// <summary>
    /// Minimum benchmark score required for this tier
    /// </summary>
    public int MinimumBenchmark { get; set; }

    /// <summary>
    /// Points required per vCPU for this tier
    /// Formula: TierMinimumBenchmark / BurstableBaseline
    /// </summary>
    public double RequiredPointsPerVCpu { get; set; }

    /// <summary>
    /// Points this node provides per core
    /// Formula: NodeBenchmark / BurstableBaseline
    /// </summary>
    public double NodePointsPerCore { get; set; }

    /// <summary>
    /// Maximum vCPUs this node can provide per physical core for this tier
    /// Formula: NodePointsPerCore / RequiredPointsPerVCpu
    /// </summary>
    public int MaxVCpusPerCore { get; set; }

    public decimal PriceMultiplier { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? IneligibilityReason { get; set; }
}

/// <summary>
/// Performance classification
/// </summary>
public enum PerformanceClass
{
    BelowMinimum,      // < Burstable requirement - Rejected
    Budget,            // Burstable only
    MidRange,          // Up to Balanced
    HighEnd,           // Up to Standard
    UltraHighEnd       // All tiers including Guaranteed
}