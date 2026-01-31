using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Services;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services.VmScheduling;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/nodes/me")]
[Authorize(Roles = "node")]
public class NodeSelfController : ControllerBase
{
    private readonly DataStore _dataStore;
    private readonly ISchedulingConfigService _configService;
    private readonly NodePerformanceEvaluator _evaluator;
    private readonly NodeCapacityCalculator _capacityCalculator;
    private readonly ILogger<NodeSelfController> _logger;

    public NodeSelfController(
        DataStore dataStore,
        ISchedulingConfigService configService,
        NodePerformanceEvaluator evaluator,
        NodeCapacityCalculator capacityCalculator,
        ILogger<NodeSelfController> logger)
    {
        _dataStore = dataStore;
        _configService = configService;
        _evaluator = evaluator;
        _capacityCalculator = capacityCalculator;
        _logger = logger;
    }

    /// <summary>
    /// Get node ID from JWT token
    /// </summary>
    private string? GetNodeIdFromToken()
    {
        return User.FindFirst("node_id")?.Value;
    }

    /// <summary>
    /// Get node summary information
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(NodeSummaryResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NodeSummaryResponse>> GetSummary()
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return NotFound("Node not registered");

        return Ok(new NodeSummaryResponse
        {
            NodeId = node.Id,
            Status = node.Status,
            Region = node.Region,
            PublicIp = node.PublicIp,
            RegisteredAt = node.RegisteredAt,
            LastHeartbeat = node.LastHeartbeat,
            AgentVersion = node.AgentVersion,
            SchedulingConfigVersion = null // TODO: Add to Node model if needed
        });
    }

