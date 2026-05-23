using Orchestrator.Interfaces.VmScheduling;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services.VmScheduling;

namespace Orchestrator.Services;

/// <summary>
/// VM scheduling service. All hard-filter logic lives in
/// <see cref="ApplyHardFiltersAsync"/>; all scheduling requirements
/// are expressed via <c>spec.Constraints</c> (FILTER 10).
///
/// Soft locality preferences are neutral (0.5) until a soft-preference
/// mechanism is designed. Hard locality requirements (region, zone,
/// country, jurisdiction) are expressed as constraints.
/// </summary>
public class VmSchedulingService : IVmSchedulingService
{
    private readonly DataStore _dataStore;
    private readonly ISchedulingConfigService _configService;
    private readonly IConstraintEvaluator _constraintEvaluator;
    private readonly NodeCapacityCalculator _capacityCalculator;
    private readonly ILogger<VmSchedulingService> _logger;

    public VmSchedulingService(
        DataStore dataStore,
        ISchedulingConfigService configService,
        IConstraintEvaluator constraintEvaluator,
        NodeCapacityCalculator capacityCalculator,
        ILogger<VmSchedulingService> logger)
    {
        _dataStore = dataStore;
        _configService = configService;
        _constraintEvaluator = constraintEvaluator;
        _capacityCalculator = capacityCalculator;
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
            CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Selecting node for VM: {VCpus} vCPUs, {MemoryMB}MB RAM, {DiskGB}GB, " +
            "tier: {Tier}, constraints: {ConstraintCount}",
            spec.VirtualCpuCores,
            spec.MemoryBytes / 1024 / 1024,
            spec.DiskBytes / 1024 / 1024 / 1024,
            tier,
            spec.Constraints?.Count ?? 0);

        var scoredNodes = await GetScoredNodesForVmAsync(spec, tier, ct);

        var eligibleNodes = scoredNodes
            .Where(sn => sn.RejectionReason == null)
            .ToList();

