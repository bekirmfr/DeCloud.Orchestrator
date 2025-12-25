using DeCloud.Shared;
using Orchestrator.Data;
using Orchestrator.Models;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Services;

public interface INodeService
{
    Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request);
    Task<bool> ValidateNodeTokenAsync(string nodeId, string token);
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
    private readonly HttpClient _httpClient;
    private readonly SchedulingConfiguration _schedulingConfig;

    public NodeService(
        DataStore dataStore,
        IEventService eventService,
        ICentralIngressService ingressService,
        ILogger<NodeService> logger,
        HttpClient httpClient,
        SchedulingConfiguration? schedulingConfig = null)
    {
        _dataStore = dataStore;
        _eventService = eventService;
        _ingressService = ingressService;
        _logger = logger;
        _httpClient = httpClient;
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

    public async Task<Node?> SelectBestNodeForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard)
    {
        var scoredNodes = await GetScoredNodesForVmAsync(spec, tier);

        var best = scoredNodes
            .Where(sn => sn.RejectionReason == null)
            .OrderByDescending(sn => sn.TotalScore)
            .FirstOrDefault();

        if (best != null)
        {
            _logger.LogInformation(
                "Selected node {NodeId} ({NodeName}) for VM - Score: {Score:F2}, " +
                "CPU: {Cpu}%, MEM: {Mem}%, Tier: {Tier}",
                best.Node.Id, best.Node.Name, best.TotalScore,
                best.Availability.ProjectedCpuUtilization(spec),
                best.Availability.ProjectedMemoryUtilization(spec),
                tier);
        }
        else
        {
            _logger.LogWarning(
                "No suitable node found for VM - CPU: {Cpu}, MEM: {Mem}MB, DISK: {Disk}GB, Tier: {Tier}",
                spec.CpuCores, spec.MemoryMb, spec.DiskGb, tier);
        }

        return best?.Node;
    }

    /// <summary>
    /// Score and rank nodes for VM placement.
    /// Returns nodes sorted by score (highest first).
    /// </summary>
    public async Task<List<ScoredNode>> GetScoredNodesForVmAsync(
        VmSpec spec,
        QualityTier tier)
    {
        var policy = _schedulingConfig.TierPolicies[tier];
        var onlineNodes = _dataStore.Nodes.Values
            .Where(n => n.Status == NodeStatus.Online)
            .ToList();

        _logger.LogInformation(
            "Scoring {NodeCount} online nodes for VM spec: {CpuCores}c/{MemoryMb}MB/{DiskGb}GB [{Tier}]",
            onlineNodes.Count,
            spec.CpuCores,
            spec.MemoryMb,
            spec.DiskGb,
            tier);

        var scoredNodes = new List<ScoredNode>();

        foreach (var node in onlineNodes)
        {
            var scored = await ScoreNodeForVmAsync(node, spec, tier, policy);

            if (!string.IsNullOrEmpty(scored.RejectionReason))
            {
                _logger.LogDebug(
                    "Node {NodeId} rejected: {Reason}",
                    node.Id,
                    scored.RejectionReason);
            }
            else
            {
                _logger.LogDebug(
                    "Node {NodeId} scored: {Score:F3} (Capacity: {CapScore:F2}, Load: {LoadScore:F2})",
                    node.Id,
                    scored.TotalScore,
                    scored.ComponentScores.CapacityScore,
                    scored.ComponentScores.LoadScore);
            }

            scoredNodes.Add(scored);
        }

        return scoredNodes
            .Where(sn => string.IsNullOrEmpty(sn.RejectionReason))
            .OrderByDescending(sn => sn.TotalScore)
            .ToList();
    }

    // ============================================================================
    // SMART SCHEDULING - Scoring & Filtering
    // ============================================================================

    /// <summary>
    /// Score a single node for VM placement
    /// </summary>
    private async Task<ScoredNode> ScoreNodeForVmAsync(
        Node node,
        VmSpec spec,
        QualityTier tier,
        TierPolicy policy)
    {
        var scored = new ScoredNode { Node = node };

        // Step 1: Hard filters (must pass or node is rejected)
        var rejection = ApplyHardFilters(node, spec, policy);
        if (rejection != null)
        {
            scored.RejectionReason = rejection;
            scored.TotalScore = 0;
            return scored;
        }

        // Step 2: Calculate resource availability with overcommit
        var availability = CalculateResourceAvailability(node, tier, policy);
        scored.Availability = availability;

        // Step 3: Check if VM fits after overcommit calculation
        if (!availability.CanFit(spec))
        {
            scored.RejectionReason = $"Insufficient resources after overcommit " +
                $"(Requested: {spec.CpuCores}c/{spec.MemoryMb}MB/{spec.DiskGb}GB, " +
                $"Available: {availability.RemainingCpu:F1}c/{availability.RemainingMemory:F0}MB/{availability.RemainingStorage:F0}GB)";
            scored.TotalScore = 0;
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
            return scored;
        }

        // Step 5: Calculate component scores
        scored.ComponentScores = new NodeScores
        {
            CapacityScore = CalculateCapacityScore(availability, spec),
            LoadScore = CalculateLoadScore(node),
            ReputationScore = CalculateReputationScore(node),
            LocalityScore = CalculateLocalityScore(node, spec)
        };

        // Step 6: Calculate weighted total score
        var weights = _schedulingConfig.Weights;
        scored.TotalScore =
            (scored.ComponentScores.CapacityScore * weights.Capacity) +
            (scored.ComponentScores.LoadScore * weights.Load) +
            (scored.ComponentScores.ReputationScore * weights.Reputation) +
            (scored.ComponentScores.LocalityScore * weights.Locality);

        return scored;
    }

    /// <summary>
    /// Apply hard filters that immediately reject a node
    /// </summary>
    private string? ApplyHardFilters(Node node, VmSpec spec, TierPolicy policy)
    {
        // Check node status
        if (node.Status != NodeStatus.Online)
        {
            return $"Node is {node.Status}";
        }

        // Check GPU requirement
        if (spec.RequiresGpu && !node.SupportsGpu)
        {
            return "Node does not support GPU";
        }

        // Check specific GPU model if requested
        if (!string.IsNullOrEmpty(spec.GpuModel) &&
            (node.GpuInfo == null || !node.GpuInfo.Model.Contains(spec.GpuModel, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Node does not have required GPU model: {spec.GpuModel}";
        }

        // Check image support
        if (!string.IsNullOrEmpty(spec.ImageId) &&
            node.SupportedImages.Count > 0 &&
            !node.SupportedImages.Contains(spec.ImageId))
        {
            return $"Node does not support image: {spec.ImageId}";
        }

        // Check minimum physical capacity (before overcommit)
        // A node with 1 physical core cannot realistically host VMs requesting 4+ cores
        if (node.TotalResources.CpuCores < spec.CpuCores && policy.CpuOvercommitRatio == 1.0)
        {
            return $"Guaranteed tier requires {spec.CpuCores} physical cores, node has {node.TotalResources.CpuCores}";
        }

        // Check if node has any heartbeat (not stale)
        var staleness = DateTime.UtcNow - node.LastHeartbeat;
        if (staleness > TimeSpan.FromMinutes(2))
        {
            return $"Node heartbeat is stale ({staleness.TotalMinutes:F1} minutes)";
        }

        return null; // Passes all hard filters
    }

    // ============================================================================
    // RESOURCE AVAILABILITY CALCULATION
    // ============================================================================

    /// <summary>
    /// Calculate resource availability for a node with overcommit applied.
    /// 
    /// CRITICAL: Overcommit is applied to PHYSICAL cores, not logical threads.
    /// 
    /// Example: Node with 8 physical cores, 16 threads (hyperthreading)
    /// - TotalResources.CpuCores = 8 (physical)
    /// - TotalResources.CpuThreads = 16 (logical)
    /// 
    /// For Standard tier (2:1 overcommit):
    /// - EffectiveCpuCapacity = 8 × 2.0 = 16 vCPUs
    /// 
    /// This means we can allocate up to 16 vCPUs on this node,
    /// where each vCPU shares a physical core with one other vCPU.
    /// </summary>
    private NodeResourceAvailability CalculateResourceAvailability(
        Node node,
        QualityTier tier,
        TierPolicy policy)
    {
        // Physical cores are the BASE for overcommit calculations
        var physicalCores = node.TotalResources.CpuCores;
        var logicalThreads = node.TotalResources.CpuThreads;

        // If CpuThreads wasn't set (legacy data), assume no hyperthreading
        if (logicalThreads == 0)
        {
            logicalThreads = physicalCores;
        }

        // Calculate effective capacity: Physical cores × Overcommit ratio
        var effectiveCpu = physicalCores * policy.CpuOvercommitRatio;
        var effectiveMemory = node.TotalResources.MemoryMb * policy.MemoryOvercommitRatio;
        var effectiveStorage = node.TotalResources.StorageGb * policy.StorageOvercommitRatio;

        // Currently allocated resources (from orchestrator tracking)
        var allocatedCpu = (double)node.ReservedResources.CpuCores;
        var allocatedMemory = (double)node.ReservedResources.MemoryMb;
        var allocatedStorage = (double)node.ReservedResources.StorageGb;

        var availability = new NodeResourceAvailability
        {
            NodeId = node.Id,
            Tier = tier,
            PhysicalCpuCores = physicalCores,
            LogicalCpuThreads = logicalThreads,
            EffectiveCpuCapacity = effectiveCpu,
            EffectiveMemoryCapacity = effectiveMemory,
            EffectiveStorageCapacity = effectiveStorage,
            AllocatedCpu = allocatedCpu,
            AllocatedMemory = allocatedMemory,
            AllocatedStorage = allocatedStorage
        };

        _logger.LogDebug(
            "Node {NodeId} capacity calculation: {Summary}",
            node.Id,
            availability.GetCapacitySummary());

        return availability;
    }

    /// <summary>
    /// Calculate capacity score (0.0 - 1.0)
    /// Higher score = more remaining capacity after placing VM
    /// </summary>
    private double CalculateCapacityScore(NodeResourceAvailability availability, VmSpec spec)
    {
        // Calculate headroom after placing this VM
        var cpuHeadroom = availability.RemainingCpu - spec.CpuCores;
        var memoryHeadroom = availability.RemainingMemory - spec.MemoryMb;

        // Normalize to 0-1 range (prefer nodes that will have 50%+ capacity remaining)
        var cpuScore = Math.Min(1.0, cpuHeadroom / (availability.EffectiveCpuCapacity * 0.5));
        var memScore = Math.Min(1.0, memoryHeadroom / (availability.EffectiveMemoryCapacity * 0.5));

        // Average of CPU and memory scores
        return Math.Max(0, (cpuScore + memScore) / 2);
    }

    /// <summary>
    /// Calculate load score (0.0 - 1.0)
    /// Higher score = lower current load
    /// </summary>
    private double CalculateLoadScore(Node node)
    {
        if (node.LatestMetrics == null)
        {
            return 0.5; // Default score if no metrics
        }

        var cpuLoad = node.LatestMetrics.CpuUsagePercent;
        var memLoad = node.LatestMetrics.MemoryUsagePercent;
        var loadAvg = node.LatestMetrics.LoadAverage;

        // Invert: lower load = higher score
        var cpuScore = 1.0 - (cpuLoad / 100.0);
        var memScore = 1.0 - (memLoad / 100.0);

        // Load average penalty (high load average = lower score)
        var loadPenalty = Math.Min(1.0, loadAvg / _schedulingConfig.MaxLoadAverage);

        return Math.Max(0, ((cpuScore + memScore) / 2) - (loadPenalty * 0.2));
    }

    /// <summary>
    /// Calculate reputation score (0.0 - 1.0)
    /// Higher score = better track record
    /// </summary>
    private double CalculateReputationScore(Node node)
    {
        // Uptime component (0-0.5)
        var uptimeScore = (node.UptimePercentage / 100.0) * 0.5;

        // Success rate component (0-0.5)
        var successRate = node.TotalVmsHosted > 0
            ? (double)node.SuccessfulVmCompletions / node.TotalVmsHosted
            : 0.8; // Default for new nodes

        var successScore = successRate * 0.5;

        return uptimeScore + successScore;
    }

    /// <summary>
    /// Calculate locality score (0.0 - 1.0)
    /// Higher score = closer to preferred region/zone
    /// </summary>
    private double CalculateLocalityScore(Node node, VmSpec spec)
    {
        if (string.IsNullOrEmpty(spec.PreferredRegion))
        {
            return 0.5; // No preference, neutral score
        }

        // Same region = high score
        if (node.Region.Equals(spec.PreferredRegion, StringComparison.OrdinalIgnoreCase))
        {
            // Same zone = perfect score
            if (!string.IsNullOrEmpty(spec.PreferredZone) &&
                node.Zone.Equals(spec.PreferredZone, StringComparison.OrdinalIgnoreCase))
            {
                return 1.0;
            }
            return 0.8; // Same region, different zone
        }

        return 0.2; // Different region
    }

    // ============================================================================
    // RESOURCE RESERVATION
    // ============================================================================

    /// <summary>
    /// Reserve resources on a node for a VM.
    /// Called when VM is scheduled (before actual creation).
    /// </summary>
    public async Task<bool> ReserveResourcesForVmAsync(Node node, VmSpec spec)
    {
        // Add to reserved resources
        node.ReservedResources.CpuCores += spec.CpuCores;
        node.ReservedResources.MemoryMb += spec.MemoryMb;
        node.ReservedResources.StorageGb += spec.DiskGb;

        // Update available (inverse of reserved)
        node.AvailableResources.CpuCores =
            node.TotalResources.CpuCores - node.ReservedResources.CpuCores;
        node.AvailableResources.MemoryMb =
            node.TotalResources.MemoryMb - node.ReservedResources.MemoryMb;
        node.AvailableResources.StorageGb =
            node.TotalResources.StorageGb - node.ReservedResources.StorageGb;

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Reserved resources on node {NodeId} for VM: {CpuCores}c/{MemoryMb}MB/{DiskGb}GB. " +
            "Node now has: Reserved={ResCpu}c/{ResMem}MB, " +
            "Physical={PhysCpu}c, EffectiveCapacity={EffCpu:F1}c (Standard tier)",
            node.Id,
            spec.CpuCores, spec.MemoryMb, spec.DiskGb,
            node.ReservedResources.CpuCores, node.ReservedResources.MemoryMb,
            node.TotalResources.CpuCores,
            node.TotalResources.CpuCores * 2.0); // Standard tier example

        return true;
    }

    /// <summary>
    /// Release resources when a VM is deleted or failed to create.
    /// </summary>
    public async Task ReleaseResourcesForVmAsync(Node node, VmSpec spec)
    {
        // Subtract from reserved (with floor of 0)
        node.ReservedResources.CpuCores = Math.Max(0,
            node.ReservedResources.CpuCores - spec.CpuCores);
        node.ReservedResources.MemoryMb = Math.Max(0,
            node.ReservedResources.MemoryMb - spec.MemoryMb);
        node.ReservedResources.StorageGb = Math.Max(0,
            node.ReservedResources.StorageGb - spec.DiskGb);

        // Update available
        node.AvailableResources.CpuCores =
            node.TotalResources.CpuCores - node.ReservedResources.CpuCores;
        node.AvailableResources.MemoryMb =
            node.TotalResources.MemoryMb - node.ReservedResources.MemoryMb;
        node.AvailableResources.StorageGb =
            node.TotalResources.StorageGb - node.ReservedResources.StorageGb;

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Released resources on node {NodeId}: {CpuCores}c/{MemoryMb}MB/{DiskGb}GB. " +
            "Node now has: Reserved={ResCpu}c/{ResMem}MB",
            node.Id,
            spec.CpuCores, spec.MemoryMb, spec.DiskGb,
            node.ReservedResources.CpuCores, node.ReservedResources.MemoryMb);
    }

    // ============================================================================
    // Registration and Heartbeat
    // ============================================================================

    /// <summary>
    /// Register a new node or re-register an existing one.
    /// 
    /// CPU Resource Handling:
    /// - Accepts both CpuCores (physical) and CpuThreads (logical)
    /// - If only CpuCores is provided, CpuThreads defaults to same value
    /// - For legacy clients sending logical cores as CpuCores, a heuristic is applied
    /// </summary>
    public async Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request)
    {
        // =====================================================
        // STEP 1: Validate Request
        // =====================================================
        if (string.IsNullOrEmpty(request.MachineId))
        {
            throw new ArgumentException("MachineId is required for node registration");
        }

        if (string.IsNullOrEmpty(request.WalletAddress) ||
            request.WalletAddress == "0x0000000000000000000000000000000000000000")
        {
            throw new ArgumentException("Valid wallet address is required");
        }

        // =====================================================
        // STEP 2: Generate Deterministic Node ID
        // =====================================================
        var expectedNodeId = GenerateDeterministicNodeId(request.MachineId, request.WalletAddress);

        if (!string.IsNullOrEmpty(request.NodeId) && request.NodeId != expectedNodeId)
        {
            _logger.LogWarning(
                "Node ID mismatch: provided {Provided}, expected {Expected}",
                request.NodeId, expectedNodeId);
            throw new ArgumentException(
                "The provided node ID does not match the deterministic ID for this machine + wallet combination.");
        }

        var nodeId = expectedNodeId;

        // =====================================================
        // STEP 3: Normalize CPU Resources
        // =====================================================
        var resources = NormalizeCpuResources(request.Resources);

        _logger.LogInformation(
            "Node {NodeId} registering with resources: " +
            "CPU={PhysicalCores}c/{LogicalThreads}t, MEM={MemoryMb}MB, DISK={StorageGb}GB",
            nodeId,
            resources.CpuCores,
            resources.CpuThreads,
            resources.MemoryMb,
            resources.StorageGb);

        // =====================================================
        // STEP 4: Check if Node Exists (Re-registration)
        // =====================================================
        var existingNode = _dataStore.Nodes.Values.FirstOrDefault(n => n.Id == nodeId);

        if (existingNode != null)
        {
            return await HandleReRegistrationAsync(existingNode, request, resources);
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
            TotalResources = resources,
            AvailableResources = new NodeResources
            {
                CpuCores = resources.CpuCores,
                CpuThreads = resources.CpuThreads,
                MemoryMb = resources.MemoryMb,
                StorageGb = resources.StorageGb,
                BandwidthMbps = resources.BandwidthMbps
            },
            ReservedResources = new NodeResources(),
            AgentVersion = request.AgentVersion,
            SupportedImages = request.SupportedImages,
            SupportsGpu = request.SupportsGpu,
            GpuInfo = request.GpuInfo,
            Region = request.Region ?? "default",
            Zone = request.Zone ?? "default",
            LastHeartbeat = DateTime.UtcNow
        };

        await _dataStore.SaveNodeAsync(node);

        var newToken = GenerateAuthToken();
        _dataStore.NodeAuthTokens[node.Id] = HashToken(newToken);

        _logger.LogInformation(
            "✓ New node registered successfully: {NodeId} " +
            "(Physical cores: {PhysCores}, Logical threads: {LogicalThreads})",
            node.Id,
            resources.CpuCores,
            resources.CpuThreads);

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
                ["physicalCores"] = resources.CpuCores,
                ["logicalThreads"] = resources.CpuThreads,
                ["memoryMb"] = resources.MemoryMb,
                ["storageGb"] = resources.StorageGb
            }
        });

        return new NodeRegistrationResponse(node.Id, newToken, TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Handle re-registration of an existing node.
    /// Preserves reserved resources but updates total capacity.
    /// </summary>
    private async Task<NodeRegistrationResponse> HandleReRegistrationAsync(
        Node existingNode,
        NodeRegistrationRequest request,
        NodeResources resources)
    {
        _logger.LogInformation(
            "Node re-registration: {NodeId} (Machine: {MachineId}, Wallet: {Wallet})",
            existingNode.Id, request.MachineId, request.WalletAddress);

        // Update node properties
        existingNode.Name = request.Name;
        existingNode.MachineId = request.MachineId;
        existingNode.WalletAddress = request.WalletAddress;
        existingNode.PublicIp = request.PublicIp;
        existingNode.AgentPort = request.AgentPort;
        existingNode.TotalResources = resources;
        existingNode.AgentVersion = request.AgentVersion;
        existingNode.SupportedImages = request.SupportedImages;
        existingNode.SupportsGpu = request.SupportsGpu;
        existingNode.GpuInfo = request.GpuInfo;
        existingNode.Region = request.Region ?? "default";
        existingNode.Zone = request.Zone ?? "default";
        existingNode.Status = NodeStatus.Online;
        existingNode.LastHeartbeat = DateTime.UtcNow;

        // Recalculate available resources (preserving reservations)
        existingNode.AvailableResources = new NodeResources
        {
            CpuCores = resources.CpuCores - existingNode.ReservedResources.CpuCores,
            CpuThreads = resources.CpuThreads - existingNode.ReservedResources.CpuThreads,
            MemoryMb = resources.MemoryMb - existingNode.ReservedResources.MemoryMb,
            StorageGb = resources.StorageGb - existingNode.ReservedResources.StorageGb,
            BandwidthMbps = resources.BandwidthMbps
        };

        await _dataStore.SaveNodeAsync(existingNode);

        var token = GenerateAuthToken();
        _dataStore.NodeAuthTokens[existingNode.Id] = HashToken(token);

        _logger.LogInformation(
            "✓ Node re-registered successfully: {NodeId} " +
            "(Physical cores: {PhysCores}, Reserved: {ResCores})",
            existingNode.Id,
            resources.CpuCores,
            existingNode.ReservedResources.CpuCores);

        return new NodeRegistrationResponse(
            existingNode.Id,
            token,
            TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Normalize CPU resources from registration request.
    /// 
    /// Handles several scenarios:
    /// 1. New format: Both CpuCores (physical) and CpuThreads (logical) provided
    /// 2. Legacy format: Only CpuCores provided (could be physical or logical)
    /// 3. Heuristic: If CpuCores is suspiciously high, might be logical cores
    /// </summary>
    private NodeResources NormalizeCpuResources(NodeResources input)
    {
        var output = new NodeResources
        {
            MemoryMb = input.MemoryMb,
            StorageGb = input.StorageGb,
            BandwidthMbps = input.BandwidthMbps
        };

        // Case 1: Both values provided (new format)
        if (input.CpuCores > 0 && input.CpuThreads > 0)
        {
            output.CpuCores = input.CpuCores;
            output.CpuThreads = input.CpuThreads;

            _logger.LogDebug(
                "CPU resources (new format): {Physical}c/{Logical}t",
                output.CpuCores, output.CpuThreads);
        }
        // Case 2: Only CpuCores provided (legacy or no HT)
        else if (input.CpuCores > 0)
        {
            // Heuristic: Common server CPUs have 4-64 physical cores
            // If value is very high and even, it might be logical cores
            // Conservative approach: assume it's physical unless clearly wrong

            output.CpuCores = input.CpuCores;
            output.CpuThreads = input.CpuCores; // Assume no hyperthreading

            _logger.LogDebug(
                "CPU resources (legacy format): {Cores}c (assuming physical)",
                output.CpuCores);
        }
        else
        {
            // Fallback: minimum viable
            output.CpuCores = 1;
            output.CpuThreads = 1;

            _logger.LogWarning("No CPU cores reported, defaulting to 1");
        }

        return output;
    }

    public Task<bool> ValidateNodeTokenAsync(string nodeId, string token)
    {
        if (!_dataStore.NodeAuthTokens.TryGetValue(nodeId, out var storedHash))
            return Task.FromResult(false);

        var tokenHash = HashToken(token);
        return Task.FromResult(storedHash == tokenHash);
    }

    /// <summary>
    /// Process heartbeat without overwriting orchestrator resource tracking.
    /// Logs resource discrepancies for debugging.
    /// </summary>
    public async Task<NodeHeartbeatResponse> ProcessHeartbeatAsync(
        string nodeId,
        NodeHeartbeat heartbeat)
    {
        if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
        {
            return new NodeHeartbeatResponse(false, null);
        }

        var wasOffline = node.Status == NodeStatus.Offline;
        node.Status = NodeStatus.Online;
        node.LastHeartbeat = DateTime.UtcNow;
        node.LatestMetrics = heartbeat.Metrics;

        // Log resource discrepancy between node-reported and orchestrator-tracked
        var nodeReportedFree = heartbeat.AvailableResources;
        var orchestratorTrackedFree = new NodeResources
        {
            CpuCores = node.TotalResources.CpuCores - node.ReservedResources.CpuCores,
            CpuThreads = node.TotalResources.CpuThreads - node.ReservedResources.CpuThreads,
            MemoryMb = node.TotalResources.MemoryMb - node.ReservedResources.MemoryMb,
            StorageGb = node.TotalResources.StorageGb - node.ReservedResources.StorageGb
        };

        var cpuDiff = Math.Abs(nodeReportedFree.CpuCores - orchestratorTrackedFree.CpuCores);
        var memDiff = Math.Abs(nodeReportedFree.MemoryMb - orchestratorTrackedFree.MemoryMb);

        if (cpuDiff > 1 || memDiff > 1024)
        {
            _logger.LogDebug(
                "Resource tracking drift on node {NodeId}: " +
                "Node reports {NodeCpu}c/{NodeMem}MB free, " +
                "Orchestrator tracks {OrcCpu}c/{OrcMem}MB free " +
                "(Total: {TotalCpu}c, Reserved: {ResCpu}c)",
                nodeId,
                nodeReportedFree.CpuCores, nodeReportedFree.MemoryMb,
                orchestratorTrackedFree.CpuCores, orchestratorTrackedFree.MemoryMb,
                node.TotalResources.CpuCores, node.ReservedResources.CpuCores);
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

        return new NodeHeartbeatResponse(true, commands.Count > 0 ? commands : null);
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
            var cpuToFree = vm.Spec.CpuCores;
            var memToFree = vm.Spec.MemoryMb;
            var storageToFree = vm.Spec.DiskGb;

            node.ReservedResources.CpuCores = Math.Max(0,
                node.ReservedResources.CpuCores - cpuToFree);
            node.ReservedResources.MemoryMb = Math.Max(0,
                node.ReservedResources.MemoryMb - memToFree);
            node.ReservedResources.StorageGb = Math.Max(0,
                node.ReservedResources.StorageGb - storageToFree);

            // Also update available resources
            node.AvailableResources.CpuCores += cpuToFree;
            node.AvailableResources.MemoryMb += memToFree;
            node.AvailableResources.StorageGb += storageToFree;

            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "Released reserved resources for VM {VmId} on node {NodeId}: " +
                "{CpuCores}c, {MemoryMb}MB, {StorageGb}GB. " +
                "Node now has: Reserved={ResCpu}c/{ResMem}MB, Available={AvCpu}c/{AvMem}MB",
                vm.Id, node.Id, cpuToFree, memToFree, storageToFree,
                node.ReservedResources.CpuCores, node.ReservedResources.MemoryMb,
                node.AvailableResources.CpuCores, node.AvailableResources.MemoryMb);
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
            user.Quotas.CurrentCpuCores = Math.Max(0,
                user.Quotas.CurrentCpuCores - vm.Spec.CpuCores);
            user.Quotas.CurrentMemoryMb = Math.Max(0,
                user.Quotas.CurrentMemoryMb - vm.Spec.MemoryMb);
            user.Quotas.CurrentStorageGb = Math.Max(0,
                user.Quotas.CurrentStorageGb - vm.Spec.DiskGb);

            await _dataStore.SaveUserAsync(user);

            _logger.LogInformation(
                "Updated quotas for user {UserId}: VMs={VMs}/{MaxVMs}, CPU={CPU}c, MEM={MEM}MB",
                user.Id, user.Quotas.CurrentVms, user.Quotas.MaxVms,
                user.Quotas.CurrentCpuCores, user.Quotas.CurrentMemoryMb);
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
                ["FreedCpu"] = vm.Spec.CpuCores,
                ["FreedMemoryMb"] = vm.Spec.MemoryMb,
                ["FreedStorageGb"] = vm.Spec.DiskGb
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

                // Update metrics if provided
                if (reported.CpuUsagePercent.HasValue)
                {
                    vm.LatestMetrics = new VmMetrics
                    {
                        CpuUsagePercent = reported.CpuUsagePercent.Value,
                        Timestamp = DateTime.UtcNow
                    };
                    await _dataStore.SaveVmAsync(vm);
                }

                // Update access info if available
                if (!string.IsNullOrEmpty(reported.IpAddress) &&
                    vm.NetworkConfig.PrivateIp != reported.IpAddress)
                {
                    vm.AccessInfo ??= new VmAccessInfo();
                    vm.AccessInfo.SshHost = reported.IpAddress;
                    vm.AccessInfo.SshPort = int.Parse(reported.VncPort);

                    if (!string.IsNullOrEmpty(reported.VncPort))
                    {
                        // VNC accessible through WireGuard at node IP
                        vm.AccessInfo.VncHost = node?.PublicIp ?? reported.IpAddress;
                        vm.AccessInfo.VncPort = int.Parse(reported.VncPort);
                    }

                    // Update network config with actual libvirt IP
                    vm.NetworkConfig.PrivateIp = reported.IpAddress;

                    await _dataStore.SaveVmAsync(vm);
                }
            }
            else if (!string.IsNullOrEmpty(reported.TenantId))
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
                vmId, nodeId, reported.TenantId, reported.State);

            var recoveredVm = new VirtualMachine
            {
                Id = vmId,
                Name = reported.Name ?? $"recovered-{vmId[..8]}",
                OwnerId = reported.TenantId ?? "unknown",
                NodeId = nodeId,
                Status = ParseVmStatus(reported.State),
                PowerState = ParsePowerState(reported.State),
                StartedAt = reported.StartedAt,
                CreatedAt = reported.StartedAt ?? DateTime.UtcNow,
                NetworkConfig = new VmNetworkConfig
                {
                    PrivateIp = reported.IpAddress ?? "",
                    Hostname = reported.Name ?? "",
                    //PublicIp = ,
                    //PortMappings = [reported.VncPort],
                    //OverlayNetworkId = 
                },
                Spec = new VmSpec
                {
                    CpuCores = reported.VCpus ?? 1,
                    MemoryMb = reported.MemoryBytes.HasValue
                        ? reported.MemoryBytes.Value / 1024 / 1024
                        : 1024,
                    DiskGb = reported.DiskBytes.HasValue
                        ? reported.DiskBytes.Value / 1024 / 1024 / 1024
                        : 20,
                    ImageId = "unknown",
                    EncryptedPassword = reported.EncryptedPassword ?? null
                },
                StatusMessage = "Recovered from node heartbeat after orchestrator restart",
                Labels = new Dictionary<string, string>
                {
                    ["recovered"] = "true",
                    ["recovery-date"] = DateTime.UtcNow.ToString("O"),
                    ["recovery-node"] = nodeId
                }
            };

            if (reported.CpuUsagePercent.HasValue)
            {
                recoveredVm.LatestMetrics = new VmMetrics
                {
                    CpuUsagePercent = reported.CpuUsagePercent.Value,
                    Timestamp = DateTime.UtcNow
                };
            }

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

    // ============================================================================
    // Utilities
    // ============================================================================

    private string GenerateDeterministicNodeId(string machineId, string walletAddress)
    {
        var input = $"{machineId.ToLowerInvariant()}:{walletAddress.ToLowerInvariant()}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return $"node-{Convert.ToHexString(hash[..8]).ToLowerInvariant()}";
    }

    private string GenerateAuthToken()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }

    private static string SafeSubstring(string s, int maxLength)
    {
        return s.Length > maxLength ? s[..maxLength] : s;
    }
}