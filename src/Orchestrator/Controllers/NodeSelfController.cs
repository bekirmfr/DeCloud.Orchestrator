using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;
using DeCloud.Shared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Interfaces;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;
using Orchestrator.Services;
using Orchestrator.Services.Payment;
using Orchestrator.Services.SystemVm;
using Orchestrator.Services.VmScheduling;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/nodes/me")]
[Authorize(Roles = "node")]
public class NodeSelfController : ControllerBase
{
    private readonly DataStore _dataStore;
    private readonly ISchedulingConfigService _configService;
    private readonly INodeService _nodeService;
    private readonly IDhtNodeService _dhtNodeService;
    private readonly IRelayNodeService _relayNodeService;
    private readonly NodePerformanceEvaluator _evaluator;
    private readonly NodeCapacityCalculator _capacityCalculator;
    private readonly INodeMarketplaceService _marketplaceService;
    private readonly SystemVmObligationService _reconciler;
    private readonly ICentralIngressService _ingressService;
    private readonly ICloudInitRenderer _cloudInitRenderer;
    private readonly PricingConfig _pricingConfig;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NodeSelfController> _logger;

    public NodeSelfController(
        DataStore dataStore,
        ISchedulingConfigService configService,
        INodeService nodeService,
        IDhtNodeService dhtNodeService,
        IRelayNodeService relayNodeService,
        NodePerformanceEvaluator evaluator,
        NodeCapacityCalculator capacityCalculator,
        INodeMarketplaceService marketplaceService,
        SystemVmObligationService reconciler,
        ICentralIngressService ingressService,
        ICloudInitRenderer cloudInitRenderer,
        PricingConfig pricingConfig,
        IConfiguration configuration,
        ILogger<NodeSelfController> logger)
    {
        _dataStore = dataStore;
        _configService = configService;
        _nodeService = nodeService;
        _dhtNodeService = dhtNodeService;
        _relayNodeService = relayNodeService;
        _evaluator = evaluator;
        _capacityCalculator = capacityCalculator;
        _marketplaceService = marketplaceService;
        _reconciler = reconciler;
        _ingressService = ingressService;
        _cloudInitRenderer = cloudInitRenderer;
        _pricingConfig = pricingConfig;
        _configuration = configuration;
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
            Region = node.Locality.Region,
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
    /// Returns the current identity state JSON for one obligation role.
    /// Called by the node agent after a heartbeat signals ObligationStatesPending.
    ///
    /// SECURITY: The response contains private keys (WireGuard, Ed25519).
    /// The endpoint is [Authorize(Roles = "node")] so only the authenticated
    /// node agent for this node can retrieve its own state.
    /// State JSON is never written to server-side logs — only role + version.
    /// </summary>
    [HttpGet("obligations/{role}/state")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetObligationState(string role)
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var canonical = ObligationRole.Canonicalise(role);
        if (canonical is null)
            return BadRequest($"Unknown role '{role}'. Valid values: relay, dht, blockstore.");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node is null)
            return NotFound("Node not registered");

        var obligation = node.SystemVmObligations
            .FirstOrDefault(o => o.Role.ToString().Equals(canonical, StringComparison.OrdinalIgnoreCase)
                              || SystemVmRoleMap.ToCanonicalName(o.Role) == canonical);

        if (obligation is null || string.IsNullOrEmpty(obligation.StateJson))
        {
            _logger.LogDebug(
                "GetObligationState [{Role}] for node {NodeId}: no state found",
                canonical, nodeId);
            return NotFound($"No obligation state found for role '{canonical}'.");
        }

        // Log role + version only — never the state JSON content.
        _logger.LogDebug(
            "GetObligationState [{Role}] v{Version} served to node {NodeId}",
            canonical, obligation.StateVersion, nodeId);

        return Content(obligation.StateJson, "application/json");
    }