        if (!eligibleNodes.Any())
        {
            _logger.LogWarning(
                "No eligible nodes found for VM (tier: {Tier}, constraints: {ConstraintCount})",
                tier, spec.Constraints?.Count ?? 0);
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
            bestNode.Node.Locality.Region,
            bestNode.Node.Locality.Zone,
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
            CancellationToken ct = default)
    {
        var scoredNodes = await GetScoredNodesForVmAsync(spec, tier, ct);

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
        CancellationToken ct = default)
    {
        var allNodes = await _dataStore.GetAllNodesAsync(NodeStatus.Online);

        _logger.LogInformation("Scoring {Count} online nodes", allNodes.Count);

        var scoredNodes = new List<ScoredNode>();

        foreach (var node in allNodes)
        {
            var scored = await ScoreNodeForVmAsync(node, spec, tier, ct);

            _logger.LogInformation(
                "Node {NodeId} ({Architecture}, {Region}/{Zone}) - " +
                "Score: {TotalScore:F2}, Capacity: {CapacityScore:F2}, " +
                "Load: {LoadScore:F2}, Reputation: {ReputationScore:F2}, " +
                "Locality: {LocalityScore:F2}, Rejection: {RejectionReason}",
                node.Id,
                node.Architecture,
                node.Locality.Region,
                node.Locality.Zone,
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

            var rejectionReason = await ApplyHardFiltersAsync(
                node, spec, tier, tierConfig, config, ct);

            if (rejectionReason != null)
            {
                _logger.LogInformation("Node {NodeId} rejected: {Reason}", node.Id, rejectionReason);
                scored.RejectionReason = rejectionReason;
                scored.TotalScore = 0;
                return scored;
            }

            // =====================================================
            // STEP 2: CALCULATE RESOURCE AVAILABILITY
            // =====================================================

            var availability = await CalculateResourceAvailabilityAsync(node, tier, ct);
            scored.Availability = availability;

            // Calculate compute point cost for VM
            var pointCost = spec.VirtualCpuCores *
                (int)tierConfig.GetPointsPerVCpu(config.BaselineBenchmark, config.BaselineOvercommitRatio);

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

            scored.Scores = CalculateNodeScores(node, availability, spec, config.Weights);
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

    /// <inheritdoc />
    public async Task<string?> ValidateNodeForVmAsync(
            Node node,
            VmSpec spec,
            QualityTier tier,
            CancellationToken ct = default)
    {
        var config = await _configService.GetConfigAsync(ct);

        if (!config.Tiers.TryGetValue(tier, out var tierConfig))
            return $"No tier configuration found for tier {tier}";

        return await ApplyHardFiltersAsync(node, spec, tier, tierConfig, config, ct);
    }

    // ============================================================================
    // PRIVATE - Hard Filters
    // ============================================================================

    /// <summary>
    /// Apply hard filters - return rejection reason if any filter fails
    ///
    /// SECURITY: Architecture validation prevents incompatible VM deployment
    /// LOCALITY: Region/zone filtering ensures geographic constraints are met
    /// </summary>
    private async Task<string?> ApplyHardFiltersAsync(
            Node node,
            VmSpec spec,
            QualityTier tier,
            TierConfiguration tierConfig,
            SchedulingConfig config,
            CancellationToken ct = default)
    {
        // =====================================================
        // FILTER 1: Node Status
        // =====================================================
        if (node.Status != NodeStatus.Online)
            return $"Node not online (status: {node.Status})";

        // =====================================================
        // FILTER 1.5: Scheduling Readiness
        //
        // Operator-controlled pause. A logged-out node is online and
        // heartbeating but has explicitly opted out of new VM placement.
        // This is the natural boundary for the logout → configure →
        // register → login settings-change flow.
        // =====================================================
        if (!node.SchedulingReady)
            return "Node not scheduling-ready (operator logged out)";

        // =====================================================
        // FILTER 2: Tier Eligibility
        // =====================================================
        var evaluation = node.PerformanceEvaluation;
        if (evaluation == null)
            return "Node has no performance evaluation";

        if (!evaluation.EligibleTiers.Contains(tier))
            return $"Node not eligible for tier {tier} " +
                   $"(eligible: [{string.Join(", ", evaluation.EligibleTiers)}])";

        // FILTER 3 (architecture) and FILTER 4 (locality, reputation) have
        // been fully removed. All such requirements are expressed in
        // spec.Constraints and evaluated in FILTER 10 below.

        // =====================================================
        // FILTER 5: GPU Mode Requirement
        // =====================================================
        if (spec.GpuMode == GpuMode.Passthrough)
        {
            // Passthrough requires IOMMU-enabled node with an available GPU for VFIO
            if (!node.HardwareInventory.SupportsGpu || node.HardwareInventory.GpuCount == 0)
                return "VM requires GPU passthrough but node has no GPU";

            if (!node.HardwareInventory.HasIommuCapableGpu)
                return "VM requires GPU passthrough but node has no IOMMU-capable GPU";

            if (!node.HardwareInventory.HasPassthroughCapableGpu)
                return "VM requires GPU passthrough but no GPU is available for VFIO passthrough";
        }
        else if (spec.GpuMode == GpuMode.Proxied)
        {
            // Proxied mode requires any node with a GPU (no IOMMU needed)
            if (!node.HardwareInventory.SupportsGpu || node.HardwareInventory.GpuCount == 0)
                return "VM requires proxied GPU but node has no GPU";
        }
        // GpuMode.None — no GPU filter applied

        // =====================================================
        // FILTER 6: Load Average
        // =====================================================
        if (node.LatestMetrics?.LoadAverage > config.Limits.MaxLoadAverage)
            return $"Load too high ({node.LatestMetrics.LoadAverage:F2} > {config.Limits.MaxLoadAverage})";

        // =====================================================
        // FILTER 7: Minimum Free Memory
        // =====================================================
        var freeMemoryMb = (node.TotalResources.MemoryBytes - node.ReservedResources.MemoryBytes - node.UsedResources.MemoryBytes) / (1024 * 1024);
        if (freeMemoryMb < config.Limits.MinFreeMemoryMb)
            return $"Insufficient free memory ({freeMemoryMb}MB < {config.Limits.MinFreeMemoryMb}MB)";

        // =====================================================
        // FILTER 8: All node obligations must be Active
        //
        // A node that hasn't completed its platform obligations (DHT, Relay,
        // BlockStore, Ingress) is not fully onboarded and must not host user VMs.
        // Obligations are seeded at registration and converged to Active by
        // SystemVmReconciliationService. Any obligation still Pending, Deploying,
        // or Failed means the node's infrastructure is not ready.
        //
        // This supersedes the previous per-obligation BlockStore check:
        // SystemVmReconciliationService sets BlockStoreInfo.Status = Active
        // atomically when the BlockStore obligation becomes Active, so an
        // all-obligations-Active check implies BlockStore is Active too.
        //
        // Nodes with no obligations (hardware below DHT/Relay/BlockStore thresholds)
        // have an empty list and pass this filter — they have nothing to fulfill.
        // =====================================================
        var unmetObligation = node.SystemVmObligations
            .FirstOrDefault(o => o.Status != SystemVmStatus.Active);

        if (unmetObligation != null)
            return $"Node has unmet {unmetObligation.Role} obligation " +
                   $"({unmetObligation.Status}) — node infrastructure not ready";

        // =====================================================
        // FILTER 8.1: Active Block Store required for replicated VMs
        //
        // When replicationFactor > 0, the node must have an Active BlockStore VM.
        // Without it, the lazysync daemon on this node has nowhere to push dirty
        // overlay blocks — replication cannot function.
        //
        // Ephemeral VMs (replicationFactor == 0) bypass this filter: they
        // intentionally accept data loss on node failure and can run anywhere.
        // =====================================================
        if (spec.ReplicationFactor > 0)
        {
            var blockStoreStatus = node.BlockStoreInfo?.Status;
            if (blockStoreStatus != BlockStoreStatus.Active)
            {
                return blockStoreStatus == null
                    ? "VM requires replication (ReplicationFactor > 0) but node has no Block Store VM"
                    : $"VM requires replication but node Block Store is {blockStoreStatus} (not Active)";
            }
        }

        // =====================================================
        // FILTER 9: KVM Required for User VMs
        //
        // Nodes without KVM run QEMU TCG (software emulation) — 10-50x slower
        // than native KVM. User workloads must not
        // be scheduled on non-KVM nodes.
        // TO-DO: In the future, we can consider allowing low-tier, non-latency-sensitive VMs to run on non-KVM light nodes with a warning about performance implications. For now, we enforce KVM for all user VMs.
        // =====================================================
        if (!node.HardwareInventory.KvmAvailable)
            return "Node does not support KVM hardware virtualization. " +
                   "QEMU TCG software emulation is not suitable for any VM workload.";

        // =====================================================
        // FILTER 10: Tenant-supplied constraints (Phase B)
        //
        // Evaluated through the unified IConstraintEvaluator. Each
        // constraint runs in order; first failure short-circuits with a
        // structured rejection naming the failing constraint's index.
        //
        // Constraints are validated at VM creation time
        // (VmService.CreateVmAsync), so malformed entries should never
        // reach this point. The evaluator's belt-and-suspenders validation
        // catches anything that does and rejects with a clear message.
        //
        // See docs/SCHEDULING.md §7 for the constraint vocabulary and
        // design rationale.
        // =====================================================
        if (spec.Constraints is { Count: > 0 })
        {
            for (var i = 0; i < spec.Constraints.Count; i++)
            {
                var result = _constraintEvaluator.Evaluate(spec.Constraints[i], node);
                if (!result.Passed)
                {
                    return $"Constraint #{i} failed: {result.RejectionReason}";
                }
            }

            _logger.LogDebug(
                "Node {NodeId} passed all {Count} tenant constraints",
                node.Id, spec.Constraints.Count);
        }

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
        var tierCapacity = await _capacityCalculator.CalculateTierCapacityAsync(node, tier, ct);

        return new NodeResourceAvailability
        {
            NodeId = node.Id,
            Tier = tier,
            TotalComputePoints = tierCapacity.TierComputePoints,
            TotalMemoryBytes = tierCapacity.TierMemoryBytes,
            TotalStorageBytes = tierCapacity.TierStorageBytes,
            // Used (heartbeat ground truth) + Reserved (transient scheduling holds)
            // See docs/RESOURCE-ALLOCATION.md §8.4
            AllocatedComputePoints = node.UsedResources.ComputePoints + node.ReservedResources.ComputePoints,
            AllocatedMemoryBytes = node.UsedResources.MemoryBytes + node.ReservedResources.MemoryBytes,
            AllocatedStorageBytes = node.UsedResources.StorageBytes + node.ReservedResources.StorageBytes
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
        // Based on uptime and successful VM completions.
        // Single source of truth: NodeReputation.Compute.
        // =====================================================
        var reputationScore = NodeReputation.Compute(node);

        // =====================================================
        // LOCALITY SCORE (0.0 - 1.0)
        // Neutral (0.5) — soft locality preferences are deferred.
        // Hard region/zone/country/jurisdiction requirements are
        // expressed as constraints in spec.Constraints (FILTER 10).
        // =====================================================
        const double localityScore = 0.5;

        return new NodeScores
        {
            CapacityScore = capacityScore,
            LoadScore = loadScore,
            ReputationScore = reputationScore,
            LocalityScore = localityScore
        };
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
}