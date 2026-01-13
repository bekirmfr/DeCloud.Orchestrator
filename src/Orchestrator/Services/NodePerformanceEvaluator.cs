using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Background;

/// <summary>
/// Evaluates node performance and determines tier eligibility
/// Uses database-backed configuration from SchedulingConfigService
/// </summary>
public class NodePerformanceEvaluator
{
    private readonly ILogger<NodePerformanceEvaluator> _logger;
    private readonly ISchedulingConfigService _configService;

    public NodePerformanceEvaluator(
        ILogger<NodePerformanceEvaluator> logger,
        ISchedulingConfigService configService)
    {
        _logger = logger;
        _configService = configService;
    }

    /// <summary>
    /// Evaluate node performance and calculate tier eligibility
    /// Now uses dynamic configuration from database
    /// </summary>
    public async Task<NodePerformanceEvaluation> EvaluateNodeAsync(
        Node node,
        CancellationToken ct = default)
    {
        // Load current configuration
        var config = await _configService.GetConfigAsync(ct);

        var benchmarkScore = node.HardwareInventory.Cpu.BenchmarkScore;
        var cpuModel = node.HardwareInventory.Cpu.Model;
        var burstableBaseline = config.BaselineBenchmark;

        // Apply performance cap if configured
        var cappedScore = Math.Min(
            benchmarkScore,
            (int)(burstableBaseline * config.MaxPerformanceMultiplier));

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

        // Get baseline overcommit ratio from Burstable tier
        var baselineOvercommitRatio = config.Tiers[QualityTier.Burstable].CpuOvercommitRatio;

        // Check eligibility for each tier (from highest to lowest)
        foreach (var (tier, tierConfig) in config.Tiers
            .OrderByDescending(kvp => kvp.Value.MinimumBenchmark))
        {
            var requiredPointsPerVCpu = tierConfig.GetPointsPerVCpu(
                config.BaselineBenchmark,
                baselineOvercommitRatio);

            // Simple comparison: can this node provide enough points?
            var isEligible = pointsPerCore >= requiredPointsPerVCpu;

            if (isEligible)
            {
                evaluation.EligibleTiers.Add(tier);

                evaluation.TierCapabilities[tier] = new TierCapability
                {
                    Tier = tier,
                    MinimumBenchmark = tierConfig.MinimumBenchmark,
                    RequiredPointsPerVCpu = requiredPointsPerVCpu,
                    NodePointsPerCore = pointsPerCore,
                    MaxVCpusPerCore = (int)(pointsPerCore / requiredPointsPerVCpu),
                    PriceMultiplier = tierConfig.PriceMultiplier,
                    Description = tierConfig.Description,
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
                    MinimumBenchmark = tierConfig.MinimumBenchmark,
                    RequiredPointsPerVCpu = requiredPointsPerVCpu,
                    NodePointsPerCore = pointsPerCore,
                    PriceMultiplier = tierConfig.PriceMultiplier,
                    IsEligible = false,
                    IneligibilityReason = $"Node provides {pointsPerCore:F2} points/core, " +
                                         $"tier requires {requiredPointsPerVCpu:F2} points/vCPU " +
                                         $"(gap: {gap:F2} points, {percentageGap:F1}% below minimum)"
                };
            }
        }

        // Determine overall acceptability
        if (cappedScore < config.Tiers[QualityTier.Burstable].MinimumBenchmark)
        {
            evaluation.IsAcceptable = false;
            evaluation.RejectionReason = $"Benchmark score {cappedScore} below minimum " +
                                         $"acceptable performance ({config.Tiers[QualityTier.Burstable].MinimumBenchmark})";
        }
        else
        {
            evaluation.IsAcceptable = true;
            evaluation.HighestTier = evaluation.EligibleTiers.FirstOrDefault();
        }

        LogEvaluationResult(evaluation);

        return evaluation;
    }

    private void LogEvaluationResult(NodePerformanceEvaluation evaluation)
    {
        _logger.LogInformation(
            "═══════════════════════════════════════════════════════════");
        _logger.LogInformation(
            "Node Performance Evaluation: {NodeId}",
            evaluation.NodeId);
        _logger.LogInformation(
            "═══════════════════════════════════════════════════════════");

        _logger.LogInformation("CPU Model:        {Model}", evaluation.CpuModel);
        _logger.LogInformation("Benchmark:        {Score} (capped: {Capped})",
            evaluation.BenchmarkScore, evaluation.CappedBenchmarkScore);
        _logger.LogInformation("Baseline:         {Baseline}", evaluation.BurstableBaseline);
        _logger.LogInformation("Points/Core:      {Points:F2}",
            evaluation.PointsPerCore);
        _logger.LogInformation("Multiplier:       {Multiplier:F2}x (raw), {Capped:F2}x (capped)",
            evaluation.PerformanceMultiplier, evaluation.CappedPerformanceMultiplier);
        _logger.LogInformation("Status:           {Status}",
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
    public Dictionary<QualityTier, TierCapability> TierCapabilities { get; set; } = new();
}

/// <summary>
/// Capability information for a specific tier
/// </summary>
public class TierCapability
{
    public QualityTier Tier { get; set; }
    public int MinimumBenchmark { get; set; }
    public double RequiredPointsPerVCpu { get; set; }
    public double NodePointsPerCore { get; set; }
    public int MaxVCpusPerCore { get; set; }
    public decimal PriceMultiplier { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsEligible { get; set; }
    public string? IneligibilityReason { get; set; }
}