    /// <summary>
    /// Updates the persisted identity state for one obligation role.
    /// Intended for admin key rotation. Enforces version > stored invariant.
    ///
    /// Body: { "stateJson": "...", "version": N }
    ///
    /// Returns 409 Conflict if the incoming version is not strictly greater
    /// than the currently stored version.
    /// </summary>
    [HttpPut("obligations/{role}/state")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> UpdateObligationState(
        string role,
        [FromBody] UpdateObligationStateRequest request)
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var canonical = ObligationRole.Canonicalise(role);
        if (canonical is null)
            return BadRequest($"Unknown role '{role}'. Valid values: relay, dht, blockstore.");

        if (string.IsNullOrWhiteSpace(request.StateJson))
            return BadRequest("StateJson must not be empty.");

        if (request.Version < 1)
            return BadRequest("Version must be >= 1.");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node is null)
            return NotFound("Node not registered");

        var obligation = node.SystemVmObligations
            .FirstOrDefault(o => SystemVmRoleMap.ToCanonicalName(o.Role) == canonical);

        if (obligation is null)
            return NotFound($"No obligation of role '{canonical}' assigned to this node.");

        if (request.Version <= obligation.StateVersion)
        {
            _logger.LogWarning(
                "UpdateObligationState [{Role}] rejected for node {NodeId}: " +
                "incoming v{Incoming} <= stored v{Stored}",
                canonical, nodeId, request.Version, obligation.StateVersion);
            return Conflict(new
            {
                error = "VERSION_CONFLICT",
                stored = obligation.StateVersion,
                detail = $"Incoming version {request.Version} must be greater than stored version {obligation.StateVersion}."
            });
        }

