using DeCloud.Shared;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orchestrator.Background;

public interface INodeService
{
    Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request);
    Task<NodeHeartbeatResponse> ProcessHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat);
    /// <summary>
    /// Process command acknowledgment from node
    /// </summary>
    Task<bool> ProcessCommandAcknowledgmentAsync(
        string nodeId,
        string commandId,
        CommandAcknowledgment ack);
    Task<Node?> GetNodeAsync(string nodeId);
    Task<List<Node>> GetAllNodesAsync(NodeStatus? statusFilter = null);

    // Enhanced scheduling methods
    Task<List<Node>> GetAvailableNodesForVmAsync(VmSpec spec, QualityTier tier = QualityTier.Standard);
    Task<Node?> SelectBestNodeForVmAsync(VmSpec spec, QualityTier tier = QualityTier.Standard);
    Task<List<ScoredNode>> GetScoredNodesForVmAsync(VmSpec spec, QualityTier tier = QualityTier.Standard);

    Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status);
    Task<bool> RemoveNodeAsync(string nodeId);
    Task CheckNodeHealthAsync();
    /// <summary>
    /// Request node to sign an SSH certificate using its CA
    /// </summary>
    Task<CertificateSignResponse> SignCertificateAsync(
        string nodeId,
        CertificateSignRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Inject SSH public key into a VM's authorized_keys
    /// </summary>
    Task<bool> InjectSshKeyAsync(
        string nodeId,
        string vmId,
        string publicKey,
        string username = "root",
        CancellationToken ct = default);
}

public class NodeService : INodeService
{
    private readonly DataStore _dataStore;
    private readonly IEventService _eventService;
    private readonly ICentralIngressService _ingressService;
    private readonly ILogger<NodeService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HttpClient _httpClient;
    private readonly SchedulingConfiguration _schedulingConfig;
    private readonly IRelayNodeService _relayNodeService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IWireGuardManager _wireGuardManager;

    public NodeService(
        DataStore dataStore,
        IEventService eventService,
        ICentralIngressService ingressService,
        ILogger<NodeService> logger,
        ILoggerFactory loggerFactory,
        HttpClient httpClient,
        IRelayNodeService relayNodeService,
        IServiceProvider serviceProvider,
        IWireGuardManager wireGuardManager,
        SchedulingConfiguration? schedulingConfig = null
        )
    {
        _dataStore = dataStore;
        _eventService = eventService;
        _ingressService = ingressService;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _httpClient = httpClient;
        _relayNodeService = relayNodeService;
        _serviceProvider = serviceProvider;
        _wireGuardManager = wireGuardManager;
        _schedulingConfig = schedulingConfig ?? new SchedulingConfiguration();
        _schedulingConfig.Weights.Validate();
    }

    // ============================================================================
    // SMART SCHEDULING - Core Methods
    // ============================================================================

