using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Options;
using Orchestrator.Models;
using Orchestrator.Services.VmScheduling;

namespace Orchestrator.Services;

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
        HardwareInventory inventory,
        CancellationToken ct = default)
    {
        // Load current configuration
        var config = await _configService.GetConfigAsync(ct);

        var benchmarkScore = inventory.Cpu.BenchmarkScore;
        var cpuModel = inventory.Cpu.Model;
        var baselineBenchmark = config.BaselineBenchmark;

        // Apply performance cap if configured
        var cappedScore = Math.Min(
            benchmarkScore,
            (int)(baselineBenchmark * config.MaxPerformanceMultiplier));

        // Single source of truth: points per core
        var pointsPerCore = (double)cappedScore / baselineBenchmark;
        var totalPoints = (int) (pointsPerCore * inventory.Cpu.PhysicalCores * config.BaselineOvercommitRatio); 

        var evaluation = new NodePerformanceEvaluation
        {
            NodeId = inventory.NodeId,
            CpuModel = cpuModel,
            BenchmarkScore = benchmarkScore,
            CappedBenchmarkScore = cappedScore,
            PhysicalCores = inventory.Cpu.PhysicalCores,
            BaselineBenchmark = baselineBenchmark,
            PointsPerCore = pointsPerCore,
            TotalComputePoints = totalPoints,
            PerformanceMultiplier = (double)benchmarkScore / baselineBenchmark,
            CappedPerformanceMultiplier = pointsPerCore,
            EligibleTiers = new List<QualityTier>(),
            TierCapabilities = new List<TierCapability>()
        };

        // Get baseline overcommit ratio from Burstable tier
        var baselineOvercommitRatio = config.Tiers[QualityTier.Burstable].CpuOvercommitRatio;

        // Check eligibility for each tier (from highest to lowest)
        foreach (var (tier, tierConfig) in config.Tiers
            .OrderByDescending(kvp => kvp.Value.MinimumBenchmark))
        {
            // Performance-weighted capacity cost of one vCPU at this tier.
            // Units: same as totalPoints (performance-weighted capacity).
            var requiredPointsPerVCpu = tierConfig.GetPointsPerVCpu(
                config.BaselineBenchmark,
                baselineOvercommitRatio);

            // Gate 1 — Performance: is this node's CPU fast enough for the tier?
            // Compares benchmark scores — same units, apples to apples.
            var meetsPerformance = benchmarkScore >= tierConfig.MinimumBenchmark;

            // Gate 2 — Capacity: does this node have enough total performance-weighted
            // capacity to host at least one vCPU of this tier?
            // Compares totalPoints (physicalCores × benchmarkScore/baseline) against
            // the tier's per-vCPU cost in the same unit — apples to apples.
            var meetsCapacity = evaluation.TotalComputePoints >= requiredPointsPerVCpu;

            var isEligible = meetsPerformance && meetsCapacity;

            if (isEligible)
            {
                evaluation.EligibleTiers.Add(tier);

                evaluation.TierCapabilities.Add(new TierCapability
                {
                    Tier = tier,
                    MinimumBenchmark = tierConfig.MinimumBenchmark,
                    RequiredPointsPerVCpu = requiredPointsPerVCpu,
                    NodePointsPerCore = pointsPerCore,
                    MaxVCpus = (int)(evaluation.TotalComputePoints / requiredPointsPerVCpu),
                    PriceMultiplier = tierConfig.PriceMultiplier,
                    Description = tierConfig.Description,
                    IsEligible = true
                });
            }
            else
            {
                // Report which gate(s) failed with correct units for each.
                var reasons = new List<string>();

                if (!meetsPerformance)
                    reasons.Add(
                        $"benchmark {benchmarkScore} below tier minimum {tierConfig.MinimumBenchmark}");

                if (!meetsCapacity)
                {
                    var capacityGap = requiredPointsPerVCpu - evaluation.TotalComputePoints;
                    var capacityGapPct = (capacityGap / requiredPointsPerVCpu) * 100;
                    reasons.Add(
                        $"node total {evaluation.TotalComputePoints:F0} pts below {requiredPointsPerVCpu:F2} pts required for 1 vCPU " +
                        $"(short by {capacityGap:F2} pts, {capacityGapPct:F1}%)");
                }

                evaluation.TierCapabilities.Add(new TierCapability
                {
                    Tier = tier,
                    MinimumBenchmark = tierConfig.MinimumBenchmark,
                    RequiredPointsPerVCpu = requiredPointsPerVCpu,
                    NodePointsPerCore = pointsPerCore,
                    PriceMultiplier = tierConfig.PriceMultiplier,
                    IsEligible = false,
                    IneligibilityReason = string.Join("; ", reasons)
                });
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
        _logger.LogInformation("Baseline:         {Baseline}", evaluation.BaselineBenchmark);
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

        foreach (var capability in evaluation.TierCapabilities
            .OrderByDescending(c => c.MinimumBenchmark))
        {
            if (capability.IsEligible)
            {
                _logger.LogInformation(
                    "  ✓ {Tier,-12} | {Points:F2} pts/vCPU | Max {MaxVCpus} vCPUs | ${Price}x pricing",
                    capability.Tier,
                    capability.RequiredPointsPerVCpu,
                    capability.MaxVCpus,
                    capability.PriceMultiplier);
            }
            else
            {
                _logger.LogDebug(
                    "  ✗ {Tier,-12} | {Reason}",
                    capability.Tier,
                    capability.IneligibilityReason);
            }
        }

        _logger.LogInformation(
            "═══════════════════════════════════════════════════════════");
    }
}