        obligation.StateJson = request.StateJson;
        obligation.StateVersion = request.Version;
        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "ObligationState [{Role}] updated to v{Version} for node {NodeId}",
            canonical, request.Version, nodeId);

        return Ok(new { role = canonical, version = request.Version });
    }

    public class UpdateObligationStateRequest
    {
        /// <summary>JSON-serialised identity state blob.</summary>
        public string StateJson { get; init; } = string.Empty;

        /// <summary>Monotonic version — must be strictly greater than stored.</summary>
        public int Version { get; init; }
    }

    /// <summary>
    /// Get node performance evaluation and tier eligibility
    /// </summary>
    [HttpGet("performance")]
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
    /// Evaluate node performance, assign obligations, and prepare system VMs.
    /// This is a primary lifecycle step — called after register, before login.
    /// </summary>
    [HttpPost("evaluate")]
    [ProducesResponseType(typeof(EvaluateNodeResponse), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<EvaluateNodeResponse>> Evaluate(
        CancellationToken ct,
        [FromBody] HardwareInventory inventory)
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return NotFound("Node not registered");

        _logger.LogInformation("Node {NodeId} requested evaluation", nodeId);

        inventory.NodeId = nodeId;

        // ── Step 1: Benchmark ────────────────────────────────────────────
        var evaluation = await _evaluator.EvaluateNodeAsync(inventory, ct);
        if (evaluation == null)
            return StatusCode(500, "Failed to evaluate node performance");

        if (!evaluation.IsAcceptable)
        {
            return BadRequest($"Node performance below minimum requirements: {evaluation.RejectionReason}");
        }

        node.HardwareInventory = inventory;
        node.PerformanceEvaluation = evaluation;

        // Stamp physical totals — what the hardware can do.
        node.TotalResources = new ResourceSnapshot
        {
            ComputePoints = (int)evaluation.TotalComputePoints,
            MemoryBytes = inventory.Memory.TotalBytes,
            StorageBytes = inventory.Storage.Sum(s => s.TotalBytes),
            // Physical proxy-eligible VRAM baseline — used by AllocateNodeAsync
            // to resolve GpuVramPercent into a concrete byte ceiling.
            GpuVramBytes = inventory.Gpus
                .Where(g => g.IsAvailableForProxiedSharing)
                .Sum(g => g.MemoryBytes),
        };

        // If no explicit allocation config exists, resolve defaults so
        // AllocatedResources is always concrete after evaluate.
        var allocConfig = node.AllocationConfig ?? new AllocationConfig();
        node.AllocatedResources = new ResourceSnapshot
        {
            ComputePoints = (int)(node.TotalResources.ComputePoints * allocConfig.EffectiveCpuPercent),
            MemoryBytes = (long)(node.TotalResources.MemoryBytes * allocConfig.EffectiveMemoryPercent),
            StorageBytes = (long)(node.TotalResources.StorageBytes * allocConfig.EffectiveStoragePercent),
        };

        await _dataStore.SaveNodeAsync(node);

        // ── Step 2: Obligation seeding ───────────────────────────────────
        await _reconciler.EnsureObligationsAsync(node, ct);

        // ── Step 3: CGNAT relay assignment ───────────────────────────────
        if (node.HardwareInventory.Network.NatType != NatType.None && node.CgnatInfo == null)
        {
            _logger.LogInformation(
                "Node {NodeId} is behind CGNAT — assigning to relay", nodeId);

            var relay = await _relayNodeService.FindBestRelayForCgnatNodeAsync(node);
            if (relay != null)
                await _relayNodeService.AssignCgnatNodeToRelayAsync(node, relay);
            else
                _logger.LogWarning("No available relay found for CGNAT node {NodeId}", nodeId);
        }

        // ── Step 4: Generate obligation states + system templates ────────
        var obligationStates = new Dictionary<string, ObligationStatePayload>();
        var systemTemplates = new Dictionary<string, SystemVmTemplatePayload>();

        // Use the same logic that was in RegisterNodeAsync for generating
        // obligation states and system templates. This is delegated to
        // the existing helper methods on NodeService.
        // (The controller needs access to NodeService for this — inject via DI
        //  or call a dedicated service method.)
        var (states, templates) = await _nodeService.GenerateObligationPayloadsAsync(node, ct);
        obligationStates = states;
        systemTemplates = templates;

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Node {NodeId} evaluation complete — {ObligationCount} obligations, " +
            "HighestTier={Tier}, Points={Points}",
            nodeId,
            node.SystemVmObligations.Count,
            evaluation.HighestTier,
            evaluation.TotalComputePoints);

        // ── Step 5: Build response ───────────────────────────────────────
        var schedulingConfig = await _configService.GetConfigAsync(ct);
        var dhtBootstrapPeers = await _dhtNodeService.GetBootstrapPeersAsync(excludeNodeId: nodeId);

        var obligationDescriptors = node.SystemVmObligations
            .Select(o =>
            {
                var roleName = SystemVmRoleMap.ToCanonicalName(o.Role);
                if (roleName is null) return null;
                var deps = SystemVmDependencies.GetDependencies(o.Role)
                    .Select(d => SystemVmRoleMap.ToCanonicalName(d))
                    .Where(d => d is not null)
                    .Cast<string>()
                    .ToList();
                return new ObligationDescriptorPayload { Role = roleName, Deps = deps };
            })
            .Where(o => o is not null)
            .Cast<ObligationDescriptorPayload>()
            .ToList();

        return Ok(new EvaluateNodeResponse(
            evaluation,
            schedulingConfig,
            dhtBootstrapPeers,
            obligationStates,
            systemTemplates.Count > 0 ? systemTemplates : null,
            obligationDescriptors
        ));
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

    /// <summary>
    /// Get effective pricing for this node — operator's custom rates resolved
    /// against platform defaults and clamped to floor minimums. Unset fields
    /// in the operator's settings appear here as the platform default rate.
    /// </summary>
    [HttpGet("pricing")]
    [ProducesResponseType(typeof(NodePricing), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NodePricing>> GetPricing()
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return NotFound("Node not registered");

        return Ok(PricingResolver.Resolve(node.Pricing, _pricingConfig));
    }

    /// <summary>
    /// Returns the public ingress URL for every VM currently running on this node.
    /// Called by the NodeAgent dashboard to resolve real stored URLs from the DB
    /// rather than constructing them from a formula.
    ///
    /// Response shape:
    /// {
    ///   "vmId-1": "https://my-vm.vms.stackfi.tech",
    ///   "vmId-2": "https://other-vm.vms.stackfi.tech",
    ///   ...
    /// }
    /// Only VMs with an active or known ingress route are included.
    /// VMs without a route are omitted (dashboard falls back to formula).
    /// </summary>
    [HttpGet("vm-ingress")]
    [ProducesResponseType(typeof(Dictionary<string, string>), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<Dictionary<string, string>>> GetVmIngressUrls()
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return NotFound("Node not registered");

        // Get all VMs on this node from the orchestrator data store
        var vms = await _dataStore.GetVmsByNodeAsync(nodeId);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var vm in vms)
        {
            // Priority 1: active route in the central ingress service (in-memory, authoritative)
            var route = await _ingressService.GetRouteAsync(vm.Id);
            if (route != null && !string.IsNullOrEmpty(route.Subdomain))
            {
                result[vm.Id] = route.PublicUrl;  // "https://{subdomain}"
                continue;
            }

            // Priority 2: subdomain stored on the VM record itself (persisted across restarts)
            if (!string.IsNullOrEmpty(vm.IngressConfig?.DefaultSubdomain)
                && vm.IngressConfig.DefaultSubdomainEnabled)
            {
                result[vm.Id] = $"https://{vm.IngressConfig.DefaultSubdomain}";
                continue;
            }

            // Priority 3: synthesise from base domain + vm name (same formula as GenerateSubdomain)
            // This covers VMs created before ingress was enabled or before IngressConfig was written.
            if (_ingressService.IsEnabled
                && !string.IsNullOrEmpty(_ingressService.BaseDomain)
                && !string.IsNullOrEmpty(vm.Name))
            {
                result[vm.Id] = $"https://{vm.Name}.{_ingressService.BaseDomain}";
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Returns this node's SystemVmObligations array — which system VM roles
    /// are assigned to this node, their current fulfilment status, and any
    /// error information. Used by the node dashboard to show obligation state
    /// on the system VM cards.
    ///
    /// Response shape:
    /// {
    ///   "obligations": [
    ///     {
    ///       "role":         0,          // SystemVmRole int (0=Dht,1=Relay,2=BlockStore)
    ///       "roleName":     "Dht",
    ///       "vmId":         "f3f9...",  // null if not yet deployed
    ///       "status":       2,          // SystemVmStatus int (0=Pending,1=Deploying,2=Active)
    ///       "statusName":   "Active",
    ///       "failureCount": 0,
    ///       "lastError":    null,
    ///       "deployedAt":   "2026-...",
    ///       "activeAt":     "2026-...",
    ///       "runningBinaryVersion": "abc12345...", // null until first heartbeat
    ///       "currentBinaryVersion": "abc12345...", // null until binary staged
    ///       "stateVersion": 0                       // monotonic, incremented on key rotation
    ///     }
    ///   ]
    /// }
    /// </summary>
    [HttpGet("obligations")]
    [ProducesResponseType(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> GetObligations()
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return NotFound("Node not registered");

        var obligations = (node.SystemVmObligations ?? [])
            .Select(o => new
            {
                role = (int)o.Role,
                roleName = o.Role.ToString(),
                vmId = o.VmId,
                status = (int)o.Status,
                statusName = o.Status.ToString(),
                failureCount = o.FailureCount,
                lastError = o.LastError,
                deployedAt = o.DeployedAt?.ToString("O"),
                activeAt = o.ActiveAt?.ToString("O"),
                runningBinaryVersion = o.RunningBinaryVersion,
                currentBinaryVersion = o.CurrentBinaryVersion,
                stateVersion = o.StateVersion,
                templateId = o.TemplateId
            })
            .ToList();

        return Ok(new { obligations });
    }

    /// <summary>
    /// Returns the current system template payload for a role.
    /// Called by the node agent when the heartbeat response signals
    /// SystemTemplatesPending for this role.
    /// </summary>
    [HttpGet("system-templates/{role}")]
    [ProducesResponseType(typeof(SystemVmTemplatePayload), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<SystemVmTemplatePayload>> GetSystemTemplate(string role)
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId)) return Unauthorized("Invalid node token");

        var canonical = ObligationRole.Canonicalise(role);
        if (canonical is null) return BadRequest($"Unknown role '{role}'.");

        var roleEnum = SystemVmRoleMap.FromCanonicalName(canonical);
        if (roleEnum is null)
            return BadRequest($"Role '{role}' has no system VM mapping.");

        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node is null) return NotFound("Node not registered");

        var obligation = node.SystemVmObligations
            .FirstOrDefault(o => SystemVmRoleMap.ToCanonicalName(o.Role) == canonical);

        VmTemplate? template = null;
        if (!string.IsNullOrEmpty(obligation?.TemplateId))
            template = await _dataStore.GetTemplateByIdAsync(obligation.TemplateId);

        if (template is null) return BadRequest(
            $"No template assigned for role '{canonical}' yet. Ensure obligation is fulfilled and template deployed.");

        var arch = node.HardwareInventory?.Cpu?.Architecture;
        var systemTemplate = NodeService.BuildSystemVmTemplate(canonical, template, arch);

        // F4: render declared statics if the template has any. Skip strict
        // validation — system templates only declare WG_DESCRIPTION and
        // DECLOUD_ROLE for now; the other __VARNAME__ placeholders are still
        // handled by the legacy node-side LibvirtVmManager path until full
        // Phase 3 cutover.
        if (template.Variables is { Count: > 0 } && obligation is not null)
        {
            try
            {
                var orchestratorUrl = _configuration["OrchestratorClient:BaseUrl"]
                    ?? _configuration["OrchestratorUrl"]
                    ?? string.Empty;

                var ctx = new ResolutionContext(
                    Node: node,
                    Obligation: obligation,
                    Vm: null,                           // system-VM flow
                    Template: template,
                    OrchestratorUrl: orchestratorUrl,
                    TargetArchitecture: ArchitectureHelper.NormaliseArchitecture(arch),
                    UserSuppliedStatics: System.Collections.Immutable.ImmutableDictionary<string, string>.Empty);

                var rendered = await _cloudInitRenderer.RenderAsync(
                    template, ctx, HttpContext.RequestAborted, strictValidation: false);

                systemTemplate.CloudInitContent = rendered;

                _logger.LogDebug(
                    "Rendered system template '{Role}' r{Revision} for node {NodeId}: " +
                    "{VarCount} declared static(s) substituted",
                    canonical, template.Revision, nodeId, template.Variables.Count);
            }
            catch (Exception ex)
            {
                // Render failure is fatal — shipping a partially-rendered template
                // would silently break the VM at boot. Better to fail the pull
                // and let the node retry next cycle (when the orchestrator might
                // have been fixed).
                _logger.LogError(ex,
                    "Failed to render system template '{Role}' for node {NodeId}: {Message}",
                    canonical, nodeId, ex.Message);
                return StatusCode(500, $"Template render failed: {ex.Message}");
            }
        }

        var templateJson = System.Text.Json.JsonSerializer.Serialize(
            systemTemplate,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });

        return Ok(new SystemVmTemplatePayload
        {
            TemplateId = template.Id,
            TemplateJson = templateJson,
            Revision = template.Revision,
        });
    }

    /// <summary>
    /// Update pricing for this node. Rates are clamped to platform floor minimums.
    /// </summary>
    [HttpPatch("pricing")]
    [ProducesResponseType(typeof(NodePricing), 200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<NodePricing>> UpdatePricing([FromBody] NodePricing pricing)
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var update = new NodeProfileUpdate { Pricing = pricing };
        var success = await _marketplaceService.UpdateNodeProfileAsync(nodeId, update);
        if (!success)
            return NotFound("Node not registered");

        // Return the effective pricing — operator's custom rates resolved
        // against platform defaults and clamped to floor minimums.
        var node = await _dataStore.GetNodeAsync(nodeId);
        return Ok(PricingResolver.Resolve(node!.Pricing, _pricingConfig));
    }

    /// <summary>
    /// Update node profile (name, description, tags, pricing)
    /// </summary>
    [HttpPatch("profile")]
    [ProducesResponseType(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> UpdateProfile([FromBody] NodeProfileUpdate update)
    {
        var nodeId = GetNodeIdFromToken();
        if (string.IsNullOrEmpty(nodeId))
            return Unauthorized("Invalid node token");

        var success = await _marketplaceService.UpdateNodeProfileAsync(nodeId, update);
        if (!success)
            return NotFound("Node not registered");

        return Ok(new { message = "Profile updated" });
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