    public async Task<List<Node>> GetAvailableNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard)
    {
        var scoredNodes = await GetScoredNodesForVmAsync(spec, tier);

        return scoredNodes
            .Where(sn => sn.RejectionReason == null)
            .Select(sn => sn.Node)
            .ToList();
    }

    public async Task<Node?> SelectBestNodeForVmAsync(VmSpec vmSpec, QualityTier tier)
    {
        var candidates = _dataStore.Nodes.Values
            .Where(n => n.Status == NodeStatus.Online)
            .ToList();

        if (!candidates.Any())
        {
            _logger.LogWarning("No online nodes available");
            return null;
        }

        var scoredNodes = new List<ScoredNode>();
        var tierRequirements = _schedulingConfig.TierRequirements[tier];

        foreach (var node in candidates)
        {
            // CRITICAL: Check tier eligibility first
            if (node.PerformanceEvaluation == null ||
                !node.PerformanceEvaluation.EligibleTiers.Contains(tier))
            {
                _logger.LogDebug(
                    "Node {NodeId} rejected: Not eligible for {Tier} tier",
                    node.Id,
                    tier);
                continue;
            }

            var scored = await ScoreNodeForVmAsync(node, vmSpec, tier, tierRequirements);

            if (scored.RejectionReason == null)
            {
                scoredNodes.Add(scored);
            }
        }

        if (!scoredNodes.Any())
        {
            _logger.LogWarning(
                "No eligible nodes found for {Tier} tier",
                tier);
            return null;
        }

        _schedulingConfig.Weights.Validate();

        var bestNode = scoredNodes
            .OrderByDescending(n => n.TotalScore)
            .First();

        _logger.LogInformation(
            "Selected node {NodeId} for {Tier} tier VM (Score: {Score:F2})",
            bestNode.Node.Id,
            tier,
            bestNode.TotalScore);

        return bestNode.Node;
    }

    public async Task<List<ScoredNode>> GetScoredNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard)
    {
        var policy = _schedulingConfig.TierRequirements[tier];
        var allNodes = _dataStore.Nodes.Values.ToList();
        _logger.LogInformation("Scoring {TotalNodesCount} nodes", allNodes.Count);
        var scoredNodes = new List<ScoredNode>();

        foreach (var node in allNodes)
        {
            var scored = await ScoreNodeForVmAsync(node, spec, tier, policy);
            _logger.LogInformation("Scoring result:");
            // Add scroring logs
             _logger.LogInformation("Node {NodeId} - Total Score: {TotalScore:F2}, " +
                 "Capacity: {CapacityScore:F2}, Load: {LoadScore:F2}, " +
                 "Reputation: {ReputationScore:F2}, Locality: {LocalityScore:F2}, " +
                 "Rejection: {RejectionReason}",
                 node.Id,
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
    // SMART SCHEDULING - Scoring & Filtering
    // ============================================================================

    private async Task<ScoredNode> ScoreNodeForVmAsync(
        Node node,
        VmSpec spec,
        QualityTier tier,
        TierRequirements tierRequirements)
    {
        try
        {
            var scored = new ScoredNode { Node = node };

            // Step 1: Hard filters (must pass or node is rejected)
            var rejection = ApplyHardFilters(node, spec, tierRequirements);
            if (rejection != null)
            {
                _logger.LogDebug("Node {NodeId} rejected: {Reason}", node.Id, rejection);
                scored.RejectionReason = rejection;
                scored.TotalScore = 0;
                return scored;
            }

            // Step 2: Calculate resource availability with overcommit
            var availability = CalculateResourceAvailability(node, tier);

            scored.Availability = availability;

            // Calculate point cost for this VMs
            var pointCost = spec.VirtualCpuCores *
                (int)tierRequirements.GetPointsPerVCpu(_schedulingConfig.BurstableBaselineBenchmark);
            availability.RequiredComputePoints = pointCost;

            // Step 3: Check if VM fits after overcommit calculation
            if (!availability.CanFit(spec))
            {
                var memoryMb = spec.MemoryBytes / (1024 * 1024);
                var diskGb = spec.DiskBytes / (1024 * 1024 * 1024);

                scored.RejectionReason = $"Insufficient resources after overcommit " +
                    $"(Points: {availability.RemainingComputePoints}/{pointCost}, " +
                    $"MEM: {availability.RemainingMemoryBytes / (1024 * 1024):F2}/{memoryMb}MB, " +
                    $"DISK: {availability.RemainingStorageBytes / (1024 * 1024 * 1024):F2}/{diskGb}GB)";
                scored.TotalScore = 0;
                _logger.LogDebug("Node {NodeId} rejected: {Reason}", node.Id, scored.RejectionReason);
                return scored;
            }

            // Step 4: Check utilization safety threshold
            var projectedCpuUtil = availability.ProjectedCpuUtilization(spec);
            var projectedMemUtil = availability.ProjectedMemoryUtilization(spec);

            if (projectedCpuUtil > _schedulingConfig.MaxUtilizationPercent ||
                projectedMemUtil > _schedulingConfig.MaxUtilizationPercent)
            {
                scored.RejectionReason = $"Would exceed max utilization " +
                    $"(CPU: {projectedCpuUtil:F1}%, MEM: {projectedMemUtil:F1}% > {_schedulingConfig.MaxUtilizationPercent}%)";
                scored.TotalScore = 0;
                _logger.LogDebug("Node {NodeId} rejected: {Reason}", node.Id, scored.RejectionReason);
                return scored;
            }

            // Step 5: Calculate component scores
            var scores = new NodeScores
            {
                CapacityScore = CalculateCapacityScore(availability, spec),
                LoadScore = CalculateLoadScore(node, availability),
                ReputationScore = CalculateReputationScore(node),
                LocalityScore = CalculateLocalityScore(node, spec)
            };

            scored.Scores = scores;

            // Step 6: Calculate weighted total score
            var weights = _schedulingConfig.Weights;
            scored.TotalScore =
                (scores.CapacityScore * weights.Capacity) +
                (scores.LoadScore * weights.Load) +
                (scores.ReputationScore * weights.Reputation) +
                (scores.LocalityScore * weights.Locality);

            return await Task.FromResult(scored);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to score node {NodeId}", node.Id);
            throw;
        }
        
    }

    private string? ApplyHardFilters(Node node, VmSpec spec, TierRequirements policy)
    {
        // Filter 1: Node must be online
        if (node.Status != NodeStatus.Online)
            return $"Node offline (status: {node.Status})";

        // Filter 2: Must support required image
        if (!string.IsNullOrEmpty(spec.ImageId) &&
            node.SupportedImages != null &&
            !node.SupportedImages.Contains(spec.ImageId))
            return $"Image {spec.ImageId} not supported";

        // Filter 3: GPU requirement
        if (spec.RequiresGpu && !node.SupportsGpu)
            return "GPU required but not available";

        // Filter 4: Minimum total resources check (physical capacity)
        if (node.TotalResources.MemoryBytes < _schedulingConfig.MinFreeMemoryMb)
            return $"Node total memory too low ({node.TotalResources.MemoryBytes}MB < {_schedulingConfig.MinFreeMemoryMb}MB)";

        // Filter 5: Load average check (if metrics available)
        if (node.LatestMetrics?.LoadAverage > _schedulingConfig.MaxLoadAverage)
            return $"Load too high ({node.LatestMetrics.LoadAverage:F2} > {_schedulingConfig.MaxLoadAverage})";

        // Filter 6: Region requirement (if specified)
        if (!string.IsNullOrEmpty(spec.PreferredRegion) &&
            spec.PreferredRegion != null &&
            node.Region != spec.PreferredRegion)
            return $"Region mismatch (required: {spec.PreferredRegion}, node: {node.Region})";

        return null; // Passed all filters
    }

    /// <summary>
    /// This is the source of truth for scheduling decisions
    /// </summary>
    private NodeResourceAvailability CalculateResourceAvailability(
    Node node,
    QualityTier tier)
    {
        // Use NodeCapacityCalculator for capacity
        var capacityLogger = _loggerFactory.CreateLogger<NodeCapacityCalculator>();
        var capacityCalculator = new NodeCapacityCalculator(_schedulingConfig, capacityLogger);
        var totalCapacity = capacityCalculator.CalculateTotalCapacity(node);

        if (!totalCapacity.IsAcceptable)
        {
            return new NodeResourceAvailability
            {
                NodeId = node.Id,
                Tier = tier,
                TotalComputePoints = 0,
                TotalMemoryBytes = 0,
                TotalStorageBytes = 0
            };
        }

        // Get tier-specific capacity
        var tierCapacity = capacityCalculator.CalculateTierCapacity(node, tier);

        if (!tierCapacity.IsEligible)
        {
            return new NodeResourceAvailability
            {
                NodeId = node.Id,
                Tier = tier,
                TotalComputePoints = 0,
                TotalMemoryBytes = 0,
                TotalStorageBytes = 0
            };
        }

        // Use actual reserved resources from node
        var allocatedComputePoints = node.ReservedResources.ComputePoints;
        var allocatedMemoryBytes = node.ReservedResources.MemoryBytes;
        var allocatedStorageBytes = node.ReservedResources.StorageBytes;

        return new NodeResourceAvailability
        {
            NodeId = node.Id,
            Tier = tier,

            TotalComputePoints = tierCapacity.TierComputePoints,
            TotalMemoryBytes = tierCapacity.TierMemoryBytes,
            TotalStorageBytes = tierCapacity.TierStorageBytes,

            AllocatedComputePoints = allocatedComputePoints,
            AllocatedMemoryBytes = allocatedMemoryBytes,
            AllocatedStorageBytes = allocatedStorageBytes,

            RequiredComputePoints = 0 // Set during fit check
        };
    }

    private double CalculateCapacityScore(NodeResourceAvailability availability, VmSpec spec)
    {
        // Calculate how much capacity remains AFTER placing this VM
        var cpuHeadroom = availability.RemainingComputePoints - spec.VirtualCpuCores;
        var memoryHeadroom = availability.RemainingMemoryBytes - spec.MemoryBytes;

        // Normalize to 0-1 range (prefer nodes that will have 50%+ capacity remaining)
        var cpuScore = Math.Min(1.0, cpuHeadroom / (availability.TotalComputePoints * 0.5));
        var memoryScore = Math.Min(1.0, memoryHeadroom / (availability.TotalMemoryBytes * 0.5));

        // Average of CPU and memory scores
        return (cpuScore + memoryScore) / 2.0;
    }

    private double CalculateLoadScore(Node node, NodeResourceAvailability availability)
    {
        // Use current utilization as primary indicator
        var cpuUtil = availability.ComputeUtilization;
        var memoryUtil = availability.MemoryUtilization;

        // Convert utilization to score (inverse relationship)
        var cpuScore = 1.0 - (cpuUtil / 100.0);
        var memoryScore = 1.0 - (memoryUtil / 100.0);

        // Factor in load average if available
        double loadScore = 1.0;
        if (node.LatestMetrics?.LoadAverage != null)
        {
            // Normalize load average (assume 1.0 per core is normal)
            var normalizedLoad = node.LatestMetrics.LoadAverage / node.HardwareInventory.Cpu.PhysicalCores;
            loadScore = Math.Max(0, 1.0 - normalizedLoad);
        }

        // Weighted average: utilization (70%), load average (30%)
        return (cpuScore * 0.35) + (memoryScore * 0.35) + (loadScore * 0.30);
    }

    private double CalculateReputationScore(Node node)
    {
        double score = 0.5; // Start with neutral score

        // Uptime bonus (max +0.3)
        var uptime = DateTime.UtcNow - node.RegisteredAt;
        var uptimeBonus = Math.Min(0.3, uptime.TotalDays / 30.0 * 0.3); // Max after 30 days
        score += uptimeBonus;

        // Recent heartbeat bonus (max +0.2)
        var timeSinceHeartbeat = DateTime.UtcNow - node.LastHeartbeat;
        if (timeSinceHeartbeat.TotalSeconds < 30)
            score += 0.2; // Very recent
        else if (timeSinceHeartbeat.TotalMinutes < 2)
            score += 0.1; // Recent

        return Math.Clamp(score, 0.0, 1.0);
    }

    private double CalculateLocalityScore(Node node, VmSpec spec)
    {
        if (string.IsNullOrEmpty(spec.PreferredRegion))
            return 0.5; // Neutral if no preference

        // Same region and zone
        if (node.Region == spec.PreferredRegion &&
            node.Zone == spec.PreferredZone)
            return 1.0;

        // Same region, different zone
        if (node.Region == spec.PreferredRegion)
            return 0.7;

        // Different region
        return 0.3;
    }

    // ============================================================================
    // Registration and Heartbeat
    // ============================================================================

    // Implements deterministic node ID validation and registration

    public async Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request)
    {
        // =====================================================
        // STEP 1: Validate Wallet Address
        // =====================================================
        if (string.IsNullOrWhiteSpace(request.WalletAddress) ||
            request.WalletAddress == "0x0000000000000000000000000000000000000000")
        {
            _logger.LogError("Node registration rejected: null wallet address");
            throw new ArgumentException(
                "Valid wallet address required for node registration. " +
                "Null address (0x000...000) is not allowed.");
        }

        // =====================================================
        // NEW: STEP 1.5: Verify Wallet Signature
        // =====================================================
        if (!VerifyWalletSignature(request.WalletAddress, request.Message, request.Signature))
        {
            _logger.LogError("Node registration rejected: Invalid wallet signature");
            throw new UnauthorizedAccessException("Invalid wallet signature. Please ensure you're using the correct wallet.");
        }

        // =====================================================
        // STEP 2: Validate Machine ID
        // =====================================================
        if (string.IsNullOrWhiteSpace(request.MachineId))
        {
            _logger.LogError("Node registration rejected: missing machine ID");
            throw new ArgumentException("Machine ID is required for node registration");
        }

        // =====================================================
        // STEP 3: Generate and Validate Node ID
        // =====================================================
        string nodeId;
        try
        {
            nodeId = NodeIdGenerator.GenerateNodeId(request.MachineId, request.WalletAddress);

            _logger.LogInformation(
                "✓ Wallet signature generated for node {NodeId}",
                nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate node ID");
            throw new ArgumentException("Invalid machine ID or wallet address", ex);
        }

        // =====================================================
        // Get orchestrator WireGuard public key if available
        // =====================================================
        string? orchestratorPublicKey = null;

        orchestratorPublicKey = await _wireGuardManager.GetOrchestratorPublicKeyAsync();
        _logger.LogInformation(
            "Including orchestrator WireGuard public key {PublicKey} in registration response for node {NodeId}",
            orchestratorPublicKey, nodeId);

        // =====================================================
        // Generate and save API key
        // =====================================================
        var apiKey = GenerateApiKey();
        var apiKeyHash = GenerateHash(apiKey);

        // =====================================================
        // STEP 4: Check if Node Exists (Re-registration)
        // =====================================================
        var existingNode = _dataStore.Nodes.Values.FirstOrDefault(n => n.Id == nodeId);

        if (existingNode != null)
        {
            // RE-REGISTRATION: Update existing node
            _logger.LogInformation(
                "Node re-registration: {NodeId} (Machine: {MachineId}, Wallet: {Wallet})",
                nodeId, request.MachineId, request.WalletAddress);

            existingNode.Name = request.Name;
            existingNode.PublicIp = request.PublicIp;
            existingNode.AgentPort = request.AgentPort;
            existingNode.HardwareInventory = request.HardwareInventory;
            existingNode.AgentVersion = request.AgentVersion;
            existingNode.SupportedImages = request.SupportedImages;
            existingNode.Region = request.Region ?? "default";
            existingNode.Zone = request.Zone ?? "default";
            existingNode.Status = NodeStatus.Online;
            existingNode.LastHeartbeat = DateTime.UtcNow;

            // Re-evaluate performance (hardware may have changed)
            var reregPerfLogger = _loggerFactory.CreateLogger<NodePerformanceEvaluator>();
            var reregPerfEvaluator = new NodePerformanceEvaluator(_schedulingConfig, reregPerfLogger);

            existingNode.PerformanceEvaluation = reregPerfEvaluator.EvaluateNode(existingNode);

            if (!existingNode.PerformanceEvaluation.IsAcceptable)
            {
                _logger.LogWarning(
                    "Node {NodeId} rejected during re-registration: {Reason}",
                    nodeId,
                    existingNode.PerformanceEvaluation.RejectionReason);

                throw new InvalidOperationException(
                    $"Node performance below minimum requirements: {existingNode.PerformanceEvaluation.RejectionReason}");
            }

            // Recalculate total capacity
            var reregCapLogger = _loggerFactory.CreateLogger<NodeCapacityCalculator>();
            var reregCapCalculator = new NodeCapacityCalculator(_schedulingConfig, reregCapLogger);
            var reregTotalCapacity = reregCapCalculator.CalculateTotalCapacity(existingNode);

            existingNode.TotalResources = new ResourceSnapshot
            {
                ComputePoints = reregTotalCapacity.TotalComputePoints,
                MemoryBytes = reregTotalCapacity.TotalMemoryBytes,
                StorageBytes = reregTotalCapacity.TotalStorageBytes
            };

            // Keep existing reserved resources (don't reset!)
            // Reserved resources tracks actual VMs and should not be wiped on re-registration

            _logger.LogInformation(
                "Node {NodeId} re-evaluated: Highest tier={Tier}, Total capacity={Points} points",
                nodeId,
                existingNode.PerformanceEvaluation.HighestTier,
                reregTotalCapacity.TotalComputePoints);

            existingNode.ApiKeyHash = apiKeyHash;
            existingNode.ApiKeyCreatedAt = DateTime.UtcNow;
            existingNode.ApiKeyLastUsedAt = null;

            await _dataStore.SaveNodeAsync(existingNode);

            _logger.LogInformation(
                "✓ Node re-registered successfully: {NodeId} Orchestrator WireGuard Public Key: {OrchestratorPublicKey}",
                existingNode.Id,
                orchestratorPublicKey);

            return new NodeRegistrationResponse(
                existingNode.Id,
                TimeSpan.FromSeconds(15),
                orchestratorPublicKey,
                string.Empty);
        }

        // =====================================================
        // STEP 5: Create New Node
        // =====================================================
        _logger.LogInformation(
            "New node registration: {NodeId} (Machine: {MachineId}, Wallet: {Wallet})",
            nodeId, request.MachineId, request.WalletAddress);

        var node = new Node
        {
            Id = nodeId,
            MachineId = request.MachineId,
            Name = request.Name,
            WalletAddress = request.WalletAddress,
            PublicIp = request.PublicIp,
            AgentPort = request.AgentPort,
            Status = NodeStatus.Online,
            HardwareInventory = request.HardwareInventory,
            // TotalResources will be set after performance evaluation
            TotalResources = new ResourceSnapshot(),
            ReservedResources = new ResourceSnapshot(),
            AgentVersion = request.AgentVersion,
            SupportedImages = request.SupportedImages,
            Region = request.Region ?? "default",
            Zone = request.Zone ?? "default",
            RegisteredAt = request.RegisteredAt,
            LastHeartbeat = DateTime.UtcNow
        };

        // =====================================================
        // STEP 6: Performance Evaluation & Capacity Calculation
        // =====================================================
        var performanceLogger = _loggerFactory.CreateLogger<NodePerformanceEvaluator>();
        var performanceEvaluator = new NodePerformanceEvaluator(_schedulingConfig, performanceLogger);

        node.PerformanceEvaluation = performanceEvaluator.EvaluateNode(node);

        if (!node.PerformanceEvaluation.IsAcceptable)
        {
            _logger.LogWarning(
                "Node {NodeId} rejected during registration: {Reason}",
                nodeId,
                node.PerformanceEvaluation.RejectionReason);

            throw new InvalidOperationException(
                $"Node performance below minimum requirements: {node.PerformanceEvaluation.RejectionReason}");
        }

        // Calculate total capacity using NodeCapacityCalculator
        var capacityLogger = _loggerFactory.CreateLogger<NodeCapacityCalculator>();
        var capacityCalculator = new NodeCapacityCalculator(_schedulingConfig, capacityLogger);
        var totalCapacity = capacityCalculator.CalculateTotalCapacity(node);

        node.TotalResources = new ResourceSnapshot
        {
            ComputePoints = totalCapacity.TotalComputePoints,
            MemoryBytes = totalCapacity.TotalMemoryBytes,
            StorageBytes = totalCapacity.TotalStorageBytes
        };

        _logger.LogInformation(
            "Node {NodeId} accepted: Highest tier={Tier}, Total capacity={Points} points",
            nodeId,
            node.PerformanceEvaluation.HighestTier,
            totalCapacity.TotalComputePoints);

        // =====================================================
        // Generate and save API key
        // =====================================================
        node.ApiKeyHash = apiKeyHash;
        node.ApiKeyCreatedAt = DateTime.UtcNow;
        node.ApiKeyLastUsedAt = null;

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "✓ New node registered successfully: {NodeId}",
            node.Id);

        // =====================================================
        // STEP 7: Relay Node Deployment & Assignment
        // =====================================================
        if (_relayNodeService.IsEligibleForRelay(node) && node.RelayInfo == null)
        {
            _logger.LogInformation(
                "Node {NodeId} is eligible for relay - deploying relay VM",
                node.Id);

            var vmService = _serviceProvider.GetRequiredService<IVmService>();
            var relayVmId = await _relayNodeService.DeployRelayVmAsync(node, vmService);

            if (relayVmId != null)
            {
                _logger.LogInformation(
                    "Relay VM {VmId} deployed successfully for node {NodeId}",
                    relayVmId, node.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to deploy relay VM for eligible node {NodeId}",
                    node.Id);
            }
            await _dataStore.SaveNodeAsync(node);
        }
        else
        {
            _logger.LogInformation(
                "Node {NodeId} is not eligible for relay",
                node.Id);
        }

        // Check if node is behind CGNAT and needs relay assignment
        if (node.HardwareInventory.Network.NatType != NatType.None &&
            node.CgnatInfo == null)
        {
            _logger.LogInformation(
                "Node {NodeId} is behind CGNAT (type: {NatType}) - assigning to relay",
                node.Id, node.HardwareInventory.Network.NatType);

            var relay = await _relayNodeService.FindBestRelayForCgnatNodeAsync(node);

            if (relay != null)
            {
                await _relayNodeService.AssignCgnatNodeToRelayAsync(node, relay);
            }
            else
            {
                _logger.LogWarning(
                    "No available relay found for CGNAT node {NodeId}",
                    node.Id);
            }
        }

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.NodeRegistered,
            ResourceType = "node",
            ResourceId = node.Id,
            NodeId = node.Id,
            Payload = new Dictionary<string, object>
            {
                ["name"] = node.Name,
                ["region"] = node.Region,
                ["machineId"] = node.MachineId,
                ["wallet"] = node.WalletAddress,
                ["resources"] = JsonSerializer.Serialize(node.TotalResources)
            }
        });

        return new NodeRegistrationResponse(
            node.Id,
            TimeSpan.FromSeconds(15),
            orchestratorPublicKey,
            apiKey);
    }

    private bool VerifyWalletSignature(string walletAddress, string message, string signature)
    {
        try
        {
            var signer = new Nethereum.Signer.EthereumMessageSigner();
            var recoveredAddress = signer.EncodeUTF8AndEcRecover(message, signature);

            return string.Equals(
                recoveredAddress,
                walletAddress,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying wallet signature");
            return false;
        }
    }

    /// <summary>
    /// Process heartbeat without overwriting orchestrator resource tracking
    /// </summary>
    public async Task<NodeHeartbeatResponse> ProcessHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
        {
            return new NodeHeartbeatResponse(false, null, null);
        }

        var wasOffline = node.Status == NodeStatus.Offline;
        node.Status = NodeStatus.Online;
        node.LastHeartbeat = DateTime.UtcNow;
        node.LatestMetrics = heartbeat.Metrics;

        // Log discrepancy between node-reported and orchestrator-tracked resources
        var nodeReportedFree = heartbeat.AvailableResources;
        var orchestratorTrackedFree = new ResourceSnapshot
        {
            ComputePoints = node.TotalResources.ComputePoints - node.ReservedResources.ComputePoints,
            MemoryBytes = node.TotalResources.MemoryBytes - node.ReservedResources.MemoryBytes,
            StorageBytes = node.TotalResources.StorageBytes - node.ReservedResources.StorageBytes
        };

        var computePointDiff = Math.Abs(
            (node.TotalResources.ComputePoints - node.ReservedResources.ComputePoints) -
            (nodeReportedFree.ComputePoints));
        var memDiff = Math.Abs(nodeReportedFree.MemoryBytes - orchestratorTrackedFree.MemoryBytes);

        if (computePointDiff > 1 || memDiff > 1024)
        {
            _logger.LogWarning("Resource drift detected on node {NodeId}", nodeId);

            _logger.LogDebug(
                "Resource tracking drift on node {NodeId}: " +
                "Node reports {NodeComputePoints} point(s) / {NodeMem} MB free, " +
                "Orchestrator tracks {OrcComputePoints} point(s) / {OrcMem}MB free " + 
                "(Reserved: {ResComputePoints} point(s) / {ResMem} MB)",
                nodeId,
                nodeReportedFree.ComputePoints, nodeReportedFree.MemoryBytes,
                orchestratorTrackedFree.ComputePoints, orchestratorTrackedFree.MemoryBytes,
                node.ReservedResources.ComputePoints, node.ReservedResources.MemoryBytes);

            // TO-DO: Implement resource reconciliation logic here
            // For now, we just log the discrepancy and update the node based on the ehartbeat
        }

        await _dataStore.SaveNodeAsync(node);

        if (wasOffline)
        {
            _logger.LogInformation("Node {NodeId} came back online", nodeId);
            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.NodeOnline,
                ResourceType = "node",
                ResourceId = nodeId
            });
        }

        // Synchronize VM state from heartbeat
        await SyncVmStateFromHeartbeatAsync(nodeId, heartbeat);

        // Get pending commands for this node
        var commands = _dataStore.GetAndClearPendingCommands(nodeId);

        return new NodeHeartbeatResponse(true, commands.Count > 0 ? commands : null, node.CgnatInfo);
    }

    /// <summary>
    /// Process command acknowledgment from node.
    /// Uses multiple lookup strategies for reliability.
    /// </summary>
    public async Task<bool> ProcessCommandAcknowledgmentAsync(
        string nodeId,
        string commandId,
        CommandAcknowledgment ack)
    {
        _logger.LogInformation(
            "Processing acknowledgment for command {CommandId} from node {NodeId}: Success={Success}",
            commandId, nodeId, ack.Success);

        // ====================================================================
        // MULTI-STRATEGY VM LOOKUP (in order of reliability)
        // ====================================================================

        VirtualMachine? affectedVm = null;
        string lookupMethod = "none";
        CommandRegistration? registration = null;

        // Strategy 1: Command Registry (most reliable)
        if (_dataStore.TryCompleteCommand(commandId, out registration))
        {
            if (_dataStore.VirtualMachines.TryGetValue(registration!.VmId, out affectedVm))
            {
                lookupMethod = "command_registry";
                _logger.LogDebug(
                    "Found VM {VmId} via command registry for command {CommandId}",
                    registration.VmId, commandId);
            }
            else
            {
                _logger.LogWarning(
                    "Command registry pointed to non-existent VM {VmId} for command {CommandId}",
                    registration.VmId, commandId);
            }
        }

        // Strategy 2: VM's ActiveCommandId field (backup)
        if (affectedVm == null)
        {
            affectedVm = _dataStore.VirtualMachines.Values
                .FirstOrDefault(vm =>
                    vm.NodeId == nodeId &&
                    vm.ActiveCommandId == commandId);

            if (affectedVm != null)
            {
                lookupMethod = "active_command_id";
                _logger.LogDebug(
                    "Found VM {VmId} via ActiveCommandId for command {CommandId}",
                    affectedVm.Id, commandId);
            }
        }

        // Strategy 3: StatusMessage contains commandId (legacy fallback)
        if (affectedVm == null)
        {
            affectedVm = _dataStore.VirtualMachines.Values
                .FirstOrDefault(vm =>
                    vm.NodeId == nodeId &&
                    vm.StatusMessage != null &&
                    vm.StatusMessage.Contains(commandId));

            if (affectedVm != null)
            {
                lookupMethod = "status_message_legacy";
                _logger.LogWarning(
                    "Found VM {VmId} via StatusMessage fallback for command {CommandId}. " +
                    "Command tracking may be degraded - check if ActiveCommandId is being set.",
                    affectedVm.Id, commandId);
            }
        }

        // Strategy 4: For DeleteVm commands, try to find VM in Deleting status on this node
        if (affectedVm == null && registration?.CommandType == NodeCommandType.DeleteVm)
        {
            affectedVm = _dataStore.VirtualMachines.Values
                .FirstOrDefault(vm =>
                    vm.NodeId == nodeId &&
                    vm.Status == VmStatus.Deleting);

            if (affectedVm != null)
            {
                lookupMethod = "deleting_status_heuristic";
                _logger.LogWarning(
                    "Found VM {VmId} via Deleting status heuristic for command {CommandId}. " +
                    "This is a last-resort lookup - investigate why primary methods failed.",
                    affectedVm.Id, commandId);
            }
        }

        // ====================================================================
        // HANDLE LOOKUP FAILURE
        // ====================================================================

        if (affectedVm == null)
        {
            _logger.LogError(
                "CRITICAL: Could not find VM for command {CommandId} from node {NodeId}. " +
                "Command type: {Type}. Resources may be leaked if this was a deletion. " +
                "Manual cleanup may be required.",
                commandId, nodeId, registration?.CommandType.ToString() ?? "unknown");

            // Emit alert event for monitoring/alerting systems
            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "command",
                ResourceId = commandId,
                NodeId = nodeId,
                Payload = new Dictionary<string, object>
                {
                    ["Error"] = "Orphaned command acknowledgement - VM not found",
                    ["CommandId"] = commandId,
                    ["CommandType"] = registration?.CommandType.ToString() ?? "unknown",
                    ["ExpectedVmId"] = registration?.VmId ?? "unknown",
                    ["AckSuccess"] = ack.Success
                }
            });

            // Return true - we received the ack even if we couldn't process it
            // The stale command cleanup service will handle stuck VMs
            return true;
        }

        _logger.LogInformation(
            "Processing command {CommandId} for VM {VmId} (lookup: {Method})",
            commandId, affectedVm.Id, lookupMethod);

        // ====================================================================
        // CLEAR COMMAND TRACKING ON VM
        // ====================================================================

        affectedVm.ActiveCommandId = null;
        affectedVm.ActiveCommandType = null;
        affectedVm.ActiveCommandIssuedAt = null;

        // ====================================================================
        // HANDLE COMMAND FAILURE
        // ====================================================================

        if (!ack.Success)
        {
            _logger.LogError(
                "Command {CommandId} failed on node {NodeId}: {Error}",
                commandId, nodeId, ack.ErrorMessage ?? "Unknown error");

            // Handle failure based on VM status
            if (affectedVm.Status == VmStatus.Deleting)
            {
                affectedVm.Status = VmStatus.Error;
                affectedVm.StatusMessage = $"Deletion failed: {ack.ErrorMessage ?? "Unknown error"}";
                affectedVm.UpdatedAt = DateTime.UtcNow;
                await _dataStore.SaveVmAsync(affectedVm);

                _logger.LogWarning(
                    "VM {VmId} deletion failed - resources remain reserved. " +
                    "Manual intervention may be required.",
                    affectedVm.Id);
            }
            else if (affectedVm.Status == VmStatus.Provisioning)
            {
                affectedVm.Status = VmStatus.Error;
                affectedVm.StatusMessage = $"Creation failed: {ack.ErrorMessage ?? "Unknown error"}";
                affectedVm.UpdatedAt = DateTime.UtcNow;
                await _dataStore.SaveVmAsync(affectedVm);
            }
            else if (affectedVm.Status == VmStatus.Stopping)
            {
                // Stop failed - VM might still be running
                affectedVm.Status = VmStatus.Error;
                affectedVm.StatusMessage = $"Stop failed: {ack.ErrorMessage ?? "Unknown error"}";
                affectedVm.UpdatedAt = DateTime.UtcNow;
                await _dataStore.SaveVmAsync(affectedVm);
            }

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "vm",
                ResourceId = affectedVm.Id,
                NodeId = nodeId,
                UserId = affectedVm.OwnerId,
                Payload = new Dictionary<string, object>
                {
                    ["CommandId"] = commandId,
                    ["CommandType"] = registration?.CommandType.ToString() ?? "unknown",
                    ["Error"] = ack.ErrorMessage ?? "Unknown error"
                }
            });

            return true;
        }

        // ====================================================================
        // HANDLE COMMAND SUCCESS
        // ====================================================================

        if (affectedVm.Status == VmStatus.Deleting)
        {
            _logger.LogInformation(
                "Deletion confirmed for VM {VmId} - completing deletion and freeing resources",
                affectedVm.Id);

            await CompleteVmDeletionAsync(affectedVm);
        }
        else if (affectedVm.Status == VmStatus.Provisioning)
        {
            _logger.LogInformation(
                "Creation confirmed for VM {VmId} - marking as running",
                affectedVm.Id);

            affectedVm.Status = VmStatus.Running;
            affectedVm.PowerState = VmPowerState.Running;
            affectedVm.StartedAt = ack.CompletedAt;
            affectedVm.StatusMessage = null;
            affectedVm.UpdatedAt = DateTime.UtcNow;
            await _dataStore.SaveVmAsync(affectedVm);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmStarted,
                ResourceType = "vm",
                ResourceId = affectedVm.Id,
                NodeId = nodeId,
                UserId = affectedVm.OwnerId
            });

            await _ingressService.OnVmStartedAsync(affectedVm.Id);
        }
        else if (affectedVm.Status == VmStatus.Stopping)
        {
            _logger.LogInformation(
                "Stop confirmed for VM {VmId} - marking as stopped",
                affectedVm.Id);

            affectedVm.Status = VmStatus.Stopped;
            affectedVm.PowerState = VmPowerState.Off;
            affectedVm.StoppedAt = ack.CompletedAt;
            affectedVm.StatusMessage = null;
            affectedVm.UpdatedAt = DateTime.UtcNow;
            await _dataStore.SaveVmAsync(affectedVm);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmStopped,
                ResourceType = "vm",
                ResourceId = affectedVm.Id,
                NodeId = nodeId,
                UserId = affectedVm.OwnerId
            });

            await _ingressService.OnVmStoppedAsync(affectedVm.Id);
        }

        return true;
    }

    /// <summary>
    /// Complete VM deletion after node confirmation.
    /// Frees all reserved resources and updates quotas.
    /// </summary>
    private async Task CompleteVmDeletionAsync(VirtualMachine vm)
    {
        _logger.LogInformation(
            "Completing deletion for VM {VmId} (Owner: {Owner}, Node: {Node})",
            vm.Id, vm.OwnerId, vm.NodeId ?? "none");

        // Step 1: Mark as Deleted
        vm.Status = VmStatus.Deleted;
        vm.StatusMessage = "Deletion confirmed by node";
        vm.StoppedAt = DateTime.UtcNow;
        vm.UpdatedAt = DateTime.UtcNow;

        // Clear any remaining command tracking
        vm.ActiveCommandId = null;
        vm.ActiveCommandType = null;
        vm.ActiveCommandIssuedAt = null;

        await _dataStore.SaveVmAsync(vm);

        // Step 2: Free reserved resources from node
        if (!string.IsNullOrEmpty(vm.NodeId) &&
            _dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            var computePointsToFree = vm.Spec.ComputePointCost;
            var memToFree = vm.Spec.MemoryBytes;
            var memToFreeMb = memToFree / (1024 * 1024);
            var storageToFree = vm.Spec.DiskBytes;
            var storageToFreeGb = storageToFree / (1024 * 1024 * 1024);

            // Free CPU cores (legacy)
            node.ReservedResources.ComputePoints = Math.Max(0,
                node.ReservedResources.ComputePoints - computePointsToFree);
            node.ReservedResources.MemoryBytes = Math.Max(0,
                node.ReservedResources.MemoryBytes - memToFree);
            node.ReservedResources.StorageBytes = Math.Max(0,
                node.ReservedResources.StorageBytes - storageToFree);

            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "Released reserved resources for VM {VmId} on node {NodeId}: " +
                "{ComputePoints} point(s), {MemoryMb} MB, {StorageGb} GB. " +
                "Node now has: Reserved={ResComputePoints} point(s), Available={AvComputePoints}pts",
                vm.Id, node.Id, computePointsToFree, memToFreeMb, storageToFreeGb,
                node.ReservedResources.ComputePoints, node.TotalResources.ComputePoints - node.ReservedResources.ComputePoints);
        }
        else
        {
            _logger.LogWarning(
                "Could not find node {NodeId} to release resources for VM {VmId}",
                vm.NodeId, vm.Id);
        }

        // Step 3: Update user quotas
        if (_dataStore.Users.TryGetValue(vm.OwnerId, out var user))
        {
            user.Quotas.CurrentVms = Math.Max(0, user.Quotas.CurrentVms - 1);
            user.Quotas.CurrentVirtualCpuCores = Math.Max(0,
                user.Quotas.CurrentVirtualCpuCores - vm.Spec.VirtualCpuCores);
            user.Quotas.CurrentMemoryBytes = Math.Max(0,
                user.Quotas.CurrentMemoryBytes - vm.Spec.MemoryBytes);
            user.Quotas.CurrentStorageBytes = Math.Max(0,
                user.Quotas.CurrentStorageBytes - vm.Spec.DiskBytes);

            await _dataStore.SaveUserAsync(user);

            _logger.LogInformation(
                "Updated quotas for user {UserId}: VMs={VMs}/{MaxVMs}, CPU={CPU}c, MEM={MEM}MB",
                user.Id, user.Quotas.CurrentVms, user.Quotas.MaxVms,
                user.Quotas.CurrentVirtualCpuCores, user.Quotas.CurrentMemoryBytes);
        }

        await _ingressService.OnVmDeletedAsync(vm.Id);

        // Step 4: Emit completion event
        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmDeleted,
            ResourceType = "vm",
            ResourceId = vm.Id,
            NodeId = vm.NodeId,
            UserId = vm.OwnerId,
            Payload = new Dictionary<string, object>
            {
                ["FreedCpu"] = vm.Spec.VirtualCpuCores,
                ["FreedMemoryBytes"] = vm.Spec.MemoryBytes,
                ["FreedStorageBytes"] = vm.Spec.DiskBytes
            }
        });

        _logger.LogInformation(
            "VM {VmId} deletion completed successfully - all resources freed",
            vm.Id);
    }

    /// <summary>
    /// Synchronize VM state reported by node agent with orchestrator's view
    /// Handles VM state updates and orphan VM recovery
    /// </summary>
    private async Task SyncVmStateFromHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        if (heartbeat.ActiveVms == null || !heartbeat.ActiveVms.Any())
            return;

        var knownVmIds = _dataStore.VirtualMachines.Values
            .Where(v => v.NodeId == nodeId)
            .Select(v => v.Id)
            .ToHashSet();

        var node = await GetNodeAsync(nodeId);

        foreach (var reported in heartbeat.ActiveVms)
        {
            var vmId = reported.VmId;

            if (_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
            {
                // Update existing VM state
                var newStatus = ParseVmStatus(reported.State);
                var newPowerState = ParsePowerState(reported.State);

                if (vm.Status != newStatus || vm.PowerState != newPowerState)
                {
                    var wasRunning = vm.Status == VmStatus.Running;
                    vm.Status = newStatus;
                    vm.PowerState = newPowerState;

                    if (newStatus == VmStatus.Running && vm.StartedAt == null)
                        vm.StartedAt = reported.StartedAt ?? DateTime.UtcNow;

                    await _dataStore.SaveVmAsync(vm);

                    if (newStatus == VmStatus.Running && !wasRunning)
                        await _ingressService.OnVmStartedAsync(vmId);
                    else if (newStatus != VmStatus.Running && wasRunning)
                        await _ingressService.OnVmStoppedAsync(vmId);

                    _logger.LogInformation(
                        "VM {VmId} state updated from heartbeat: {Status}/{PowerState}",
                        vmId, newStatus, newPowerState);
                }

                // Update access info if available
                if (reported.IsIpAssigned)
                {
                    // Update network config with actual libvirt IP
                    vm.NetworkConfig.PrivateIp = reported.IpAddress;
                    vm.NetworkConfig.IsIpAssigned = reported.IsIpAssigned;

                    vm.AccessInfo ??= new VmAccessInfo();
                    vm.AccessInfo.SshHost = reported.IpAddress;
                    vm.AccessInfo.SshPort = 22;

                    if (reported.VncPort != null)
                    {
                        // VNC accessible through WireGuard at node IP
                        vm.AccessInfo.VncHost = node?.PublicIp;
                        vm.AccessInfo.VncPort = reported.VncPort ?? 5900;
                    }

                    await _dataStore.SaveVmAsync(vm);
                }
            }
            else if (!string.IsNullOrEmpty(reported.OwnerId))
            {
                // Orphaned VM recovery
                await RecoverOrphanedVmAsync(nodeId, reported);
            }
        }
    }

    /// <summary>
    /// Recover VMs that exist on node but are unknown to orchestrator
    /// This handles orchestrator restarts where node state persists
    /// </summary>
    private async Task RecoverOrphanedVmAsync(string nodeId, HeartbeatVmInfo reported)
    {
        var vmId = reported.VmId;

        try
        {
            _logger.LogInformation(
                "Recovering orphaned VM {VmId} on node {NodeId} (Owner: {OwnerId}, State: {State})",
                vmId, nodeId, reported.OwnerId, reported.State);

            var recoveredVm = new VirtualMachine
            {
                Id = vmId,
                Name = reported.Name ?? $"recovered-{vmId[..8]}",
                OwnerId = reported.OwnerId,
                NodeId = nodeId,
                Status = ParseVmStatus(reported.State),
                PowerState = ParsePowerState(reported.State),
                StartedAt = reported.StartedAt,
                CreatedAt = reported.StartedAt ?? DateTime.UtcNow,
                NetworkConfig = new VmNetworkConfig
                {
                    PrivateIp = reported.IpAddress ?? "",
                    Hostname = reported.Name ?? "",
                    MacAddress = reported.MacAddress ?? "",
                    //PublicIp = ,
                    //PortMappings = [reported.VncPort],
                    //OverlayNetworkId = 
                },
                AccessInfo = new VmAccessInfo
                {
                    SshHost = reported.IpAddress,
                    SshPort = reported.SshPort ?? 2222,
                    VncHost = reported.IpAddress,
                    VncPort = reported.VncPort ?? 5900
                },
                Spec = new VmSpec
                {
                    VirtualCpuCores = reported.VirtualCpuCores,
                    MemoryBytes = reported.MemoryBytes.Value,
                    DiskBytes = reported.DiskBytes.Value,
                    ImageId = reported.ImageId ?? "Unknown",
                    QualityTier = (QualityTier)reported.QualityTier,
                    ComputePointCost = reported.ComputePointCost,
                },
                StatusMessage = "Recovered from node heartbeat after orchestrator restart",
                Labels = new Dictionary<string, string>
                {
                    ["recovered"] = "true",
                    ["recovery-date"] = DateTime.UtcNow.ToString("O"),
                    ["recovery-node"] = nodeId
                }
            };

            await _dataStore.SaveVmAsync(recoveredVm);

            _logger.LogInformation(
                "✓ Successfully recovered VM {VmId} on node {NodeId} (Owner: {OwnerId}, State: {State})",
                vmId, nodeId, recoveredVm.OwnerId, recoveredVm.Status);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmRecovered,
                ResourceType = "vm",
                ResourceId = vmId,
                NodeId = nodeId,
                UserId = recoveredVm.OwnerId,
                Payload = new Dictionary<string, object>
                {
                    ["nodeId"] = nodeId,
                    ["state"] = recoveredVm.Status.ToString(),
                    ["ipAddress"] = recoveredVm.NetworkConfig.PrivateIp ?? "",
                    ["recoveryTimestamp"] = DateTime.UtcNow
                }
            });

            if (recoveredVm.Status == VmStatus.Running)
            {
                _logger.LogInformation(
                    "Registering recovered running VM {VmId} with CentralIngress",
                    vmId);
                await _ingressService.OnVmStartedAsync(vmId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to recover orphaned VM {VmId} on node {NodeId}",
                vmId, nodeId);
        }
    }

    private VmStatus ParseVmStatus(string? state) => state?.ToLower() switch
    {
        "running" => VmStatus.Running,
        "stopped" => VmStatus.Stopped,
        "paused" => VmStatus.Running,
        "stopping" => VmStatus.Stopping,
        "failed" => VmStatus.Error,
        "error" => VmStatus.Error,
        _ => VmStatus.Error
    };

    private VmPowerState ParsePowerState(string? state) => state?.ToLower() switch
    {
        "running" => VmPowerState.Running,
        "paused" => VmPowerState.Paused,
        "stopped" => VmPowerState.Off,
        _ => VmPowerState.Off
    };

    // ============================================================================
    // SSH Methods
    // ============================================================================

    /// <summary>
    /// Request node to sign an SSH certificate using its CA
    /// </summary>
    public async Task<CertificateSignResponse> SignCertificateAsync(
        string nodeId,
        CertificateSignRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var node = await GetNodeAsync(nodeId);
            if (node == null)
            {
                _logger.LogWarning("Node {NodeId} not found for certificate signing", nodeId);
                return new CertificateSignResponse
                {
                    Success = false,
                    Error = "Node not found"
                };
            }

            var url = $"http://{node.PublicIp}:{node.AgentPort}/api/ssh/sign-certificate";

            _logger.LogInformation(
                "Requesting certificate signing from node {NodeId} at {Url}",
                nodeId, url);

            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var httpResponse = await _httpClient.PostAsync(url, content, ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "Certificate signing failed: HTTP {StatusCode}, Body: {Body}",
                    httpResponse.StatusCode, errorBody);

                return new CertificateSignResponse
                {
                    Success = false,
                    Error = $"HTTP {httpResponse.StatusCode}: {errorBody}"
                };
            }

            var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
            var response = System.Text.Json.JsonSerializer.Deserialize<CertificateSignResponse>(responseJson);

            if (response == null)
            {
                return new CertificateSignResponse
                {
                    Success = false,
                    Error = "Failed to deserialize response"
                };
            }

            _logger.LogInformation(
                "✓ Certificate signed successfully for cert ID {CertId}",
                request.CertificateId);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting certificate signature from node {NodeId}", nodeId);
            return new CertificateSignResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Inject SSH public key into a VM's authorized_keys
    /// </summary>
    [Obsolete("Use SSH certificates instead - VMs validate certificates end-to-end")]
    public async Task<bool> InjectSshKeyAsync(
        string nodeId,
        string vmId,
        string publicKey,
        string username = "root",
        CancellationToken ct = default)
    {
        try
        {
            var node = await GetNodeAsync(nodeId);
            if (node == null)
            {
                _logger.LogWarning("Node {NodeId} not found for SSH key injection", nodeId);
                return false;
            }

            var url = $"http://{node.PublicIp}:{node.AgentPort}/api/vms/{vmId}/ssh/inject-key";

            _logger.LogInformation(
                "Injecting SSH key into VM {VmId} on node {NodeId}",
                vmId, nodeId);

            var request = new InjectSshKeyRequest
            {
                PublicKey = publicKey,
                Username = username,
                Temporary = true
            };

            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var httpResponse = await _httpClient.PostAsync(url, content, ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "SSH key injection failed: HTTP {StatusCode}, Body: {Body}",
                    httpResponse.StatusCode, errorBody);
                return false;
            }

            _logger.LogInformation(
                "✓ SSH key injected successfully into VM {VmId}",
                vmId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error injecting SSH key into VM {VmId} on node {NodeId}", vmId, nodeId);
            return false;
        }
    }

    // ============================================================================
    // Simple Getters
    // ============================================================================

    public Task<Node?> GetNodeAsync(string nodeId)
    {
        _dataStore.Nodes.TryGetValue(nodeId, out var node);
        return Task.FromResult(node);
    }

    public Task<List<Node>> GetAllNodesAsync(NodeStatus? statusFilter = null)
    {
        var nodes = _dataStore.Nodes.Values
            .Where(n => !statusFilter.HasValue || n.Status == statusFilter.Value)
            .OrderBy(n => n.Name)
            .ToList();

        return Task.FromResult(nodes);
    }

    public async Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status)
    {
        if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            return false;

        node.Status = status;
        await _dataStore.SaveNodeAsync(node);
        return true;
    }

    public async Task<bool> RemoveNodeAsync(string nodeId)
    {
        if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            return false;

        await _dataStore.DeleteNodeAsync(nodeId);
        _logger.LogInformation("Node removed: {NodeId} ({Name})", nodeId, node.Name);
        return true;
    }

    // ============================================================================
    // Health Monitoring
    // ============================================================================

    public async Task CheckNodeHealthAsync()
    {
        var heartbeatTimeout = TimeSpan.FromMinutes(2);
        var now = DateTime.UtcNow;

        var onlineNodes = _dataStore.Nodes.Values
            .Where(n => n.Status == NodeStatus.Online)
            .ToList();

        foreach (var node in onlineNodes)
        {
            var timeSinceLastHeartbeat = now - node.LastHeartbeat;

            if (timeSinceLastHeartbeat > heartbeatTimeout)
            {
                _logger.LogWarning(
                    "Node {NodeId} ({Name}) marked as offline - no heartbeat for {Minutes:F1} minutes",
                    node.Id, node.Name, timeSinceLastHeartbeat.TotalMinutes);

                node.Status = NodeStatus.Offline;
                await _dataStore.SaveNodeAsync(node);

                await _eventService.EmitAsync(new OrchestratorEvent
                {
                    Type = EventType.NodeOffline,
                    ResourceType = "node",
                    ResourceId = node.Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["lastHeartbeat"] = node.LastHeartbeat,
                        ["timeoutMinutes"] = timeSinceLastHeartbeat.TotalMinutes
                    }
                });

                await MarkNodeVmsAsErrorAsync(node.Id);
            }
        }
    }

    private async Task MarkNodeVmsAsErrorAsync(string nodeId)
    {
        var nodeVms = _dataStore.VirtualMachines.Values
            .Where(v => v.NodeId == nodeId && v.Status == VmStatus.Running)
            .ToList();

        foreach (var vm in nodeVms)
        {
            _logger.LogWarning("VM {VmId} on offline node {NodeId} marked as error",
                vm.Id, nodeId);

            vm.Status = VmStatus.Error;
            vm.StatusMessage = "Node went offline";
            vm.UpdatedAt = DateTime.UtcNow;

            await _dataStore.SaveVmAsync(vm);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "vm",
                ResourceId = vm.Id,
                UserId = vm.OwnerId,
                NodeId = nodeId,
                Payload = new Dictionary<string, object>
                {
                    ["reason"] = "Node offline",
                    ["nodeId"] = nodeId
                }
            });
        }
    }

    private string GenerateApiKey()
    {
        var apiKeyBytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(apiKeyBytes);
        var apiKey = $"dck_{Convert.ToBase64String(apiKeyBytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "")}";
        return apiKey;
    }

    private string GenerateHash(string data)
    {
        return Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(data)));
    }
}