    /// <summary>
    /// Get current scheduling configuration
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType(typeof(AgentSchedulingConfig), 200)]
    [ProducesResponseType(401)]
    public async Task<ActionResult<AgentSchedulingConfig>> GetConfig(CancellationToken ct)
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var config = await _configService.GetConfigAsync(ct);
        return Ok(MapToAgentConfig(config));
    }

    /// <summary>
    /// Get node performance evaluation and tier eligibility
    /// </summary>
    [HttpGet("evaluation")]
    [ProducesResponseType(typeof(NodePerformanceEvaluation), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NodePerformanceEvaluation>> GetEvaluation()
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return NotFound("Node not registered");

        if (node.PerformanceEvaluation == null)
            return NotFound("Node not yet evaluated - send a heartbeat first");

        return Ok(node.PerformanceEvaluation);
    }

    /// <summary>
    /// Get node capacity and current allocations
    /// </summary>
    [HttpGet("capacity")]
    [ProducesResponseType(typeof(NodeCapacityResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NodeCapacityResponse>> GetCapacity(CancellationToken ct)
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return NotFound("Node not registered");

        // Calculate capacity
        var capacity = await _capacityCalculator.CalculateTotalCapacityAsync(node, ct);

        // Get VMs allocated to this node
        var vms = await _dataStore.GetVmsByNodeAsync(nodeId);

        var runningVms = vms.Where(v => v.Status == VmStatus.Running).ToList();

        // Build VM breakdown
        var vmBreakdown = vms.Select(v => new VmAllocationSummary
        {
            VmId = v.Id,
            Name = v.Name,
            Tier = ((QualityTier)v.Spec.QualityTier).ToString(),
            VCpus = v.Spec.VirtualCpuCores,
            Points = v.Spec.ComputePointCost,
            MemoryBytes = v.Spec.MemoryBytes,
            Status = v.Status
        }).ToList();

        return Ok(new NodeCapacityResponse
        {
            NodeId = nodeId,

            // Physical resources
            PhysicalCores = capacity.PhysicalCores,
            PhysicalMemoryBytes = capacity.PhysicalMemoryBytes,
            PhysicalStorageBytes = capacity.PhysicalStorageBytes,

            // Point-based capacity
            PointsPerCore = capacity.BasePointsPerCore,
            TotalComputePoints = capacity.TotalComputePoints,
            AllocatedComputePoints = vms.Sum(v => v.Spec.ComputePointCost),

            // Memory
            AllocatedMemoryBytes = vms.Sum(v => v.Spec.MemoryBytes),
            AvailableMemoryBytes = capacity.TotalMemoryBytes - vms.Sum(v => v.Spec.MemoryBytes),

            // Storage
            AllocatedStorageBytes = vms.Sum(v => v.Spec.DiskBytes),
            AvailableStorageBytes = capacity.TotalStorageBytes - vms.Sum(v => v.Spec.DiskBytes),

            // VMs
            ActiveVmCount = runningVms.Count,
            VmBreakdown = vmBreakdown
        });
    }

    /// <summary>
    /// Force re-evaluation of node performance
    /// Useful after hardware changes or benchmarking issues
    /// </summary>
    [HttpPost("evaluate")]
    [ProducesResponseType(typeof(NodePerformanceEvaluation), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NodePerformanceEvaluation>> ForceEvaluation(CancellationToken ct,
        [FromBody] HardwareInventory inventory)
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return NotFound("Node not registered");

        _logger.LogInformation("Node {NodeId} requested re-evaluation", nodeId);

        inventory.NodeId = nodeId;

        // Re-run evaluation
        var evaluation = await _evaluator.EvaluateNodeAsync(inventory, ct);

        if (evaluation == null)
        {
            return StatusCode(500, "Failed to evaluate node performance");
        }

        node.HardwareInventory = inventory;
        node.PerformanceEvaluation = evaluation;

        // Persist updated node
        await _dataStore.SaveNodeAsync(node);

        return Ok(evaluation);
    }

    /// <summary>
    /// Convert full SchedulingConfig to lightweight AgentSchedulingConfig
    /// </summary>
    private AgentSchedulingConfig MapToAgentConfig(SchedulingConfig config)
    {
        return new AgentSchedulingConfig
        {
            Version = config.Version,
            BaselineBenchmark = config.BaselineBenchmark,
            BaselineOvercommitRatio = config.Tiers[QualityTier.Burstable].CpuOvercommitRatio,
            MaxPerformanceMultiplier = config.MaxPerformanceMultiplier,
            Tiers = config.Tiers,
            UpdatedAt = config.UpdatedAt
        };
    }

    // ============================================================
    // Response DTOs
    // ============================================================

    public class NodeSummaryResponse
    {
        public string NodeId { get; set; } = string.Empty;
        public NodeStatus Status { get; set; }
        public string? Region { get; set; }
        public string? PublicIp { get; set; }
        public DateTime RegisteredAt { get; set; }
        public DateTime? LastHeartbeat { get; set; }
        public string AgentVersion { get; set; } = string.Empty;
        public int? SchedulingConfigVersion { get; set; }
    }

    public class NodeCapacityResponse
    {
        public string NodeId { get; set; } = string.Empty;

        // Physical resources
        public int PhysicalCores { get; set; }
        public long PhysicalMemoryBytes { get; set; }
        public long PhysicalStorageBytes { get; set; }

        // Point-based capacity
        public double PointsPerCore { get; set; }
        public int TotalComputePoints { get; set; }
        public int AllocatedComputePoints { get; set; }
        public int AvailableComputePoints => TotalComputePoints - AllocatedComputePoints;
        public double UtilizationPercent => TotalComputePoints > 0
            ? (double)AllocatedComputePoints / TotalComputePoints * 100
            : 0;

        // Memory
        public long AllocatedMemoryBytes { get; set; }
        public long AvailableMemoryBytes { get; set; }

        // Storage
        public long AllocatedStorageBytes { get; set; }
        public long AvailableStorageBytes { get; set; }

        // VM breakdown
        public int ActiveVmCount { get; set; }
        public List<VmAllocationSummary> VmBreakdown { get; set; } = new();
    }

    public class VmAllocationSummary
    {
        public string VmId { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string Tier { get; set; } = string.Empty;
        public int VCpus { get; set; }
        public int Points { get; set; }
        public long MemoryBytes { get; set; }
        public VmStatus Status { get; set; }
    }

}