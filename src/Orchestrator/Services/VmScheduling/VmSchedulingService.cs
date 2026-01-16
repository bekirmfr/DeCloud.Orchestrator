using Orchestrator.Background;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services.VmScheduling;

namespace Orchestrator.Services;

/// <summary>
/// Interface for VM scheduling service
/// </summary>
public interface IVmSchedulingService
{
    /// <summary>
    /// Select the best node for a VM with optional region/zone preferences
    /// </summary>
    Task<Node?> SelectBestNodeForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        string? preferredRegion = null,
        string? preferredZone = null,
        string? requiredArchitecture = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get all available nodes for a VM with filtering options
    /// </summary>
    Task<List<Node>> GetAvailableNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        string? regionFilter = null,
        string? zoneFilter = null,
        string? architectureFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get scored nodes for a VM with detailed scoring information
    /// </summary>
    Task<List<ScoredNode>> GetScoredNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        string? preferredRegion = null,
        string? preferredZone = null,
        string? requiredArchitecture = null,
        CancellationToken ct = default);
}

/// <summary>
/// VM Scheduling Service with architecture awareness and locality optimization
/// 
/// SECURITY: Strict architecture validation prevents incompatible VM deployment
/// PERFORMANCE: Multi-dimensional scoring ensures optimal node selection
/// RELIABILITY: Comprehensive filtering prevents resource exhaustion
/// </summary>
public class VmSchedulingService : IVmSchedulingService
{
    private readonly DataStore _dataStore;
    private readonly ISchedulingConfigService _configService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<VmSchedulingService> _logger;

    public VmSchedulingService(
        DataStore dataStore,
        ISchedulingConfigService configService,
        ILoggerFactory loggerFactory,
        ILogger<VmSchedulingService> logger)
    {
        _dataStore = dataStore;
        _configService = configService;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    // ============================================================================
    // PUBLIC API - Node Selection
    // ============================================================================

    /// <summary>
    /// Select best node for VM with optional preferences
    /// </summary>
    public async Task<Node?> SelectBestNodeForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        string? preferredRegion = null,
        string? preferredZone = null,
        string? requiredArchitecture = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Selecting node for VM: {VCpus} vCPUs, {MemoryMB}MB RAM, {DiskGB}GB, " +
            "tier: {Tier}, arch: {Architecture}, region: {Region}, zone: {Zone}",
            spec.VirtualCpuCores,
            spec.MemoryBytes / 1024 / 1024,
            spec.DiskBytes / 1024 / 1024 / 1024,
            tier,
            requiredArchitecture ?? "any",
            preferredRegion ?? "any",
            preferredZone ?? "any");

        var scoredNodes = await GetScoredNodesForVmAsync(
            spec, tier, preferredRegion, preferredZone, requiredArchitecture, ct);

        var eligibleNodes = scoredNodes
            .Where(sn => sn.RejectionReason == null)
            .ToList();

        if (!eligibleNodes.Any())
        {
            _logger.LogWarning(
                "No eligible nodes found for VM (tier: {Tier}, arch: {Architecture})",
                tier, requiredArchitecture ?? "any");
            return null;
        }

        var bestNode = eligibleNodes
            .OrderByDescending(sn => sn.TotalScore)
            .First();

        _logger.LogInformation(
            "Selected node {NodeId} ({Architecture}, {Region}/{Zone}) " +
            "with score {Score:F2} for {Tier} tier VM",
            bestNode.Node.Id,
            bestNode.Node.Architecture,
            bestNode.Node.Region,
            bestNode.Node.Zone,
            bestNode.TotalScore,
            tier);

        return bestNode.Node;
    }

    /// <summary>
    /// Get all available nodes for VM with filtering
    /// </summary>
    public async Task<List<Node>> GetAvailableNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        string? regionFilter = null,
        string? zoneFilter = null,
        string? architectureFilter = null,
        CancellationToken ct = default)
    {
        var scoredNodes = await GetScoredNodesForVmAsync(
            spec, tier, regionFilter, zoneFilter, architectureFilter, ct);

        return scoredNodes
            .Where(sn => sn.RejectionReason == null)
            .Select(sn => sn.Node)
            .ToList();
    }

    /// <summary>
    /// Get scored nodes with detailed information
    /// </summary>
    public async Task<List<ScoredNode>> GetScoredNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        string? preferredRegion = null,
        string? preferredZone = null,
        string? requiredArchitecture = null,
        CancellationToken ct = default)
    {
        var allNodes = _dataStore.Nodes.Values
            .Where(n => n.Status == NodeStatus.Online)
            .ToList();

        _logger.LogInformation("Scoring {Count} online nodes", allNodes.Count);

        var scoredNodes = new List<ScoredNode>();

        foreach (var node in allNodes)
        {
            var scored = await ScoreNodeForVmAsync(
                node, spec, tier, preferredRegion, preferredZone, requiredArchitecture, ct);

            _logger.LogDebug(
                "Node {NodeId} ({Architecture}, {Region}/{Zone}) - " +
                "Score: {TotalScore:F2}, Capacity: {CapacityScore:F2}, " +
                "Load: {LoadScore:F2}, Reputation: {ReputationScore:F2}, " +
                "Locality: {LocalityScore:F2}, Rejection: {RejectionReason}",
                node.Id,
                node.Architecture,
                node.Region,
                node.Zone,
                scored.TotalScore,
                scored.Scores.CapacityScore,
                scored.Scores.LoadScore,
                scored.Scores.ReputationScore,
                scored.Scores.LocalityScore,
                scored.RejectionReason ?? "None");

            scoredNodes.Add(scored);
        }

        return scoredNodes
            .OrderByDescending(sn => sn.TotalScore)
            .ToList();
    }

    // ============================================================================
    // PRIVATE - Core Scoring Logic
    // ============================================================================

    /// <summary>
    /// Score a node for VM placement with all filters and scoring
    /// </summary>
    private async Task<ScoredNode> ScoreNodeForVmAsync(
        Node node,
        VmSpec spec,
        QualityTier tier,
        string? preferredRegion,
        string? preferredZone,
        string? requiredArchitecture,
        CancellationToken ct = default)
    {
        try
        {
            var scored = new ScoredNode { Node = node };

            // Load configuration
            var config = await _configService.GetConfigAsync(ct);
            var tierConfig = config.Tiers[tier];

            // =====================================================
            // STEP 1: HARD FILTERS (Must pass or node rejected)
            // =====================================================

            var rejection = await ApplyHardFiltersAsync(
                node, spec, tier, tierConfig, config, requiredArchitecture, ct);

            if (rejection != null)
            {
                _logger.LogDebug("Node {NodeId} rejected: {Reason}", node.Id, rejection);
                scored.RejectionReason = rejection;
                scored.TotalScore = 0;
                return scored;
            }

            // =====================================================
            // STEP 2: CALCULATE RESOURCE AVAILABILITY
            // =====================================================

            var availability = await CalculateResourceAvailabilityAsync(node, tier, ct);
            scored.Availability = availability;

            // Calculate compute point cost for VM
            var baselineOvercommit = config.Tiers[QualityTier.Burstable].CpuOvercommitRatio;
            var pointCost = spec.VirtualCpuCores *
                (int)tierConfig.GetPointsPerVCpu(config.BaselineBenchmark, baselineOvercommit);

            availability.RequiredComputePoints = pointCost;

            // =====================================================
            // STEP 3: VERIFY VM FITS AFTER OVERCOMMIT
            // =====================================================

            if (!availability.CanFit(spec))
            {
                var memoryMb = spec.MemoryBytes / (1024 * 1024);
                var diskGb = spec.DiskBytes / (1024 * 1024 * 1024);

                scored.RejectionReason = $"Insufficient resources after overcommit " +
                    $"(Points: {availability.RemainingComputePoints}/{pointCost}, " +
                    $"MEM: {availability.RemainingMemoryBytes / (1024 * 1024):F2}/{memoryMb}MB, " +
                    $"DISK: {availability.RemainingStorageBytes / (1024 * 1024 * 1024):F2}/{diskGb}GB)";
                scored.TotalScore = 0;
                return scored;
            }

            // =====================================================
            // STEP 4: CHECK UTILIZATION SAFETY THRESHOLDS
            // =====================================================

            var projectedCpuUtil = availability.ProjectedCpuUtilization(spec);
            var projectedMemUtil = availability.ProjectedMemoryUtilization(spec);

            if (projectedCpuUtil > config.Limits.MaxUtilizationPercent ||
                projectedMemUtil > config.Limits.MaxUtilizationPercent)
            {
                scored.RejectionReason = $"Would exceed max utilization " +
                    $"(CPU: {projectedCpuUtil:F1}%, MEM: {projectedMemUtil:F1}%, " +
                    $"Limit: {config.Limits.MaxUtilizationPercent}%)";
                scored.TotalScore = 0;
                return scored;
            }

            // =====================================================
            // STEP 5: CALCULATE WEIGHTED SCORES
            // =====================================================

            scored.Scores = CalculateNodeScores(
                node, availability, spec, preferredRegion, preferredZone, config.Weights);

            scored.TotalScore = CalculateTotalScore(scored.Scores, config.Weights);

            return scored;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scoring node {NodeId}", node.Id);
            return new ScoredNode
            {
                Node = node,
                RejectionReason = $"Scoring error: {ex.Message}",
                TotalScore = 0
            };
        }
    }

    // ============================================================================
    // PRIVATE - Hard Filters
    // ============================================================================

    /// <summary>
    /// Apply hard filters - return rejection reason if any filter fails
    /// 
    /// SECURITY: Architecture validation prevents incompatible VM deployment
    /// </summary>
    private async Task<string?> ApplyHardFiltersAsync(
        Node node,
        VmSpec spec,
        QualityTier tier,
        TierConfiguration tierConfig,
        SchedulingConfig config,
        string? requiredArchitecture,
        CancellationToken ct = default)
    {
        // =====================================================
        // FILTER 1: Node Status
        // =====================================================
        if (node.Status != NodeStatus.Online)
            return $"Node not online (status: {node.Status})";

        // =====================================================
        // FILTER 2: Tier Eligibility
        // =====================================================
        var evaluation = node.PerformanceEvaluation;
        if (evaluation == null || !evaluation.EligibleTiers.Contains(tier))
            return $"Node not eligible for tier {tier}";

        // =====================================================
        // FILTER 3: ARCHITECTURE COMPATIBILITY (CRITICAL)
        // =====================================================
        if (!string.IsNullOrEmpty(requiredArchitecture))
        {
            var nodeArch = NormalizeArchitecture(node.Architecture);
            var requiredArch = NormalizeArchitecture(requiredArchitecture);

            if (nodeArch != requiredArch)
            {
                return $"Architecture mismatch: Node has {nodeArch}, VM requires {requiredArch}";
            }

            _logger.LogDebug(
                "Node {NodeId} architecture {Architecture} matches requirement {Required}",
                node.Id, node.Architecture, requiredArchitecture);
        }

        // =====================================================
        // FILTER 4: Load Average
        // =====================================================
        if (node.LatestMetrics?.LoadAverage > config.Limits.MaxLoadAverage)
            return $"Load too high ({node.LatestMetrics.LoadAverage:F2} > {config.Limits.MaxLoadAverage})";

        // =====================================================
        // FILTER 5: Minimum Free Memory
        // =====================================================
        var freeMemoryMb = (node.TotalResources.MemoryBytes - node.ReservedResources.MemoryBytes) / (1024 * 1024);
        if (freeMemoryMb < config.Limits.MinFreeMemoryMb)
            return $"Insufficient free memory ({freeMemoryMb}MB < {config.Limits.MinFreeMemoryMb}MB)";

        return null; // All filters passed
    }

    // ============================================================================
    // PRIVATE - Resource Availability Calculation
    // ============================================================================

    /// <summary>
    /// Calculate available resources on node for specific tier
    /// </summary>
    private async Task<NodeResourceAvailability> CalculateResourceAvailabilityAsync(
        Node node,
        QualityTier tier,
        CancellationToken ct = default)
    {
        var capacityLogger = _loggerFactory.CreateLogger<NodeCapacityCalculator>();
        var capacityCalculator = new NodeCapacityCalculator(capacityLogger, _configService);

        var tierCapacity = await capacityCalculator.CalculateTierCapacityAsync(node, tier, ct);

        return new NodeResourceAvailability
        {
            NodeId = node.Id,
            Tier = tier,
            TotalComputePoints = tierCapacity.TierComputePoints,
            TotalMemoryBytes = tierCapacity.TierMemoryBytes,
            TotalStorageBytes = tierCapacity.TierStorageBytes,
            AllocatedComputePoints = node.ReservedResources.ComputePoints,
            AllocatedMemoryBytes = node.ReservedResources.MemoryBytes,
            AllocatedStorageBytes = node.ReservedResources.StorageBytes
        };
    }

    // ============================================================================
    // PRIVATE - Scoring Components
    // ============================================================================

    /// <summary>
    /// Calculate multi-dimensional node scores
    /// </summary>
    private NodeScores CalculateNodeScores(
        Node node,
        NodeResourceAvailability availability,
        VmSpec spec,
        string? preferredRegion,
        string? preferredZone,
        ScoringWeightsConfig weights)
    {
        // =====================================================
        // CAPACITY SCORE (0.0 - 1.0)
        // Higher remaining capacity = better score
        // =====================================================
        var capacityScore = availability.RemainingComputePoints > 0
            ? (double)availability.RemainingComputePoints / availability.TotalComputePoints
            : 0.0;

        // =====================================================
        // LOAD SCORE (0.0 - 1.0)
        // Lower load = better score
        // =====================================================
        var loadScore = node.LatestMetrics?.LoadAverage != null
            ? Math.Max(0, 1.0 - (node.LatestMetrics.LoadAverage / 16.0))
            : 0.5; // Neutral score if metrics unavailable

        // =====================================================
        // REPUTATION SCORE (0.0 - 1.0)
        // Based on uptime and successful VM completions
        // =====================================================
        var uptimeComponent = node.UptimePercentage / 100.0;
        var successComponent = node.TotalVmsHosted > 0
            ? (double)node.SuccessfulVmCompletions / node.TotalVmsHosted
            : 0.5; // Neutral for new nodes

        var reputationScore = (uptimeComponent * 0.7) + (successComponent * 0.3);

        // =====================================================
        // LOCALITY SCORE (0.0 - 1.0)
        // Rewards nodes in preferred region/zone
        // =====================================================
        var localityScore = CalculateLocalityScore(
            node.Region,
            node.Zone,
            preferredRegion,
            preferredZone);

        return new NodeScores
        {
            CapacityScore = capacityScore,
            LoadScore = loadScore,
            ReputationScore = reputationScore,
            LocalityScore = localityScore
        };
    }

    /// <summary>
    /// Calculate locality score based on region/zone matching
    /// 
    /// SCORING RULES:
    /// - Exact region + zone match: 1.0 (perfect)
    /// - Region match, different zone: 0.7 (good)
    /// - Different region, same zone name: 0.4 (ok)
    /// - No preferences specified: 0.5 (neutral)
    /// - No match: 0.3 (acceptable)
    /// </summary>
    private double CalculateLocalityScore(
        string nodeRegion,
        string nodeZone,
        string? preferredRegion,
        string? preferredZone)
    {
        // No preferences = neutral score
        if (string.IsNullOrEmpty(preferredRegion) && string.IsNullOrEmpty(preferredZone))
        {
            return 0.5;
        }

        var regionMatch = !string.IsNullOrEmpty(preferredRegion) &&
                         string.Equals(nodeRegion, preferredRegion, StringComparison.OrdinalIgnoreCase);

        var zoneMatch = !string.IsNullOrEmpty(preferredZone) &&
                       string.Equals(nodeZone, preferredZone, StringComparison.OrdinalIgnoreCase);

        // Perfect match: same region AND zone
        if (regionMatch && zoneMatch)
        {
            _logger.LogDebug(
                "Perfect locality match: {Region}/{Zone}",
                nodeRegion, nodeZone);
            return 1.0;
        }

        // Good match: same region, different/no zone preference
        if (regionMatch)
        {
            _logger.LogDebug(
                "Region match: {Region} (zone: {Zone})",
                nodeRegion, nodeZone);
            return 0.7;
        }

        // No match: acceptable but not preferred
        _logger.LogDebug(
            "No locality match: node is {Region}/{Zone}, preferred is {PreferredRegion}/{PreferredZone}",
            nodeRegion, nodeZone, preferredRegion ?? "any", preferredZone ?? "any");
        return 0.0;
    }

    /// <summary>
    /// Calculate final weighted total score
    /// </summary>
    private double CalculateTotalScore(NodeScores scores, ScoringWeightsConfig weights)
    {
        return (scores.CapacityScore * weights.Capacity) +
               (scores.LoadScore * weights.Load) +
               (scores.ReputationScore * weights.Reputation) +
               (scores.LocalityScore * weights.Locality);
    }

    // ============================================================================
    // PRIVATE - Architecture Helpers
    // ============================================================================

    /// <summary>
    /// Normalize architecture names for consistent comparison
    /// 
    /// SECURITY: Standardizes architecture identifiers to prevent bypass
    /// </summary>
    private string NormalizeArchitecture(string architecture)
    {
        if (string.IsNullOrEmpty(architecture))
            return "x86_64"; // Default for backward compatibility

        return architecture.ToLower() switch
        {
            "x86_64" or "amd64" or "x64" => "x86_64",
            "aarch64" or "arm64" => "aarch64",
            "i686" or "i386" or "x86" => "i686",
            "armv7l" or "armv7" or "arm" => "armv7l",
            _ => architecture.ToLower()
        };
    }
}