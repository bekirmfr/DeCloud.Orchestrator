using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared;
using DeCloud.Shared.Contracts;
using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using DeCloud.Shared.Utils;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Orchestrator.Interfaces;
using Orchestrator.Interfaces.VmScheduling;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;
using Orchestrator.Services.Locality;
using Orchestrator.Services.Payment;
using Orchestrator.Services.SystemVm;
using Orchestrator.Services.VmScheduling;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Orchestrator.Services;

public class NodeService : INodeService
{
    private readonly DataStore _dataStore;
    private readonly ISchedulingConfigService _configService;
    private readonly IConstraintEvaluator _constraintEvaluator;
    private readonly ILocalityService _locality;
    private readonly IEventService _eventService;
    private readonly ICentralIngressService _ingressService;
    private readonly ILogger<NodeService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IRelayNodeService _relayNodeService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ObligationStateGenerator _stateGenerator;
    private readonly IConfiguration _configuration;
    private readonly ICloudInitRenderer _cloudInitRenderer;
    private readonly PricingConfig _pricingConfig;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _cgnatSyncLocks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _registrationLocks = new();

    public NodeService(
        DataStore dataStore,
        ISchedulingConfigService configService,
        IConstraintEvaluator constraintEvaluator,
        ILocalityService locality,
        IEventService eventService,
        ICentralIngressService ingressService,
        ILogger<NodeService> logger,
        HttpClient httpClient,
        IRelayNodeService relayNodeService,
        IServiceProvider serviceProvider,
        ICloudInitRenderer cloudInitRenderer,
        IConfiguration configuration,
        IOptions<PricingConfig> pricingConfig,
        ObligationStateGenerator stateGenerator)
    {
        _dataStore = dataStore;
        _configService = configService;
        _constraintEvaluator = constraintEvaluator;
        _locality = locality;
        _eventService = eventService;
        _ingressService = ingressService;
        _logger = logger;
        _httpClient = httpClient;
        _relayNodeService = relayNodeService;
        _serviceProvider = serviceProvider;
        _cloudInitRenderer = cloudInitRenderer;
        _configuration = configuration;
        _pricingConfig = pricingConfig.Value;
        _stateGenerator = stateGenerator;
    }

    /// <summary>
    /// Process a resource allocation request from the node agent.
    /// Validates percentages, merges with existing allocation, persists.
    ///
    /// This is a JWT-authenticated operational action — no wallet required.
    /// The orchestrator stores the percentages; concrete capacity is computed
    /// at login time via <see cref="LoginNodeAsync"/>.
    /// </summary>
    public async Task<NodeAllocateResponse> AllocateNodeAsync(
        string nodeId,
        NodeAllocateRequest request,
        CancellationToken ct = default)
    {
        var node = await _dataStore.GetNodeAsync(nodeId)
            ?? throw new KeyNotFoundException($"Node {nodeId} not found");

        // Validate request
        var validationError = request.Validate();
        if (validationError != null)
        {
            return new NodeAllocateResponse
            {
                Success = false,
                Error = validationError
            };
        }

        // ── GPU allocation mode guards ────────────────────────────────────
        // GpuCount   → Passthrough only. Proxied shares one GPU across VMs;
        //              the scheduling unit is VRAM, not GPU count.
        //              The correct Proxied control is GpuVramPercent.
        // GpuVramPercent → Proxied only. Passthrough dedicates a full physical
        //              GPU per VM; there is no shared VRAM pool to partition.
        //              The correct Passthrough control is GpuCount.
        if (node.HardwareInventory?.Gpus != null)
        {
            if (request.GpuCount.HasValue)
            {
                var detectedGpus = node.HardwareInventory.Gpus.Count;
                if (request.GpuCount.Value > detectedGpus)
                    return new NodeAllocateResponse
                    {
                        Success = false,
                        Error = $"GpuCount ({request.GpuCount.Value}) exceeds detected GPUs ({detectedGpus})"
                    };

                var hasPassthrough = node.HardwareInventory.Gpus
                    .Any(g => g.IsAvailableForPassthrough);
                if (!hasPassthrough)
                    return new NodeAllocateResponse
                    {
                        Success = false,
                        Error = "GpuCount requires at least one passthrough-capable GPU " +
                                "(IOMMU + vfio-pci). Proxied-only nodes use GpuVramPercent " +
                                "to limit GPU VRAM availability."
                    };
            }

            if (request.GpuVramPercent.HasValue)
            {
                var hasProxied = node.HardwareInventory.Gpus
                    .Any(g => g.IsAvailableForProxiedSharing);
                if (!hasProxied)
                    return new NodeAllocateResponse
                    {
                        Success = false,
                        Error = "GpuVramPercent requires at least one proxy-eligible GPU. " +
                                "Passthrough-only nodes use GpuCount to limit GPU availability."
                    };
            }
        }

        // ── Store raw percentages ────────────────────────────────────────
        var config = node.AllocationConfig ?? new AllocationConfig();
        config.CpuPercent = request.CpuPercent ?? config.CpuPercent;
        config.MemoryPercent = request.MemoryPercent ?? config.MemoryPercent;
        config.StoragePercent = request.StoragePercent ?? config.StoragePercent;
        config.GpuCount = request.GpuCount ?? config.GpuCount;
        config.GpuVramPercent = request.GpuVramPercent ?? config.GpuVramPercent;
        node.AllocationConfig = config;

        // ── Resolve concrete values from physical totals ─────────────────
        // TotalResources is set at evaluate time. If not yet evaluated,
        // we can only store percentages — concrete resolution happens
        // on the next evaluate or when TotalResources becomes available.
        if (node.TotalResources.ComputePoints > 0)
        {
            // Total proxy-eligible VRAM baseline (set in TotalResources at evaluate time).
            // Falls back to inventory sum for nodes evaluated before this field existed.
            var totalProxiedVram = node.TotalResources.GpuVramBytes > 0
                ? node.TotalResources.GpuVramBytes
                : node.HardwareInventory.Gpus
                    .Where(g => g.IsAvailableForProxiedSharing)
                    .Sum(g => g.MemoryBytes);

            node.AllocatedResources = new ResourceSnapshot
            {
                ComputePoints = (int)(node.TotalResources.ComputePoints * config.EffectiveCpuPercent),
                MemoryBytes = (long)(node.TotalResources.MemoryBytes * config.EffectiveMemoryPercent),
                StorageBytes = (long)(node.TotalResources.StorageBytes * config.EffectiveStoragePercent),
                GpuVramBytes = config.GpuVramPercent.HasValue
                    ? (long)(totalProxiedVram * config.GpuVramPercent.Value)
                    : totalProxiedVram, // null = 100%
            };
        }

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Node {NodeId} allocation updated: CPU={CpuPct:P0}, Mem={MemPct:P0}, " +
            "Stor={StorPct:P0}, GpuVram={GpuVramPct} → {Points} pts, {MemGb:F1} GB RAM, " +
            "{StorGb:F1} GB storage, {VramGb:F1} GB GPU VRAM",
            nodeId,
            config.EffectiveCpuPercent,
            config.EffectiveMemoryPercent,
            config.EffectiveStoragePercent,
            config.GpuVramPercent.HasValue ? $"{config.GpuVramPercent.Value:P0}" : "100%",
            node.AllocatedResources.ComputePoints,
            node.AllocatedResources.MemoryBytes / (1024.0 * 1024 * 1024),
            node.AllocatedResources.StorageBytes / (1024.0 * 1024 * 1024),
            node.AllocatedResources.GpuVramBytes / (1024.0 * 1024 * 1024));

        return new NodeAllocateResponse
        {
            Success = true,
            EffectiveCpuPercent = config.EffectiveCpuPercent,
            EffectiveMemoryPercent = config.EffectiveMemoryPercent,
            EffectiveStoragePercent = config.EffectiveStoragePercent,
            EffectiveGpuVramPercent = config.GpuVramPercent,
            GpuCount = config.GpuCount,
            ResolvedComputePoints = node.AllocatedResources.ComputePoints,
            ResolvedMemoryBytes = node.AllocatedResources.MemoryBytes,
            ResolvedStorageBytes = node.AllocatedResources.StorageBytes,
            ResolvedGpuVramBytes = node.AllocatedResources.GpuVramBytes,
        };
    }


    // ============================================================================
    // Registration and Heartbeat
    // ============================================================================

    // Implements deterministic node ID validation and registration

    public async Task<NodeRegistrationResponse> RegisterNodeAsync(
    NodeRegistrationRequest request, CancellationToken ct = default)
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
        // STEP 1.5: Verify Wallet Signature
        // =====================================================
        if (!VerifyWalletSignature(request.WalletAddress, request.Message, request.Signature))
        {
            _logger.LogError("Node registration rejected: Invalid wallet signature");
            throw new UnauthorizedAccessException(
                "Invalid wallet signature. Please ensure you're using the correct wallet.");
        }

        // =====================================================
        // STEP 1.6: Validate Signature Freshness
        // =====================================================
        ValidateSignatureTimestamp(request.Message);

        // =====================================================
        // STEP 2: Compute Deterministic Node ID
        // =====================================================
        var nodeId = NodeIdGenerator.GenerateNodeId(request.MachineId, request.WalletAddress);

        // =====================================================
        // STEP 3: Resolve Locality
        // =====================================================
        var country = string.IsNullOrWhiteSpace(request.Country)
            ? "ZZ"
            : request.Country.ToUpperInvariant().Trim();

        if (country != "ZZ" && !_locality.IsValidCountryCode(country))
        {
            throw new ArgumentException(
                $"Invalid country code '{request.Country}'. " +
                $"Use ISO 3166-1 alpha-2 (e.g. 'TR', 'DE', 'US').");
        }

        var region = request.Region ?? "default";
        if (region != "default" && !_locality.IsValidRegionCode(region))
        {
            throw new ArgumentException(
                $"Invalid region '{region}'. " +
                $"Valid regions: [{string.Join(", ", _locality.Regions.Select(r => r.Code))}].");
        }

        var zone = request.Zone ?? "default";
        if (zone != "default" && !_locality.IsValidZone(zone, region))
        {
            throw new ArgumentException(
                $"Invalid zone '{zone}'. Zone must follow format '<region>-<n>'.");
        }

        var jurisdictionTags = _locality.GetTagsForCountry(country).ToList();

        var locality = new NodeLocality
        {
            Country = country,
            JurisdictionTags = jurisdictionTags,
            Region = region == "default" ? "unknown" : region,
            Zone = zone == "default" ? null : zone,
            IpDerivedCountry = null,
            LocationMismatch = false,
        };

        // =====================================================
        // STEP 4: Build Node Record
        // =====================================================
        var jti = Guid.NewGuid().ToString();
        var apiKey = GenerateNodeJwtToken(nodeId, request.WalletAddress, request.MachineId, jti);
        var apiKeyHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(apiKey)));

        var existingNode = await _dataStore.GetNodeAsync(nodeId);

        var node = new Node
        {
            Id = nodeId,
            MachineId = request.MachineId,
            Name = request.Name,
            WalletAddress = request.WalletAddress,
            CurrentJti = jti,
            PublicIp = request.PublicIp,
            AgentPort = request.AgentPort,
            Status = NodeStatus.Online,
            // Target lifecycle: operator calls login separately after evaluate.
            IsSchedulingReady = false,
            HardwareInventory = request.HardwareInventory,
            TotalResources = existingNode?.TotalResources ?? new ResourceSnapshot(),
            ReservedResources = new ResourceSnapshot(),
            UsedResources = existingNode?.UsedResources ?? new ResourceSnapshot(),
            AgentVersion = request.AgentVersion,
            SupportedImages = request.SupportedImages,
            Locality = locality,
            RegisteredAt = existingNode?.RegisteredAt ?? request.RegisteredAt,
            LastSeenAt = DateTime.UtcNow,
            SshCaPublicKey = request.SshCaPublicKey ?? existingNode?.SshCaPublicKey,
            Pricing = request.Pricing ?? new NodePricing(),
            // Preserve existing state on re-registration
            SystemVmObligations = existingNode?.SystemVmObligations ?? new List<SystemVmObligation>(),
            PerformanceEvaluation = existingNode?.PerformanceEvaluation,
            AllocationConfig = existingNode?.AllocationConfig,
            AllocatedResources = existingNode?.AllocatedResources,
            DhtInfo = existingNode?.DhtInfo,
            RelayInfo = existingNode?.RelayInfo,
            CgnatInfo = existingNode?.CgnatInfo,
            RegisteredSettingsHash = SettingsHash.Compute(
                request.WalletAddress,
                country,
                region == "default" ? "unknown" : region)
        };

        // =====================================================
        // STEP 5: Save and issue credentials
        // =====================================================
        node.ApiKeyHash = apiKeyHash;
        node.ApiKeyCreatedAt = DateTime.UtcNow;
        node.ApiKeyLastUsedAt = null;

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation("✓ Node registered: {NodeId} (SchedulingReady=false)", node.Id);

        // =====================================================
        // STEP 6: VM compliance check (re-registration only)
        // =====================================================
        var nonCompliantVms = new List<NonCompliantVmInfo>();
        if (existingNode != null)
        {
            var localityChanged =
                !string.Equals(existingNode.Locality?.Country, node.Locality?.Country, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existingNode.Locality?.Region, node.Locality?.Region, StringComparison.OrdinalIgnoreCase);

            if (localityChanged)
            {
                nonCompliantVms = await FlagNonCompliantVmsAsync(node, ct);
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
                ["region"] = node.Locality.Region,
                ["machineId"] = node.MachineId,
                ["wallet"] = node.WalletAddress
            }
        });

        var orchestratorPublicKey = _configuration["WireGuard:PublicKey"] ?? "";

        return new NodeRegistrationResponse(
            node.Id,
            apiKey,
            orchestratorPublicKey,
            TimeSpan.FromSeconds(15),
            nonCompliantVms.Count > 0 ? nonCompliantVms : null
        );
    }


    public async Task<NodeDeregisterResponse> DeregisterNodeAsync(
    string nodeId, bool force, string reason,
    CancellationToken ct = default)
    {
        var node = await _dataStore.GetNodeAsync(nodeId)
            ?? throw new KeyNotFoundException($"Node {nodeId} not found");

        // Check for running tenant VMs
        var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
        var tenantVms = nodeVms.Where(v =>
            v.Role == VmRole.General &&
            (v.Status == VmStatus.Running || v.Status == VmStatus.Provisioning))
            .ToList();

        if (tenantVms.Count > 0 && !force)
        {
            throw new InvalidOperationException(
                $"Node has {tenantVms.Count} running tenant VM(s). " +
                "Use --force to deregister anyway, or drain VMs first " +
                "with 'decloud vm drain'.");
        }

        // Revoke JWT
        if (!string.IsNullOrEmpty(node.CurrentJti))
        {
            var revocationService = _serviceProvider
                .GetRequiredService<IJwtRevocationService>();
            await revocationService.RevokeAsync(
                node.CurrentJti, nodeId, $"deregister: {reason}", ct);
        }

        // Remove node record
        try
        {
            await _dataStore.DeleteNodeAsync(nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete node {NodeId} from store", nodeId);
        }

        _logger.LogInformation(
            "Node {NodeId} deregistered: reason={Reason}, force={Force}, " +
            "tenantVms={TenantVmCount}",
            nodeId, reason, force, tenantVms.Count);

        return new NodeDeregisterResponse
        {
            Deregistered = true,
            TenantVmsDestroyed = force ? tenantVms.Count : 0,
            Message = tenantVms.Count > 0 && force
                ? $"{tenantVms.Count} tenant VM(s) will be orphaned"
                : null
        };
    }

    // ============================================================================
    // Login / Logout (scheduling readiness)
    // ============================================================================

    public async Task<NodeLoginResponse> LoginNodeAsync(string nodeId, CancellationToken ct = default)
    {
        var node = await _dataStore.GetNodeAsync(nodeId)
            ?? throw new KeyNotFoundException($"Node {nodeId} not found");

        // -----------------------------------------------------------------
        // Precondition: settings drift check
        // -----------------------------------------------------------------
        if (!string.IsNullOrEmpty(node.RegisteredSettingsHash) &&
            !string.IsNullOrEmpty(node.LatestHeartbeatSettingsHash) &&
            !string.Equals(node.RegisteredSettingsHash,
                node.LatestHeartbeatSettingsHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Settings drift detected — local settings do not match registered state. " +
                "Run 'decloud register' to re-commit settings before logging in.");
        }

        // -----------------------------------------------------------------
        // Precondition: evaluation must exist
        // -----------------------------------------------------------------
        if (node.PerformanceEvaluation == null || !node.PerformanceEvaluation.IsAcceptable)
        {
            throw new InvalidOperationException(
                "Node has not been evaluated. Run 'decloud evaluate' first.");
        }

        // -----------------------------------------------------------------
        // Precondition: allocation must be resolved
        // AllocatedResources is set at allocate time. If the operator hasn't
        // run allocate, apply defaults from TotalResources now.
        // -----------------------------------------------------------------
        if (node.AllocatedResources.ComputePoints == 0 && node.TotalResources.ComputePoints > 0)
        {
            var pct = node.AllocationConfig ?? new AllocationConfig();
            node.AllocatedResources = new ResourceSnapshot
            {
                ComputePoints = (int)(node.TotalResources.ComputePoints * pct.EffectiveCpuPercent),
                MemoryBytes = (long)(node.TotalResources.MemoryBytes * pct.EffectiveMemoryPercent),
                StorageBytes = (long)(node.TotalResources.StorageBytes * pct.EffectiveStoragePercent),
            };

            _logger.LogInformation(
                "Node {NodeId} had no resolved allocation — applied defaults at login",
                nodeId);
        }

        // -----------------------------------------------------------------
        // Validate: used resources must not exceed allocated capacity
        // -----------------------------------------------------------------
        if (node.UsedResources.ComputePoints > node.AllocatedResources.ComputePoints)
        {
            throw new InvalidOperationException(
                $"Current CPU usage ({node.UsedResources.ComputePoints} pts) exceeds " +
                $"allocated capacity ({node.AllocatedResources.ComputePoints} pts). " +
                "Drain or delete VMs first, then login.");
        }

        if (node.UsedResources.MemoryBytes > node.AllocatedResources.MemoryBytes)
        {
            throw new InvalidOperationException(
                $"Current memory usage ({node.UsedResources.MemoryBytes / (1024 * 1024)} MB) exceeds " +
                $"allocated capacity ({node.AllocatedResources.MemoryBytes / (1024 * 1024)} MB). " +
                "Drain or delete VMs first, then login.");
        }

        // -----------------------------------------------------------------
        // Activate scheduling — no computation, just a gate
        // -----------------------------------------------------------------
        node.IsSchedulingReady = true;
        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Node {NodeId} logged in — scheduling resumed. " +
            "Allocated: {Points} pts, {MemGb:F1} GB RAM, {StorGb:F1} GB storage",
            nodeId,
            node.AllocatedResources.ComputePoints,
            node.AllocatedResources.MemoryBytes / (1024.0 * 1024 * 1024),
            node.AllocatedResources.StorageBytes / (1024.0 * 1024 * 1024));

        return new NodeLoginResponse
        {
            SchedulingReady = true,
            SettingsDriftWarning = null
        };
    }


    public async Task<NodeLogoutResponse> LogoutNodeAsync(string nodeId, CancellationToken ct = default)
    {
        var node = await _dataStore.GetNodeAsync(nodeId)
            ?? throw new KeyNotFoundException($"Node {nodeId} not found");

        node.IsSchedulingReady = false;
        await _dataStore.SaveNodeAsync(node);

        // Count VMs still running on this node so the operator knows
        // whether a drain is needed before uninstall.
        var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
        var activeCount = nodeVms.Count(v =>
            v.Status == VmStatus.Running || v.Status == VmStatus.Provisioning);

        _logger.LogInformation(
            "Node {NodeId} logged out — scheduling paused ({ActiveVms} active VMs remain)",
            nodeId, activeCount);

        return new NodeLogoutResponse
        {
            SchedulingReady = false,
            ActiveVmCount = activeCount
        };
    }

    // ============================================================================
    // Obligation States Management
    // ============================================================================

    /// <summary>
    /// Generate obligation identity states and system VM templates for the
    /// node's current obligations. Used by the evaluate endpoint.
    /// Passes empty version dictionaries so all states/templates are returned.
    /// </summary>
    public async Task<(Dictionary<string, ObligationStatePayload>, Dictionary<string, SystemVmTemplatePayload>)>
        GenerateObligationPayloadsAsync(Node node, CancellationToken ct = default)
    {
        // Empty dictionaries = "node has nothing cached, send everything"
        var emptyVersions = new Dictionary<string, int>();

        var obligationStates = GenerateAndAttachObligationStates(node, emptyVersions);
        var systemTemplates = await GenerateSystemTemplatePayloads(node, emptyVersions);

        return (obligationStates, systemTemplates);
    }

    /// <summary>
    /// For each obligation on <paramref name="node"/>, generate fresh identity
    /// state if none exists (StateVersion == 0), or re-send existing state if
    /// the node's reported version is lower than the stored version.
    ///
    /// Mutates <c>node.SystemVmObligations[].StateJson / StateVersion</c> in place
    /// so the caller's subsequent <c>SaveNodeAsync(node)</c> persists the state
    /// to MongoDB.
    ///
    /// Returns a dictionary of payloads to include in the registration response —
    /// only roles where the orchestrator has a version the node hasn't seen yet.
    /// </summary>
    private Dictionary<string, ObligationStatePayload> GenerateAndAttachObligationStates(
        Node node,
        Dictionary<string, int> nodeReportedVersions)
    {
        var result = new Dictionary<string, ObligationStatePayload>(StringComparer.OrdinalIgnoreCase);

        foreach (var obligation in node.SystemVmObligations)
        {
            // Derive the canonical role name used as dictionary key and SQLite PK.
            var roleName = SystemVmRoleMap.ToCanonicalName(obligation.Role);

            if (roleName is null)
                continue;   // Ingress and other future roles handled later

            // Determine what version the node currently has.
            nodeReportedVersions.TryGetValue(roleName, out var nodeVersion);

            // Generate fresh state if this is the first time (StateVersion == 0).
            if (obligation.StateVersion == 0 || string.IsNullOrEmpty(obligation.StateJson))
            {
                try
                {
                    var state = _stateGenerator.GenerateState(obligation.Role, node);
                    var stateJson = System.Text.Json.JsonSerializer.Serialize(
                        state,
                        state.GetType(),
                        new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                        });

                    obligation.StateJson = stateJson;
                    obligation.StateVersion = state.Version; // always 1 on first generation

                    // Stamp AuthToken on the obligation for fast lookup by callback
                    // controllers (DhtController.DhtJoin, BlockStoreController.Join).
                    // StateJson contains the token but requires deserialization — the
                    // top-level field avoids that cost on every inbound callback.
                    obligation.AuthToken = state switch
                    {
                        DhtObligationState dht => dht.AuthToken,
                        BlockStoreObligationState bs => bs.AuthToken,
                        RelayObligationState relay => relay.AuthToken,
                        _ => null
                    };

                    _logger.LogInformation(
                        "Generated initial obligation state for role {Role} on node {NodeId} (v{Version})",
                        obligation.Role, node.Id, state.Version);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to generate state for {Role} on node {NodeId} — skipping",
                        obligation.Role, node.Id);
                    continue;
                }
            }

            // Include in response if orchestrator version > node-reported version.
            if (obligation.StateVersion > nodeVersion)
            {
                result[roleName] = new ObligationStatePayload
                {
                    StateJson = obligation.StateJson!,
                    Version = obligation.StateVersion,
                };

                _logger.LogDebug(
                    "Including {Role} state v{OrchestratorVersion} in registration response " +
                    "(node has v{NodeVersion})",
                    roleName, obligation.StateVersion, nodeVersion);
            }
            else
            {
                _logger.LogDebug(
                    "{Role} state is current on node (v{Version}) — skipping payload",
                    roleName, obligation.StateVersion);
            }
        }

        return result;
    }

    /// <summary>
    /// Build system template payloads for the registration response.
    /// Only includes roles where the orchestrator has a higher revision than
    /// what the node reported. Parallel to GenerateAndAttachObligationStates.
    /// </summary>
    private async Task<Dictionary<string, SystemVmTemplatePayload>> GenerateSystemTemplatePayloads(
        Node node,
        Dictionary<string, int> nodeReportedRevisions)
    {
        var result = new Dictionary<string, SystemVmTemplatePayload>(StringComparer.OrdinalIgnoreCase);

        foreach (var obligation in node.SystemVmObligations)
        {
            var roleName = SystemVmRoleMap.ToCanonicalName(obligation.Role);
            if (roleName is null) continue;

            // Prefer stable _id lookup; fall back to slug for obligations created
            // before TemplateId was introduced (null TemplateId = legacy path).
            VmTemplate? template = null;
            if (!string.IsNullOrEmpty(obligation.TemplateId))
            {
                template = await _dataStore.GetTemplateByIdAsync(obligation.TemplateId);
            }

            if (template is null)
            {
                // Legacy fallback: look up by slug and stamp TemplateId for next time.
                var slug = SystemVmRoleMap.ToTemplateSlug(obligation.Role);
                if (slug is null) continue;
                template = await _dataStore.GetTemplateBySlugAsync(slug);
                if (template is not null)
                {
                    obligation.TemplateId = template.Id;
                    // Node will be saved by the caller after this method returns.
                }
            }

            if (template is null) continue;

            nodeReportedRevisions.TryGetValue(roleName, out var nodeRevision);
            if (template.Revision <= nodeRevision) continue;  // node already current

            // Build the lightweight SystemVmTemplate from the full VmTemplate.
            var arch = node.HardwareInventory?.Cpu?.Architecture;
            var systemTemplate = BuildSystemVmTemplate(roleName, template, arch, _dataStore);

            // F4: render declared statics if the template has any. Same logic as
            // NodeSelfController.GetSystemTemplate; both call sites need to render
            // because both ship templates to the node.
            if (template.Variables is { Count: > 0 })
            {
                try
                {
                    var orchestratorUrl = _configuration["OrchestratorClient:BaseUrl"]
                        ?? _configuration["OrchestratorUrl"]
                        ?? string.Empty;

                    var ctx = new ResolutionContext(
                        Node: node,
                        Obligation: obligation,
                        Vm: null,
                        Template: template,
                        OrchestratorUrl: orchestratorUrl,
                        TargetArchitecture: ArchitectureHelper.NormaliseArchitecture(arch),
                        UserSuppliedStatics: System.Collections.Immutable.ImmutableDictionary<string, string>.Empty);

                    systemTemplate.CloudInitContent = await _cloudInitRenderer.RenderAsync(
                        template, ctx, ct: default, strictValidation: false);
                }
                catch (Exception ex)
                {
                    // Render failure: skip this role's template in the registration
                    // response. The node won't store an outdated/broken template and
                    // will retry via heartbeat on the next cycle.
                    _logger.LogError(ex,
                        "Failed to render system template '{Role}' for node {NodeId}: {Message} — " +
                        "skipping template in registration response, node will retry via heartbeat.",
                        roleName, node.Id, ex.Message);
                    continue;
                }
            }

            var templateJson = JsonSerializer.Serialize(
                systemTemplate,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            result[roleName] = new SystemVmTemplatePayload
            {
                TemplateId = template.Id,
                TemplateJson = templateJson,
                Revision = template.Revision,
            };

            _logger.LogInformation(
                "Including system template for {Role} r{Revision} in registration response " +
                "(node has r{NodeRevision})",
                roleName, template.Revision, nodeRevision);
        }

        return result;
    }

    /// <summary>
    /// Extract the deployment-relevant subset of a full VmTemplate into a
    /// SystemVmTemplate for delivery to the node agent.
    ///
    /// Resolves the base image (URL + SHA256) from the consolidated image
    /// registry (<c>DataStore.Images</c>) — see BASE_IMAGE_DESIGN.md §7.
    /// </summary>
    internal static SystemVmTemplate BuildSystemVmTemplate(
        string role,
        VmTemplate template,
        string? nodeArchitecture,
        DataStore dataStore)
    {
        var canonical = ObligationRole.Canonicalise(role);
        var systemRole = canonical is not null
            ? SystemVmRoleMap.FromCanonicalName(canonical)
            : null;

        var imageId = systemRole.HasValue
            ? SystemVmRoleMap.ToBaseImageId(systemRole.Value)
            : string.Empty;

        // Resolve (URL, SHA256) for the node's architecture. Empty hash is
        // permissive on first use; the node reports the computed hash back
        // via heartbeat. See BASE_IMAGE_DESIGN.md §4.5.
        var archTag = VmImage.NormaliseArchTag(nodeArchitecture) ?? "amd64";
        var descriptor =
            !string.IsNullOrEmpty(imageId) &&
            dataStore.Images.TryGetValue(imageId, out var image)
                ? image.ByArchitecture.GetValueOrDefault(archTag)
                : null;
        var baseImageUrl = descriptor?.Url ?? string.Empty;
        var baseImageHash = descriptor?.Sha256 ?? string.Empty;

        return new SystemVmTemplate
        {
            Role = role,
            TemplateId = template.Id,
            Revision = template.Revision,
            CloudInitContent = template.CloudInitTemplate,
            Variables = template.Variables ?? new List<TemplateVariable>(),
            Artifacts = template.Artifacts,
            VirtualCpuCores = template.RecommendedSpec?.VirtualCpuCores ?? 1,
            MemoryBytes = template.RecommendedSpec?.MemoryBytes ?? (512L * 1024 * 1024),
            DiskBytes = template.RecommendedSpec?.DiskBytes ?? (2L * 1024 * 1024 * 1024),
            QualityTier = (int)(template.RecommendedSpec?.QualityTier ?? QualityTier.Burstable),
            ComputePointCost = template.RecommendedSpec?.ComputePointCost ?? 1,
            BaseImageUrl = baseImageUrl,
            BaseImageHash = baseImageHash,
            Services = template.ExposedPorts.Select(p => new SystemVmServiceDeclaration
            {
                Name = p.Description ?? p.Port.ToString(),
                Port = p.Port,
                Protocol = p.Protocol,
                CheckType = p.ReadinessCheck?.Strategy.ToString() ?? "CloudInitDone",
                HttpPath = p.ReadinessCheck?.HttpPath,
                LivenessCheck = p.ReadinessCheck?.LivenessCheck ?? false,
                TimeoutSeconds = p.ReadinessCheck?.TimeoutSeconds ?? 300,
            }).Prepend(new SystemVmServiceDeclaration
            {
                Name = "Guest Agent",
                CheckType = "GuestAgentPing",
                LivenessCheck = true,
                TimeoutSeconds = 300,
            }).Prepend(new SystemVmServiceDeclaration
            {
                Name = "System",
                CheckType = "CloudInitDone",
                TimeoutSeconds = 300,
            }).ToList(),
            UpdatedAt = template.UpdatedAt,
        };
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
    /// Extract and validate the ISO 8601 timestamp from the canonical
    /// signing message. Returns:
    ///   "VALID"         — timestamp within acceptable window
    ///   "EXPIRED"       — timestamp too old (> 5 min + 2 min skew)
    ///   "FUTURE"        — timestamp too far in the future (> 2 min skew)
    ///   "LEGACY_FORMAT" — message doesn't contain a parseable ISO timestamp
    ///                     (pre-canonical format, skip validation)
    ///   null            — couldn't determine (treat as legacy)
    /// </summary>
    private static string? ValidateSignatureTimestamp(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        // Look for "Timestamp:  2026-05-09T11:42:30Z" in the canonical format.
        // The legacy format uses "Timestamp: {unix_epoch}" which won't parse
        // as ISO 8601, so it falls through to LEGACY_FORMAT.
        const string prefix = "Timestamp:";
        var lines = message.Split('\n');
        string? timestampStr = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                timestampStr = trimmed.Substring(prefix.Length).Trim();
                break;
            }
        }

        if (string.IsNullOrEmpty(timestampStr))
            return "LEGACY_FORMAT";

        // Try ISO 8601 parse
        if (!DateTime.TryParse(timestampStr, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var signedAt))
        {
            // Could be a Unix epoch from legacy format — not an error
            return "LEGACY_FORMAT";
        }

        var now = DateTime.UtcNow;
        var age = now - signedAt;

        // Allow ±2 minutes for clock skew
        const int skewToleranceMinutes = 2;
        const int validityWindowMinutes = 5;

        if (age.TotalMinutes > (validityWindowMinutes + skewToleranceMinutes))
            return "EXPIRED";

        if (age.TotalMinutes < -skewToleranceMinutes)
            return "FUTURE";

        return "VALID";
    }

    /// <summary>
    /// Process heartbeat without overwriting orchestrator resource tracking
    /// </summary>
    public async Task<NodeHeartbeatResponse> ProcessHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat,
    CancellationToken ct = default)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
        {
            return new NodeHeartbeatResponse(false, null, null, null);
        }

        var currentConfig = await _configService.GetConfigAsync(ct);

        var wasOffline = node.Status == NodeStatus.Offline;
        node.Status = NodeStatus.Online;
        node.LastHeartbeat = DateTime.UtcNow;
        node.LatestMetrics = heartbeat.Metrics;
        node.LastSeenAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(heartbeat.AgentVersion))
        {
            node.AgentVersion = heartbeat.AgentVersion;
        }

        // Persist the reported hash so login can gate on it
        if (!string.IsNullOrEmpty(heartbeat.SettingsHash))
        {
            node.LatestHeartbeatSettingsHash = heartbeat.SettingsHash;
        }

        // =====================================================
        // Settings drift detection
        //
        // Compare the node's reported settings hash against what we
        // stored at registration. Mismatch means the operator edited
        // /etc/decloud/settings locally without re-registering.
        //
        // Null hash from agent = pre-feature agent, skip check.
        // Null hash on node = pre-feature registration, skip check.
        // =====================================================
        SettingsDriftInfo? settingsDrift = null;

        if (!string.IsNullOrEmpty(heartbeat.SettingsHash) &&
            !string.IsNullOrEmpty(node.RegisteredSettingsHash))
        {
            if (!string.Equals(heartbeat.SettingsHash,
                    node.RegisteredSettingsHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                settingsDrift = new SettingsDriftInfo
                {
                    Message = "Local settings hash does not match registered state. " +
                              "Run 'decloud register' to re-commit, or revert local edits.",
                    ExpectedHash = node.RegisteredSettingsHash,
                    ReportedHash = heartbeat.SettingsHash
                };

                _logger.LogWarning(
                    "Settings drift detected on node {NodeId}: " +
                    "registered={ExpectedHash}, reported={ReportedHash}",
                    nodeId, node.RegisteredSettingsHash, heartbeat.SettingsHash);
            }
        }

        if (node.IsBehindCgnat)
        {
            await SyncCgnatStateFromHeartbeatAsync(node, heartbeat.CgnatInfo, ct);
        }

        // Propagate binary versions into each obligation so reconciliation
        // can detect stale system VMs with no extra lookups.
        if (heartbeat.SystemVmBinaryVersions is { Count: > 0 })
        {
            foreach (var obligation in node.SystemVmObligations)
            {
                if (heartbeat.SystemVmBinaryVersions.TryGetValue(
                        obligation.Role.ToString(), out var hash))
                    obligation.CurrentBinaryVersion = hash;
            }
        }

        // =====================================================
        // Compute UsedResources from heartbeat-reported VMs (ground truth).
        // Includes system VMs, tenant VMs, stopped VMs — everything the
        // node reports. See docs/RESOURCE-ALLOCATION.md §8.3.
        // =====================================================
        var reportedVms = heartbeat.ActiveVms?
            .Where(v => !string.Equals(v.State, "Deleted", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<HeartbeatVmInfo>();

        // AllocatedGpuVramBytes — not GpuVramBytes — matches the field read by
        // ToAdvertisement and VmSchedulingService FILTER 5.
        node.UsedResources = new ResourceSnapshot
        {
            ComputePoints = reportedVms.Sum(v => v.ComputePointCost),
            MemoryBytes = reportedVms.Sum(v => (long)(v.MemoryBytes ?? 0)),
            StorageBytes = reportedVms.Sum(v => (long)(v.DiskBytes ?? 0)),
            GpuVramBytes = reportedVms
                .Where(v => (GpuMode)v.GpuMode == GpuMode.Proxied && v.GpuVramBytes > 0)
                .Sum(v => v.GpuVramBytes ?? 0),
        };

        // =====================================================
        // Reconcile ReservedResources from the actual Provisioning
        // VM set on every heartbeat. Recomputing from scratch prevents
        // holds from accumulating across node restarts, offline events,
        // and state machine race conditions where the VM's DB status
        // was updated to Running before the hold was explicitly released.
        //
        // VMs that appear in the heartbeat are confirmed running —
        // exclude them even if DB still shows Provisioning (timing race).
        // =====================================================
        var reportedVmIds = new HashSet<string>(
            reportedVms.Select(v => v.VmId),
            StringComparer.OrdinalIgnoreCase);

        var allNodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);

        var stillProvisioningVms = allNodeVms
            .Where(v => v.Status == VmStatus.Provisioning
                     && !reportedVmIds.Contains(v.Id)) // confirmed-running VMs excluded
            .ToList();

        var prevReservedMemory = node.ReservedResources.MemoryBytes;

        node.ReservedResources = new ResourceSnapshot
        {
            ComputePoints = stillProvisioningVms.Sum(v => v.Spec.ComputePointCost),
            MemoryBytes = stillProvisioningVms.Sum(v => v.Spec.MemoryBytes),
            StorageBytes = stillProvisioningVms.Sum(v => v.Spec.DiskBytes),
            GpuVramBytes = stillProvisioningVms
                .Where(v => v.Spec.GpuMode == GpuMode.Proxied
                         && v.Spec.GpuVramBytes is > 0)
                .Sum(v => v.Spec.GpuVramBytes!.Value),
        };

        if (prevReservedMemory != node.ReservedResources.MemoryBytes)
            _logger.LogInformation(
                "Node {NodeId}: ReservedResources reconciled — " +
                "{Count} VM(s) still in Provisioning, " +
                "memory hold {Prev} → {New} bytes",
                nodeId, stillProvisioningVms.Count,
                prevReservedMemory, node.ReservedResources.MemoryBytes);

        // If node was offline and is now back online, reset downtime tracking
        if (wasOffline)
        {
            node.LastFailedHeartbeatCheckAt = null; // Reset for next potential downtime
        }

        await _dataStore.SaveNodeAsync(node);

        // Update node reputation metrics (uptime tracking)
        // Note: We use IServiceProvider to avoid circular dependency
        var reputationService = _serviceProvider.GetService<INodeReputationService>();
        if (reputationService != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await reputationService.UpdateUptimeAsync(nodeId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update uptime for node {NodeId}", nodeId);
                }
            });
        }

        if (wasOffline)
        {
            _logger.LogInformation("Node {NodeId} came back online", nodeId);
            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.NodeOnline,
                ResourceType = "node",
                ResourceId = nodeId
            });

            // Resume billing immediately for all VMs on this node. Without this,
            // billing would remain paused until the next 5-min periodic cycle
            // detects the fresh heartbeat. Use the service provider to avoid
            // a circular dependency between NodeService and BillingService.
            var billingService = _serviceProvider.GetService<BillingService>();
            if (billingService != null)
            {
                var nodeVms = (await _dataStore.GetVmsByNodeAsync(nodeId))
                    .Where(v => v.Status == VmStatus.Running)
                    .ToList();
                foreach (var vm in nodeVms)
                {
                    await billingService.EnqueueBillingAsync(
                        vm.Id,
                        BillingTrigger.HeartbeatResumed,
                        "Node back online");
                }
                _logger.LogInformation(
                    "Enqueued HeartbeatResumed for {Count} VMs on node {NodeId}",
                    nodeVms.Count, nodeId);
            }
        }

        // Synchronize VM state from heartbeat
        await SyncVmStateFromHeartbeatAsync(nodeId, heartbeat);

        AgentSchedulingConfig? agentSchedulingConfig = null;

        if (heartbeat.SchedulingConfigVersion == 0 ||
        heartbeat.SchedulingConfigVersion != currentConfig.Version)
        {
            agentSchedulingConfig = currentConfig.MapToAgentConfig();

            _logger.LogInformation(
                "Node {NodeId} has outdated config (node: v{NodeVersion}, current: v{CurrentVersion}), " +
                "sending updated config in heartbeat response. " +
                "Baseline: {Baseline}, Overcommit: {Overcommit:F1}",
                nodeId,
                heartbeat.SchedulingConfigVersion,
                currentConfig.Version,
                currentConfig.BaselineBenchmark,
                currentConfig.Tiers[QualityTier.Burstable].CpuOvercommitRatio);
        }
        else
        {
            // Config is up-to-date, don't send it (saves bandwidth)
            _logger.LogDebug(
                "Node {NodeId} has current config v{Version}, no update needed",
                nodeId, currentConfig.Version);
        }

        // Get pending commands for this node
        var commands = _dataStore.GetAndClearPendingCommands(nodeId);

        // Compute invalid VM IDs — VMs the node reports running that the
        // orchestrator doesn't recognize as belonging to this node.
        // NodeAgent will destroy these on the next heartbeat cycle.
        List<string>? invalidVmIds = null;
        var obligationAdopted = false;
        if (heartbeat.ActiveVms != null && heartbeat.ActiveVms.Any())
        {
            var invalid = new List<string>();
            foreach (var reported in heartbeat.ActiveVms)
            {
                // System VMs (Relay, DHT, BlockStore) are autonomously managed by the
                // node's SystemVmReconciler. The orchestrator is not their control plane
                // and must never flag them as invalid — doing so creates an infinite
                // create → destroy loop. Health is reported separately via ObligationHealth.
                VmCategory? reportedcategory = Enum.TryParse<VmCategory>(reported.Category, out var c) ? c : null;
                if (reportedcategory == VmCategory.System)
                {
                    // Adopt VmId into the obligation if not yet stamped.
                    // Relay is stamped via its callback; DHT and BlockStore
                    // have no callback so the heartbeat is the adoption path.
                    // Only promote to Active when ObligationHealth confirms Healthy —
                    // avoids stamping Active on a VM that is still starting up.
                    VmRole? reportedRole = Enum.TryParse<VmRole>(reported.Role, out var r) ? r : null;
                    var role = reportedRole switch
                    {
                        VmRole.Relay => SystemVmRole.Relay,
                        VmRole.Dht => SystemVmRole.Dht,
                        VmRole.BlockStore => SystemVmRole.BlockStore,
                        _ => (SystemVmRole?)null
                    };

                    if (role is not null && !string.IsNullOrEmpty(reported.VmId))
                    {
                        var obligation = node.SystemVmObligations
                            .FirstOrDefault(o => o.Role == role && string.IsNullOrEmpty(o.VmId));

                        if (obligation is not null)
                        {
                            obligation.VmId = reported.VmId;

                            var roleName = role.ToString()!.ToLowerInvariant();
                            if (heartbeat.ObligationHealth?.TryGetValue(roleName, out var health) == true
                                && health == "Healthy")
                            {
                                obligation.Status = SystemVmStatus.Active;
                                obligation.ActiveAt ??= DateTime.UtcNow;
                            }

                            obligationAdopted = true;
                            _logger.LogInformation(
                                "Adopted {Role} VM {VmId} into obligation via heartbeat on node {NodeId}",
                                role, reported.VmId, nodeId);
                        }
                    }

                    continue;
                }

                var vm = await _dataStore.GetVmAsync(reported.VmId);

                // VM exists but belongs to a different node
                if (vm == null || vm.NodeId != nodeId)
                {
                    invalid.Add(reported.VmId);
                    _logger.LogWarning(
                        "Node {NodeId} reports running VM {VmId} that is invalid " +
                        "(exists={Exists}, assignedNode={AssignedNode}) — flagging for destruction",
                        nodeId, reported.VmId,
                        vm != null,
                        vm?.NodeId ?? "none");
                }
            }
            if (invalid.Count > 0)
                invalidVmIds = invalid;
        }

        if (obligationAdopted)
            await _dataStore.SaveNodeAsync(node);

        var obligationStatesPending = DetectStaleObligationStates(node, heartbeat.ObligationStateVersions);

        var systemTemplatesPending = await DetectStaleSystemTemplates(node, heartbeat.SystemTemplateVersions);

        var heldVmIds = (await _dataStore.GetVmsByNodeAsync(nodeId))
            .Where(v => v.ComplianceHold)
            .Select(v => v.Id)
            .ToList();

        return new NodeHeartbeatResponse(
        true,
        commands.Count > 0 ? commands : null,
        agentSchedulingConfig,
        node.CgnatInfo, invalidVmIds,
        obligationStatesPending.Count > 0 ? obligationStatesPending : null,
        systemTemplatesPending,
        settingsDrift,
        node.IsSchedulingReady,
        heldVmIds.Count > 0 ? heldVmIds : null);
    }

    /// <summary>
    /// Compare orchestrator-stored obligation state versions against what the
    /// node agent reported in the heartbeat. Returns role names where the
    /// orchestrator has a higher version — the node must pull those states.
    /// </summary>
    private static List<string> DetectStaleObligationStates(
        Node node,
        Dictionary<string, int>? nodeReportedVersions)
    {
        if (node.SystemVmObligations is not { Count: > 0 })
            return [];

        nodeReportedVersions ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();

        foreach (var obligation in node.SystemVmObligations)
        {
            if (obligation.StateVersion == 0)
                continue; // No state generated yet — registration handles this

            var roleName = SystemVmRoleMap.ToCanonicalName(obligation.Role);
            if (roleName is null) continue;

            nodeReportedVersions.TryGetValue(roleName, out var nodeVersion);
            if (obligation.StateVersion > nodeVersion)
                pending.Add(roleName);
        }

        return pending;
    }

    /// <summary>
    /// Compare orchestrator-stored template revisions against what the node
    /// reported. Returns roles where the orchestrator has a newer revision.
    /// Parallel to DetectStaleObligationStates.
    /// </summary>
    private async Task<List<string>> DetectStaleSystemTemplates(
        Node node,
        Dictionary<string, int>? nodeReportedRevisions)
    {
        if (nodeReportedRevisions is null || node.SystemVmObligations is not { Count: > 0 })
            return [];

        var stale = new List<string>();

        foreach (var obligation in node.SystemVmObligations)
        {
            var roleName = SystemVmRoleMap.ToCanonicalName(obligation.Role);
            if (roleName is null) continue;

            VmTemplate? template = null;
            if (!string.IsNullOrEmpty(obligation.TemplateId))
                template = await _dataStore.GetTemplateByIdAsync(obligation.TemplateId);

            if (template is null)
            {
                var slug = SystemVmRoleMap.ToTemplateSlug(obligation.Role);
                if (slug is null) continue;
                template = await _dataStore.GetTemplateBySlugAsync(slug);
                if (template is not null && obligation.TemplateId != template.Id)
                {
                    obligation.TemplateId = template.Id;
                    await _dataStore.SaveNodeAsync(node);
                }
            }

            if (template is null) continue;

            nodeReportedRevisions.TryGetValue(roleName, out var nodeRevision);
            if (template.Revision > nodeRevision)
                stale.Add(roleName);
        }

        return stale;
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
        // STEP 1: Complete command in registry (single atomic lookup)
        // ====================================================================
        _dataStore.TryCompleteCommand(commandId, out var registration);

        // ====================================================================
        // MULTI-STRATEGY VM LOOKUP (in order of reliability)
        // ====================================================================

        VirtualMachine? affectedVm = null;
        string lookupMethod = "none";

        // Strategy 1: Command Registry (already completed above)
        if (registration != null)
        {
            affectedVm = await _dataStore.GetVmAsync(registration!.VmId);
            if (affectedVm != null)
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
            var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
            affectedVm = nodeVms.FirstOrDefault(vm =>
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
            var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
            affectedVm = nodeVms
                .FirstOrDefault(vm =>
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
            var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
            affectedVm = nodeVms.FirstOrDefault(vm =>
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

        // Persist the cleared ActiveCommandId unconditionally. The status-transition
        // branches below may not match (e.g. VM is already Error due to a prior timeout)
        // leaving the field dirty in memory only. Saving here ensures late acks always
        // unblock the VM regardless of which branch fires.
        affectedVm.UpdatedAt = DateTime.UtcNow;
        await _dataStore.SaveVmAsync(affectedVm);

        // ====================================================================
        // HANDLE COMMAND FAILURE
        // ====================================================================

        if (!ack.Success)
        {
            _logger.LogError(
                "Command {CommandId} failed on node {NodeId}: {Error}",
                commandId, nodeId, ack.ErrorMessage ?? "Unknown error");

            // Route failure transitions through lifecycle manager
            var failLifecycleManager = _serviceProvider.GetRequiredService<IVmLifecycleManager>();

            if (affectedVm.Status == VmStatus.Deleting)
            {
                // ====================================================================
                // RECONCILIATION: If VM doesn't exist on node, treat as success
                // VM might have been manually deleted or cleaned up previously
                // ====================================================================
                var errorMessage = ack.ErrorMessage ?? "";
                bool vmNotFound = errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                                  errorMessage.Contains("NOT_FOUND", StringComparison.Ordinal);

                if (vmNotFound)
                {
                    _logger.LogInformation(
                        "VM {VmId} deletion reported as 'not found' by node {NodeId}. " +
                        "Treating as successful deletion (reconciliation).",
                        affectedVm.Id, nodeId);

                    // Clear command tracking before terminal transition
                    affectedVm.ActiveCommandId = null;
                    affectedVm.ActiveCommandType = null;
                    affectedVm.ActiveCommandIssuedAt = null;
                    affectedVm.PushMessage("Deletion confirmed by node (reconciled — VM not found)", VmMessageLevel.Warning, "command");
                    await _dataStore.SaveVmAsync(affectedVm);

                    await failLifecycleManager.TransitionAsync(
                        affectedVm.Id,
                        VmStatus.Deleted,
                        TransitionContext.CommandAck(commandId, nodeId, ack.CompletedAt));

                    return true;
                }

                // Real deletion failure
                _logger.LogWarning(
                    "VM {VmId} deletion failed - resources remain reserved. " +
                    "Manual intervention may be required.",
                    affectedVm.Id);

                await failLifecycleManager.TransitionAsync(
                    affectedVm.Id,
                    VmStatus.Error,
                    TransitionContext.CommandFailed(commandId, nodeId,
                        $"Deletion failed: {ack.ErrorMessage ?? "Unknown error"}"));
            }
            else if (affectedVm.Status == VmStatus.Provisioning)
            {
                // A terminal failure (e.g. malformed payload — see HandleCreateVmAsync)
                // cannot succeed on re-issue; a retryable failure can.
                var isTerminal = (ack.ErrorMessage ?? "")
                    .Contains("TERMINAL:", StringComparison.Ordinal);

                await failLifecycleManager.TransitionAsync(
                    affectedVm.Id,
                    VmStatus.Error,
                    TransitionContext.CommandFailed(commandId, nodeId,
                        $"Creation failed: {ack.ErrorMessage ?? "Unknown error"}"));

                // Roll back the migration authority transfer. The create never
                // succeeded, so restore NodeId to the source node and clear the
                // target. Without this the VM is orphaned off the migration scan
                // with NodeId pointing at a node that never received it.
                // Re-fetch after TransitionAsync — the lifecycle manager persists
                // internally; writing the stale object would overwrite it.
                var rolledBack = await _dataStore.GetVmAsync(affectedVm.Id);
                if (rolledBack != null &&
                    !string.IsNullOrEmpty(rolledBack.MigrationSourceNodeId))
                {
                    rolledBack.NodeId = rolledBack.MigrationSourceNodeId;
                    rolledBack.TargetNodeId = null;
                    rolledBack.MigrationSourceNodeId = null;

                    if (isTerminal)
                    {
                        // Re-issuing the identical command cannot help. Mark
                        // Unrecoverable so the scan does not loop on it.
                        rolledBack.LazysyncStatus = LazysyncStatus.Unrecoverable;
                        rolledBack.PushMessage(
                            $"Migration failed terminally: {ack.ErrorMessage}. " +
                            "Automatic retry cannot help — redeployment required.",
                            VmMessageLevel.Error, "migration");
                    }
                    else
                    {
                        rolledBack.PushMessage(
                            $"Migration to {nodeId} failed: {ack.ErrorMessage ?? "unknown"}. " +
                            "Returned to the migration queue.",
                            VmMessageLevel.Warning, "migration");
                    }

                    rolledBack.UpdatedAt = DateTime.UtcNow;
                    await _dataStore.SaveVmAsync(rolledBack);
                }
            }
            else if (affectedVm.Status == VmStatus.Stopping)
            {
                await failLifecycleManager.TransitionAsync(
                    affectedVm.Id,
                    VmStatus.Error,
                    TransitionContext.CommandFailed(commandId, nodeId,
                        $"Stop failed: {ack.ErrorMessage ?? "Unknown error"}"));
            }

            // Event is now emitted by lifecycle manager, but keep command-specific error event
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

        // ════════════════════════════════════════════════════════════════
        // Route VM lifecycle transitions through VmLifecycleManager
        // ════════════════════════════════════════════════════════════════

        var lifecycleManager = _serviceProvider.GetRequiredService<IVmLifecycleManager>();

        if (affectedVm.Status == VmStatus.Deleting)
        {
            _logger.LogInformation(
                "Deletion confirmed for VM {VmId} - transitioning to Deleted",
                affectedVm.Id);

            // Clear command tracking before terminal transition
            affectedVm.ActiveCommandId = null;
            affectedVm.ActiveCommandType = null;
            affectedVm.ActiveCommandIssuedAt = null;
            affectedVm.PushMessage("Deletion confirmed by node", VmMessageLevel.Info, "command");
            await _dataStore.SaveVmAsync(affectedVm);

            await lifecycleManager.TransitionAsync(
                affectedVm.Id,
                VmStatus.Deleted,
                TransitionContext.CommandAck(commandId, nodeId, ack.CompletedAt));
        }
        else if (affectedVm.Status == VmStatus.Provisioning)
        {
            _logger.LogInformation(
                "Creation confirmed for VM {VmId} - transitioning to Running",
                affectedVm.Id);

            // Clear compliance flag after successful migration
            if (affectedVm.NonCompliantSince != null)
            {
                affectedVm.NonCompliantSince = null;
                affectedVm.NonComplianceReason = null;
                _logger.LogInformation(
                    "VM {VmId} compliance flag cleared after successful migration",
                    affectedVm.Id);
            }

            await lifecycleManager.TransitionAsync(
                affectedVm.Id,
                VmStatus.Running,
                TransitionContext.CommandAck(commandId, nodeId, ack.CompletedAt));

            // ── Post-migration replication recovery ──────────────────────────────
            // LazysyncStatus.Migrating is only set by MigrateVmAsync — this branch
            // fires exclusively on successful migration acks, not on fresh creates.
            if (affectedVm.LazysyncStatus == LazysyncStatus.Migrating &&
                affectedVm.Spec.ReplicationFactor > 0)
            {
                var postMigration = await _dataStore.GetVmAsync(affectedVm.Id);
                if (postMigration != null)
                {
                    // Step 1: Invalidate the stale manifest confirmation.
                    //
                    // ConfirmedVersion was set pre-migration when blocks existed on the
                    // source node. The source node's blockstore was wiped on restart.
                    // Since Version == ConfirmedVersion, LazysyncManager never re-audits.
                    // Resetting to 0 re-enters the manifest into the pending audit queue
                    // (GetPendingAuditManifestsAsync filters: Version > ConfirmedVersion).
                    var manifest = await _dataStore.GetManifestAsync(postMigration.Id);
                    if (manifest != null)
                    {
                        manifest.ConfirmedVersion = 0;
                        manifest.ConfirmedRootCid = null;
                        manifest.ConfirmedChunkMap = null;
                        await _dataStore.SaveManifestAsync(manifest);
                        _logger.LogInformation(
                            "VM {VmId}: manifest ConfirmedVersion reset to 0 — re-audit queued",
                            postMigration.Id);
                    }

                    // Step 2: Trigger reseed on the target node.
                    //
                    // ReseedVm deletes lazysync.json → LazysyncDaemon re-pushes all blocks
                    // to the local blockstore → re-announces each block in DHT and via
                    // GossipSub new-blocks topic. Connected blockstores (e.g. source node
                    // after restart) receive the announcements and fetch missing blocks via
                    // bitswap, becoming remote DHT providers. LazysyncManager confirms once
                    // ≥ RF remote providers are detected across the sampled CIDs.
                    var commandSvc = _serviceProvider.GetRequiredService<INodeCommandService>();
                    var reseedId = Guid.NewGuid().ToString();
                    _dataStore.RegisterCommand(reseedId, postMigration.Id, nodeId, NodeCommandType.ReseedVm);
                    var reseedCmd = new NodeCommand(
                        reseedId,
                        NodeCommandType.ReseedVm,
                        JsonSerializer.Serialize(new { vmId = postMigration.Id }),
                        RequiresAck: false,
                        TargetResourceId: postMigration.Id);
                    await commandSvc.DeliverCommandAsync(nodeId, reseedCmd);

                    // Step 3: Reset LazysyncStatus.
                    // Replicating = blocks in blockstore, awaiting DHT confirmation.
                    // LazysyncManager will advance to Protected once RF providers confirmed.
                    postMigration.LazysyncStatus = LazysyncStatus.Replicating;
                    // Migration committed — clear the rollback source. (Only ever
                    // non-null for an in-flight migration; fresh creates never set it.)
                    postMigration.MigrationSourceNodeId = null;
                    postMigration.UpdatedAt = DateTime.UtcNow;
                    postMigration.PushMessage(
                        "Migration complete — manifest re-audit and reseed triggered. " +
                        "Waiting for external replication confirmation.",
                        VmMessageLevel.Info, "migration");
                    await _dataStore.SaveVmAsync(postMigration);

                    _logger.LogInformation(
                        "VM {VmId}: post-migration recovery initiated — " +
                        "ConfirmedVersion reset, ReseedVm sent to node {NodeId}, " +
                        "LazysyncStatus → Replicating",
                        postMigration.Id, nodeId);
                }
            }
        }
        else if (affectedVm.Status == VmStatus.Stopping)
        {
            _logger.LogInformation(
                "Stop confirmed for VM {VmId} - transitioning to Stopped",
                affectedVm.Id);

            await lifecycleManager.TransitionAsync(
                affectedVm.Id,
                VmStatus.Stopped,
                TransitionContext.CommandAck(commandId, nodeId, ack.CompletedAt));
        }
        else if (registration?.CommandType == NodeCommandType.AllocatePort)
        {
            _logger.LogInformation(
                "Port allocation confirmed for VM {VmId}",
                affectedVm.Id);

            // Parse allocated port data from acknowledgment
            if (!string.IsNullOrEmpty(ack.Data))
            {
                try
                {
                    var portData = System.Text.Json.JsonSerializer.Deserialize<AllocatePortAckData>(ack.Data);
                    if (portData != null && portData.PublicPort > 0)
                    {
                        // Update the port mapping with the actual allocated port
                        if (affectedVm.DirectAccess != null)
                        {
                            var mapping = affectedVm.DirectAccess.PortMappings
                                .FirstOrDefault(m => m.VmPort == portData.VmPort && m.Protocol == (PortProtocol)portData.Protocol);

                            if (mapping != null)
                            {
                                mapping.PublicPort = portData.PublicPort;
                                affectedVm.UpdatedAt = DateTime.UtcNow;
                                await _dataStore.SaveVmAsync(affectedVm);

                                _logger.LogInformation(
                                    "✓ Updated port mapping for VM {VmId}: {VmPort} → {PublicPort} ({Protocol})",
                                    affectedVm.Id, portData.VmPort, portData.PublicPort, portData.Protocol);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Port mapping not found for VM {VmId}: vmPort={VmPort}, protocol={Protocol}",
                                    affectedVm.Id, portData.VmPort, portData.Protocol);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to parse AllocatePort acknowledgment data for VM {VmId}",
                        affectedVm.Id);
                }
            }
        }
        else if (registration?.CommandType == NodeCommandType.RemovePort)
        {
            _logger.LogInformation(
                "Port removal confirmed for VM {VmId}",
                affectedVm.Id);

            // Port mapping should already be removed by DirectAccessService
            // This is just confirmation from the node
        }

        return true;
    }

    // CompleteVmDeletionAsync moved to VmLifecycleManager.OnVmDeletedAsync

    /// <summary>
    /// Synchronizes CGNAT node relay assignment between orchestrator state and node heartbeat.
    /// Ensures CGNAT nodes maintain valid relay assignments by:
    /// - Detecting and resolving relay assignment mismatches
    /// - Validating relay node and VM health
    /// - Triggering relay failover when assignments become invalid
    /// - Preventing assignment thrashing via per-node locking
    /// </summary>
    /// <remarks>
    /// Thread-safe: Uses per-node semaphore to prevent concurrent modifications.
    /// Idempotent: Can be called multiple times safely without side effects.
    /// </remarks>
    private async Task SyncCgnatStateFromHeartbeatAsync(
        Node node,
        CgnatNodeInfo? heartbeatCgnatInfo,
        CancellationToken ct = default)
    {
        // ========================================
        // STEP 0: Validate Node Should Be Behind CGNAT
        // ========================================
        if (node.HardwareInventory.Network.NatType == NatType.None)
        {
            // Node is NOT behind CGNAT - should not have CgnatInfo
            if (node.CgnatInfo != null)
            {
                _logger.LogWarning(
                    "Node {NodeId} is not behind CGNAT (NAT type: None) but has CgnatInfo - clearing it",
                    node.Id);
                node.CgnatInfo = null;
                await _dataStore.SaveNodeAsync(node);
            }
            return;
        }

        // ========================================
        // STEP 1: Acquire Node-Specific Lock
        // ========================================
        // Prevent concurrent heartbeats from the same node from racing
        var nodeLock = _cgnatSyncLocks.GetOrAdd(node.Id, _ => new SemaphoreSlim(1, 1));

        if (!await nodeLock.WaitAsync(0, ct))
        {
            _logger.LogDebug(
                "Skipping CGNAT sync for node {NodeId} - already in progress",
                node.Id);
            return;
        }

        try
        {
            // ========================================
            // STEP 2: Safely Get Relay Node References
            // ========================================
            Node? trackedRelayNode = null;
            Node? reportedRelayNode = null;

            // Get tracked relay (what orchestrator thinks node is assigned to)
            if (!string.IsNullOrEmpty(node.CgnatInfo?.AssignedRelayNodeId))
            {
                trackedRelayNode = await _dataStore.GetNodeAsync(node.CgnatInfo.AssignedRelayNodeId);
            }

            // Get reported relay (what node reports in heartbeat)
            if (!string.IsNullOrEmpty(heartbeatCgnatInfo?.AssignedRelayNodeId))
            {
                reportedRelayNode = await _dataStore.GetNodeAsync(heartbeatCgnatInfo.AssignedRelayNodeId);
            }

            _logger.LogDebug(
                "CGNAT sync for node {NodeId}: Tracked={TrackedId}, Reported={ReportedId}",
                node.Id,
                trackedRelayNode?.Id ?? "null",
                reportedRelayNode?.Id ?? "null");

            // ========================================
            // STEP 3: Handle Edge Cases
            // ========================================

            // Case 1: Orchestrator has no record of relay assignment (error state)
            if (node.CgnatInfo == null || trackedRelayNode == null)
            {
                await HandleMissingTrackedRelayAsync(
                    node,
                    reportedRelayNode,
                    heartbeatCgnatInfo,
                    ct);
                return;
            }

            // Case 2: Node heartbeat has no relay info (error state)
            if (heartbeatCgnatInfo == null || reportedRelayNode == null)
            {
                await HandleMissingReportedRelayAsync(
                    node,
                    trackedRelayNode,
                    ct);
                return;
            }

            // ========================================
            // STEP 4: Check for Assignment Mismatch
            // ========================================
            if (trackedRelayNode.Id != reportedRelayNode.Id)
            {
                await HandleRelayMismatchAsync(
                    node,
                    trackedRelayNode,
                    reportedRelayNode,
                    heartbeatCgnatInfo,
                    ct);
                return;
            }

            // ========================================
            // STEP 5: Verify Current Assignment is Still Valid
            // ========================================
            if (!await IsRelayValidAsync(trackedRelayNode.Id))
            {
                _logger.LogWarning(
                    "Assigned relay {RelayId} for CGNAT node {NodeId} is no longer valid - finding replacement",
                    trackedRelayNode.Id,
                    node.Id);

                await _relayNodeService.RemoveCgnatNodeFromRelayAsync(node, trackedRelayNode, ct);
                await _relayNodeService.FindAndAssignNewRelayAsync(node, ct);
                return;
            }

            // ========================================
            // STEP 6: All Good - Assignment is Valid
            // ========================================
            // Self-healing - always ensure peer is registered
            // This auto-recovers from:
            // - Relay VM restart (WireGuard config lost)
            // - Database corruption (ConnectedNodeIds empty)
            // - Manual intervention (peer manually removed)

            if (!string.IsNullOrEmpty(node.CgnatInfo?.TunnelIp))
            {
                _logger.LogDebug(
                    "CGNAT node {NodeId} relay assignment is valid: {RelayId} - ensuring peer registered",
                    node.Id, trackedRelayNode.Id);

                // ✅ SELF-HEALING: Idempotent peer registration
                // If peer exists on relay VM → no-op
                // If peer missing → auto-register
                await _relayNodeService.EnsurePeerRegisteredAsync(node, trackedRelayNode, ct);
            }
            else
            {
                _logger.LogDebug(
                    "CGNAT node {NodeId} relay assignment is valid: {RelayId}",
                    node.Id, trackedRelayNode.Id);
            }
        }
        finally
        {
            nodeLock.Release();
        }
    }

    // ============================================================================
    // HELPER METHODS
    // ============================================================================

    /// <summary>
    /// Handle case where orchestrator has no record of relay assignment
    /// but node may be reporting one
    /// </summary>
    private async Task HandleMissingTrackedRelayAsync(
        Node node,
        Node? reportedRelayNode,
        CgnatNodeInfo? heartbeatCgnatInfo,
        CancellationToken ct)
    {
        _logger.LogError(
            "Node {NodeId} is behind CGNAT but has no relay assignment in orchestrator records",
            node.Id);

        // Try to use what the node reports if valid
        if (reportedRelayNode != null &&
            heartbeatCgnatInfo != null &&
            await IsRelayValidAsync(reportedRelayNode.Id) &&
            await ValidateNodeIsKnownToRelay(node, reportedRelayNode))
        {
            _logger.LogInformation(
                "Adopting reported relay {RelayId} for CGNAT node {NodeId}",
                reportedRelayNode.Id, node.Id);

            var success = await _relayNodeService.AssignCgnatNodeToRelayAsync(node, reportedRelayNode, ct);
            if (success)
                return;
        }

        // Reported relay not valid or assignment failed - find new one
        await _relayNodeService.FindAndAssignNewRelayAsync(node, ct);
    }

    /// <summary>
    /// Handle case where node heartbeat has no relay info
    /// but orchestrator has a tracked assignment
    /// </summary>
    private async Task HandleMissingReportedRelayAsync(
        Node node,
        Node trackedRelayNode,
        CancellationToken ct)
    {
        _logger.LogError(
            "Node {NodeId} is behind CGNAT but did not report relay assignment in heartbeat",
            node.Id);

        // ========================================
        // GRACEFUL RECONCILIATION:
        // Check if orchestrator already has valid assignment
        // If yes, REUSE existing config instead of creating new one
        // This prevents duplicate peer accumulation on relay VMs
        // ========================================

        if (await IsRelayValidAsync(trackedRelayNode.Id))
        {
            // Check if node has existing tunnel IP and config
            if (!string.IsNullOrEmpty(node.CgnatInfo?.TunnelIp))
            {
                _logger.LogInformation(
                    "Node {NodeId} has existing valid relay assignment to {RelayId} (Tunnel IP: {TunnelIp}) - reusing config",
                    node.Id, trackedRelayNode.Id, node.CgnatInfo.TunnelIp);

                // Optionally: Ensure peer is registered on relay (idempotent)
                // This handles cases where relay VM was restarted and lost WireGuard config
                await _relayNodeService.EnsurePeerRegisteredAsync(node, trackedRelayNode, ct);

                // ✅ Early return - existing assignment is valid
                // The heartbeat response will include this existing config from node.CgnatInfo
                return;
            }
            else
            {
                // Node assigned to relay but has no tunnel IP - needs new config
                _logger.LogWarning(
                    "Node {NodeId} assigned to relay {RelayId} but has no tunnel IP - creating new assignment",
                    node.Id, trackedRelayNode.Id);

                var success = await _relayNodeService.AssignCgnatNodeToRelayAsync(node, trackedRelayNode, ct);
                if (success)
                    return;
            }
        }

        // ========================================
        // Only reach here if:
        // - Tracked relay is invalid, OR
        // - Node has no tunnel IP in CgnatInfo, OR
        // - Assignment failed
        // ========================================
        _logger.LogWarning(
            "No valid relay assignment exists for node {NodeId} - finding new relay",
            node.Id);

        await _relayNodeService.FindAndAssignNewRelayAsync(node, ct);
    }

    /// <summary>
    /// Handle case where node reports different relay than orchestrator has tracked
    /// </summary>
    private async Task HandleRelayMismatchAsync(
        Node node,
        Node trackedRelayNode,
        Node reportedRelayNode,
        CgnatNodeInfo heartbeatCgnatInfo,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "Relay assignment mismatch for node {NodeId}: " +
            "Orchestrator has {TrackedId}, node reports {ReportedId}",
            node.Id,
            trackedRelayNode.Id,
            reportedRelayNode.Id);

        // Remove from tracked relay (it's out of sync)
        await _relayNodeService.RemoveCgnatNodeFromRelayAsync(node, trackedRelayNode, ct);

        // Check if reported relay is valid and recognizes this node
        if (await IsRelayValidAsync(reportedRelayNode.Id) &&
            await ValidateNodeIsKnownToRelay(node, reportedRelayNode))
        {
            _logger.LogInformation(
                "Reported relay {RelayId} is valid and recognizes node {NodeId} - adopting it",
                reportedRelayNode.Id,
                node.Id);

            var success = await _relayNodeService.AssignCgnatNodeToRelayAsync(node, reportedRelayNode, ct);
            if (success)
                return; // ← CRITICAL: Early return on success
        }
        else
        {
            // Reported relay is invalid or doesn't recognize node
            _logger.LogWarning(
                "Reported relay {RelayId} is not valid or doesn't recognize node {NodeId} - finding new relay",
                reportedRelayNode.Id,
                node.Id);

            // Clean up reported relay if it exists but is invalid
            await _relayNodeService.RemoveCgnatNodeFromRelayAsync(node, reportedRelayNode, ct);
        }

        // Find a new relay as fallback
        await _relayNodeService.FindAndAssignNewRelayAsync(node, ct);
    }

    /// <summary>
    /// Validate that a relay node and its VM are in acceptable state
    /// CHANGE: Accept Degraded status - it might just be tunnel issue
    /// </summary>
    private async Task<bool> IsRelayValidAsync(string relayNodeId)
    {
        if (string.IsNullOrEmpty(relayNodeId))
            return false;

        var relayNode = await _dataStore.GetNodeAsync(relayNodeId);
        if (relayNode == null)
            return false;

        if (relayNode.Status != NodeStatus.Online)
            return false;

        if (relayNode.RelayInfo == null)
            return false;

        // Degraded means "tunnel issues" not "relay broken"
        // We'll try to heal the tunnel rather than abandon the relay
        if (relayNode.RelayInfo.Status != RelayStatus.Active &&
            relayNode.RelayInfo.Status != RelayStatus.Degraded)
        {
            return false;
        }

        // Relay VMs are created and tracked by the node agent (SQLite), not by
        // the orchestrator (MongoDB). GetVmAsync always returns null for them —
        // do not check the VM store. RelayInfo.Status (set by the relay callback
        // and maintained by RelayHealthMonitor) is the authoritative signal.
        if (string.IsNullOrEmpty(relayNode.RelayInfo.WireGuardPublicKey))
            return false; // relay VM never called back — not confirmed live

        return true;
    }

    /// <summary>
    /// Validate that the relay node actually recognizes this CGNAT node
    /// Prevents accepting fake relay assignments from malicious nodes
    /// </summary>
    private async Task<bool> ValidateNodeIsKnownToRelay(
        Node cgnatNode,
        Node relayNode)
    {
        if (relayNode.RelayInfo == null)
        {
            return false;
        }

        // Check if relay has this node in its connected list
        var isKnown = relayNode.RelayInfo.ConnectedNodeIds?.Contains(cgnatNode.Id) ?? false;

        if (!isKnown)
        {
            _logger.LogWarning(
                "CGNAT node {NodeId} claims assignment to relay {RelayId} but relay doesn't recognize it",
                cgnatNode.Id, relayNode.Id);
        }

        return isKnown;
    }

    /// <summary>
    /// Synchronize VM state reported by node agent with orchestrator's view
    /// Handles VM state updates and orphan VM recovery
    /// </summary>
    private async Task SyncVmStateFromHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        if (heartbeat.ActiveVms == null || !heartbeat.ActiveVms.Any())
            return;

        var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
        var knownVmIds = nodeVms.Select(v => v.Id).ToHashSet();

        var node = await _dataStore.GetNodeAsync(nodeId);

        foreach (var reported in heartbeat.ActiveVms)
        {
            var vmId = reported.VmId;

            var vm = await _dataStore.GetVmAsync(vmId);
            if (vm != null)
            {
                // Update existing VM state
                var newStatus = ParseVmStatus(reported.State);
                var newPowerState = ParsePowerState(reported.State);

                // ════════════════════════════════════════════════════════════
                // Compliance hold re-enforcement.
                // A held VM must not run. If the node reports it Running, ALWAYS skip
                // the Running transition (so ingress is never re-registered), and
                // re-issue a force-stop — unless one is already in flight
                // (Status==Stopping) or was issued in the last 2 minutes. The dedup
                // stops a flapping node from producing a burst of ForceStops; after the
                // window a genuinely dropped stop is retried.
                // ════════════════════════════════════════════════════════════
                if (vm.ComplianceHold && newStatus == VmStatus.Running)
                {
                    var stopInFlight = vm.Status == VmStatus.Stopping
                        || (vm.ActiveCommandType == NodeCommandType.StopVm
                            && vm.ActiveCommandIssuedAt is { } stopIssuedAt
                            && (DateTime.UtcNow - stopIssuedAt) < TimeSpan.FromMinutes(2));

                    if (!stopInFlight)
                    {
                        _logger.LogWarning(
                            "Compliance-held VM {VmId} reported Running — re-issuing force-stop", vmId);
                        var vmServiceForHold =
                            _serviceProvider.GetRequiredService<Orchestrator.Interfaces.IVmService>();
                        await vmServiceForHold.PerformVmActionAsync(vmId, VmAction.ForceStop);
                    }
                    continue;
                }

                // ====================================================================
                // CRITICAL: Don't overwrite transitional statuses from heartbeat
                // ====================================================================
                // When a VM is in a transitional state (Provisioning, Deleting, Stopping),
                // it's managed by command acknowledgments, not heartbeat.
                // Heartbeat data is stale (node reports current libvirt state, which hasn't
                // caught up to the command being executed).
                //
                // Race condition example:
                // 1. Orchestrator sends DeleteVm command → Status = Deleting
                // 2. Node receives command but VM still exists in libvirt
                // 3. Node sends heartbeat with VM=Running (stale!) → Status reset to Running
                // 4. Node deletes VM and sends acknowledgment
                // 5. Acknowledgment handler checks Status==Deleting → FAILS (now Running!)
                // 6. VM stuck in Running state forever
                // Skip heartbeat state updates for:
                // - Transitional statuses (Provisioning, Deleting, Stopping) — managed by
                //   command acknowledgment, not heartbeat.
                // - Terminal status (Deleted) — no valid transitions exist; the node may
                //   still report a VM in heartbeat after the orchestrator marked it Deleted
                //   (e.g. orphan VMs transitioned by TryDeployAsync before the node agent
                //   destroys the libvirt domain). Attempting the transition produces
                //   "Invalid VM transition: Deleted → Error" warnings every 15s.
                var isTransitionalStatus = vm.Status is VmStatus.Provisioning
                                                     or VmStatus.Stopping
                                                     or VmStatus.Deleting
                                                     or VmStatus.Deleted;

                if (isTransitionalStatus)
                {
                    _logger.LogDebug(
                        "Skipping heartbeat state update for VM {VmId} - in {Status} status " +
                        "(transitional or terminal — not managed by heartbeat)",
                        vmId, vm.Status);
                }
                else
                {
                    // Always stamp UpdatedAt for heartbeat-reported non-transitional VMs.
                    // This is the freshness signal used by VerifyActiveAsync — without it,
                    // stable Running VMs appear stale after HeartbeatSnapshotTtl (2m) and
                    // get incorrectly reset to Pending even though they're healthy.
                    vm.UpdatedAt = DateTime.UtcNow;
                }

                if (!isTransitionalStatus && (vm.Status != newStatus || vm.PowerState != newPowerState))
                {
                    // ════════════════════════════════════════════════════════════
                    // IMPORTANT: Update access info BEFORE transitioning status.
                    // The lifecycle manager's OnVmBecameRunning polls for PrivateIp,
                    // so the IP must be persisted before the transition fires.
                    // ════════════════════════════════════════════════════════════
                    if (reported.IsIpAssigned)
                    {
                        vm.NetworkConfig.PrivateIp = reported.IpAddress;
                        vm.NetworkConfig.IsIpAssigned = reported.IsIpAssigned;

                        vm.AccessInfo ??= new VmAccessInfo();
                        vm.AccessInfo.SshHost = reported.IpAddress;
                        vm.AccessInfo.SshPort = 22;

                        if (reported.VncPort != null)
                        {
                            vm.AccessInfo.VncHost = node?.PublicIp;
                            vm.AccessInfo.VncPort = reported.VncPort ?? 5900;
                        }

                        await _dataStore.SaveVmAsync(vm);
                    }

                    // Route state transition through lifecycle manager
                    var heartbeatLifecycleManager = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
                    await heartbeatLifecycleManager.TransitionAsync(
                        vmId,
                        newStatus,
                        TransitionContext.Heartbeat(nodeId));

                    _logger.LogInformation(
                        "VM {VmId} state updated from heartbeat: {Status}/{PowerState}",
                        vmId, newStatus, newPowerState);
                }

                // Re-fetch VM after potential state transition.
                // TransitionAsync saves internally — any save against the pre-transition
                // vm object overwrites the new status with the stale one.
                // All metadata (LastHeartbeatAt, IP, services) goes on the fresh object.
                vm = await _dataStore.GetVmAsync(vmId);
                if (vm == null) continue;

                // ── Base image identity round-trip ──────────────────────────────
                // The node computes the SHA256 of the cached base image bytes on first
                // download and reports it back here. The orchestrator stores it in
                // vm.Spec so future migration dispatches carry the hash. A non-empty
                // mismatch is a tamper / drift signal — surfaced as a warning, no
                // automatic remediation. See BASE_IMAGE_DESIGN.md §4.5.
                if (!string.IsNullOrEmpty(reported.BaseImageHash))
                {
                    var localHash = vm.Spec.BaseImageHash ?? string.Empty;
                    if (string.IsNullOrEmpty(localHash))
                    {
                        vm.Spec.BaseImageHash = reported.BaseImageHash;
                        if (!string.IsNullOrEmpty(reported.BaseImageUrl) &&
                            string.IsNullOrEmpty(vm.Spec.BaseImageUrl))
                        {
                            vm.Spec.BaseImageUrl = reported.BaseImageUrl;
                        }
                        _logger.LogInformation(
                            "VM {VmId}: adopted base image hash {Hash} from heartbeat (first-deploy discovery)",
                            vmId, reported.BaseImageHash[..Math.Min(16, reported.BaseImageHash.Length)]);
                    }
                    else if (!string.Equals(localHash, reported.BaseImageHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "VM {VmId}: base image hash mismatch — orchestrator has {Expected}, " +
                            "node reports {Actual}. Either the node's cache was tampered with, or the " +
                            "orchestrator's recorded hash changed mid-flight. Investigate.",
                            vmId,
                            localHash[..Math.Min(16, localHash.Length)],
                            reported.BaseImageHash[..Math.Min(16, reported.BaseImageHash.Length)]);
                    }
                }

                // Stamp alive signal and IP on the fresh object, then save.
                // LastHeartbeatAt is the sole heartbeat freshness signal for VerifyActiveAsync.
                // UpdatedAt is now owned exclusively by TransitionAsync (state machine).
                // Saved unconditionally so the alive signal persists even when no service
                // update follows. The service block (if it runs) will save again with
                // service readiness data — both saves are idempotent and correct.
                // Only stamp LastHeartbeatAt when the VM is Running. This is the
                // liveness signal for VerifyActiveAsync — stamping it for Error/Failed
                // state means unhealthyFor never accumulates past the grace period,
                // making the orchestrator permanently blind to genuinely unhealthy VMs.
                if (!isTransitionalStatus && newStatus == VmStatus.Running)
                {
                    vm.LastHeartbeatAt = DateTime.UtcNow;

                    if (reported.IsIpAssigned)
                    {
                        vm.NetworkConfig.PrivateIp = reported.IpAddress;
                        vm.NetworkConfig.IsIpAssigned = reported.IsIpAssigned;

                        vm.AccessInfo ??= new VmAccessInfo();
                        vm.AccessInfo.SshHost = reported.IpAddress;
                        vm.AccessInfo.SshPort = 22;

                        if (reported.VncPort != null)
                        {
                            vm.AccessInfo.VncHost = node?.PublicIp;
                            vm.AccessInfo.VncPort = reported.VncPort ?? 5900;
                        }
                    }

                    await _dataStore.SaveVmAsync(vm);
                }

                // Update per-service readiness from node agent
                if (reported.Services?.Count > 0 && vm.Services.Count > 0)
                {
                    UpdateServiceReadiness(vm, reported.Services);
                    await _dataStore.SaveVmAsync(vm);

                    // Extract DHT peer ID from System service StatusMessage.
                    // DHT VMs report their libp2p peer ID via the cloud-init readiness
                    // message (e.g., "peerId=12D3KooW..."). Persist it to the hosting
                    // node's DhtInfo so GetBootstrapPeersAsync() can construct multiaddrs.
                    //
                    // Self-heal: if DhtInfo was lost (crash, DB corruption) but the DHT
                    // VM is still running and healthy, reconstruct DhtInfo from the VM.
                    // The VM being present in heartbeat ActiveVms + IsFullyReady is proof
                    // of life — the node agent is actively health-checking it.
                    if (vm.Role == VmRole.Dht && node != null)
                    {
                        var dhtInfoChanged = false;

                        if (node.DhtInfo == null && vm.IsFullyReady)
                        {
                            // Prefer the advertise IP from the VM's deployment labels
                            // (which contains the WG tunnel IP), falling back to the
                            // host-level IP only if the label is missing.
                            var advertiseIp = vm.Labels?.GetValueOrDefault("dht-advertise-ip")
                                              ?? DhtNodeService.GetAdvertiseIp(node);

                            node.DhtInfo = new DhtNodeInfo
                            {
                                DhtVmId = vm.Id,
                                ListenAddress = $"{advertiseIp}:{DhtNodeService.DhtListenPort}",
                                ApiPort = DhtNodeService.DhtApiPort,
                                Status = DhtStatus.Active,
                                LastHealthCheck = DateTime.UtcNow,
                            };
                            dhtInfoChanged = true;

                            _logger.LogWarning(
                                "Reconstructed lost DhtInfo for node {NodeId} from healthy DHT VM {VmId} (advertise: {Ip})",
                                nodeId, vm.Id, advertiseIp);
                        }

                        if (node.DhtInfo != null && string.IsNullOrEmpty(node.DhtInfo.PeerId))
                        {
                            var systemService = vm.Services.FirstOrDefault(s => s.Name == "System");
                            var peerId = ExtractPeerId(systemService?.StatusMessage);
                            if (peerId != null)
                            {
                                node.DhtInfo.PeerId = peerId;
                                dhtInfoChanged = true;
                                _logger.LogInformation(
                                    "DHT peer ID captured for node {NodeId}: {PeerId}",
                                    nodeId, peerId);
                            }
                        }

                        if (node.DhtInfo != null)
                        {
                            // Keep LastHealthCheck current — the reconciliation service
                            // uses this to detect stale DHT VMs that stopped reporting.
                            node.DhtInfo.LastHealthCheck = DateTime.UtcNow;

                            // Correct stale ListenAddress if CGNAT was assigned after
                            // the DHT VM was deployed (registration ordering bug).
                            // BUT: don't overwrite WireGuard tunnel IPs (10.20.x.x) with host IPs.
                            // The WG tunnel IP is set at deployment time and is the correct
                            // advertise address for mesh connectivity.
                            var currentAddr = node.DhtInfo.ListenAddress ?? "";
                            var currentIp = currentAddr.Contains(':') ? currentAddr.Split(':')[0] : currentAddr;
                            var isWgTunnelIp = currentIp.StartsWith("10.20.");

                            if (!isWgTunnelIp)
                            {
                                var expectedIp = DhtNodeService.GetAdvertiseIp(node);
                                var expectedAddr = $"{expectedIp}:{DhtNodeService.DhtListenPort}";
                                if (node.DhtInfo.ListenAddress != expectedAddr)
                                {
                                    _logger.LogWarning(
                                        "DHT ListenAddress mismatch on node {NodeId}: " +
                                        "stored={Stored}, expected={Expected} — correcting",
                                        nodeId, node.DhtInfo.ListenAddress, expectedAddr);
                                    node.DhtInfo.ListenAddress = expectedAddr;
                                    dhtInfoChanged = true;
                                }
                            }

                            // Extract connectedPeers from StatusMessage if the node
                            // agent reports it (e.g., "peerId=... connectedPeers=3").
                            var systemSvc = vm.Services.FirstOrDefault(s => s.Name == "System");
                            var connectedPeers = ExtractConnectedPeers(systemSvc?.StatusMessage);
                            if (connectedPeers.HasValue && node.DhtInfo.ConnectedPeers != connectedPeers.Value)
                            {
                                node.DhtInfo.ConnectedPeers = connectedPeers.Value;
                                dhtInfoChanged = true;
                            }
                        }

                        if (dhtInfoChanged)
                        {
                            await _dataStore.SaveNodeAsync(node);
                        }
                    }
                }
            }
            else if (!string.IsNullOrEmpty(reported.OwnerId))
            {
                // Orphaned VM recovery
                await RecoverOrphanedVmAsync(nodeId, reported);
            }
        }

        // Reverse sync: find VMs that are Running in the orchestrator DB but not reported
        // in this heartbeat. These are ghost records — the VM was destroyed without the
        // orchestrator being notified.
        //
        // Exclude ALL transitional states (Provisioning, Deleting, Stopping, Deleted):
        // - Provisioning: node may not have started the VM yet (command just arrived).
        //   Stale Provisioning VMs are caught by ProvisioningTimeout in SystemVmReconciliationService
        //   and by a dedicated user-VM watchdog, not by heartbeat ghost detection.
        // - Deleting / Stopping / Deleted: managed by command acknowledgment.
        var reportedVmIds = heartbeat.ActiveVms
            .Select(v => v.VmId)
            .ToHashSet();

        var ghostVms = nodeVms
            .Where(v =>
                v.Status == VmStatus.Running &&
                !reportedVmIds.Contains(v.Id))
            .ToList();

        foreach (var ghost in ghostVms)
        {
            _logger.LogWarning(
                "VM {VmId} ({Name}) is {Status} in orchestrator DB but not reported " +
                "in heartbeat from node {NodeId} — marking as Error",
                ghost.Id, ghost.Name, ghost.Status, nodeId);

            var ghostLifecycleManager = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
            await ghostLifecycleManager.TransitionAsync(
                ghost.Id,
                VmStatus.Error,
                TransitionContext.Heartbeat(nodeId));
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

    /// <summary>
    /// Walk all running tenant VMs on this node and evaluate each VM's
    /// placement constraints against the node's new locality. Flag any
    /// VM whose constraints are no longer satisfied.
    ///
    /// Uses the same IConstraintEvaluator as FILTER 10 in the scheduling
    /// chain — no parallel logic, one evaluator, one answer.
    ///
    /// Returns the list of newly flagged VMs (for inclusion in the
    /// registration response so the operator sees them immediately).
    /// </summary>
    private async Task<List<NonCompliantVmInfo>> FlagNonCompliantVmsAsync(
        Node node, CancellationToken ct)
    {
        var flagged = new List<NonCompliantVmInfo>();

        var nodeVms = await _dataStore.GetVmsByNodeAsync(node.Id);

        foreach (var vm in nodeVms)
        {
            // Only check tenant VMs that are running or provisioning
            if (vm.Role != VmRole.General)
                continue;

            if (vm.Status != VmStatus.Running && vm.Status != VmStatus.Provisioning)
                continue;

            // VMs without constraints made no locality demands — always compliant
            if (vm.Spec.Constraints is not { Count: > 0 })
                continue;

            // Already flagged — don't overwrite the original timestamp
            if (vm.NonCompliantSince != null)
                continue;

            // Evaluate each constraint against the node's current (new) locality
            string? failureReason = null;
            for (var i = 0; i < vm.Spec.Constraints.Count; i++)
            {
                var result = _constraintEvaluator.Evaluate(vm.Spec.Constraints[i], node);
                if (!result.Passed)
                {
                    failureReason = $"Constraint #{i} failed: {result.RejectionReason}";
                    break;
                }
            }

            if (failureReason != null)
            {
                vm.NonCompliantSince = DateTime.UtcNow;
                vm.NonComplianceReason = failureReason;
                await _dataStore.SaveVmAsync(vm);

                flagged.Add(new NonCompliantVmInfo
                {
                    VmId = vm.Id,
                    VmName = vm.Name,
                    Reason = failureReason
                });

                _logger.LogWarning(
                    "VM {VmId} ({VmName}) flagged non-compliant on node {NodeId}: {Reason}",
                    vm.Id, vm.Name, node.Id, failureReason);
            }
        }

        return flagged;
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
            var node = await _dataStore.GetNodeAsync(nodeId);
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
            var node = await _dataStore.GetNodeAsync(nodeId);
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


    public async Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return false;

        node.Status = status;
        await _dataStore.SaveNodeAsync(node);
        return true;
    }

    // ============================================================================
    // Health Monitoring
    // ============================================================================

    public async Task CheckNodeHealthAsync()
    {
        var heartbeatTimeout = TimeSpan.FromMinutes(2);
        var now = DateTime.UtcNow;

        var onlineNodes = _dataStore.GetActiveNodes()
            .Where(n => n.Status == NodeStatus.Online)
            .ToList();

        foreach (var node in onlineNodes)
        {
            var timeSinceLastHeartbeat = now - node.LastSeenAt;

            if (timeSinceLastHeartbeat > heartbeatTimeout)
            {
                _logger.LogWarning(
                    "Node {NodeId} ({Name}) marked as offline - no heartbeat for {Minutes:F1} minutes",
                    node.Id, node.Name, timeSinceLastHeartbeat?.TotalMinutes ?? 0);

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
                        ["timeoutMinutes"] = timeSinceLastHeartbeat?.TotalMinutes ?? 0
                    }
                });

                await MarkNodeVmsAsErrorAsync(node.Id);

                // Start tracking downtime for reputation
                // Record the moment node went offline
                var reputationService = _serviceProvider.GetService<INodeReputationService>();
                if (reputationService != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Mark when downtime started (for tracking purposes)
                            node.LastFailedHeartbeatCheckAt = node.LastSeenAt;
                            await _dataStore.SaveNodeAsync(node);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to initialize downtime tracking for node {NodeId}", node.Id);
                        }
                    });
                }
            }
        }

        // For all offline nodes, record ongoing failures
        var offlineNodes = _dataStore.GetActiveNodes()
            .Where(n => n.Status == NodeStatus.Offline)
            .ToList();

        foreach (var node in offlineNodes)
        {
            var reputationService = _serviceProvider.GetService<INodeReputationService>();
            if (reputationService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await reputationService.RecordFailedHeartbeatsSinceLastCheckAsync(node.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to record failed heartbeats for node {NodeId}", node.Id);
                    }
                });
            }
        }
    }

    // ============================================================================
    // Source-offline alerting
    // ============================================================================

    /// <summary>
    /// Called when a node's heartbeat times out. Transitions every running tenant
    /// VM on that node to VmStatus.Error and classifies its LazysyncStatus based
    /// on manifest state so the dashboard and the migration trigger can act on it.
    ///
    /// Classification rules (in priority order):
    ///   replicationFactor == 0           → Lost          (ephemeral — no alert)
    ///   manifest missing / confirmedV==0 → Unrecoverable (seeding never finished)
    ///   confirmedVersion < version       → Recovering    (stale confirmed copy)
    ///   confirmedVersion == version      → Migrating     (fully caught up, no data loss)
    /// </summary>
    private async Task MarkNodeVmsAsErrorAsync(string nodeId)
    {
        var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);

        nodeVms = nodeVms
            .Where(v => v.NodeId == nodeId && v.Status == VmStatus.Running)
            .ToList();

        if (nodeVms.Count == 0) return;

        _logger.LogWarning(
            "Node {NodeId} offline — classifying {Count} running VM(s) for source-offline alerting",
            nodeId, nodeVms.Count);

        var lifecycleManager = _serviceProvider.GetRequiredService<IVmLifecycleManager>();

        foreach (var vm in nodeVms)
        {
            // Fetch manifest once — only for replicated VMs (saves DB round-trips for ephemeral).
            var manifest = vm.Spec.ReplicationFactor > 0
                ? await _dataStore.GetManifestAsync(vm.Id)
                : null;

            var lazysyncStatus = ClassifyOfflineVm(vm, manifest);
            var statusMessage = BuildOfflineStatusMessage(lazysyncStatus, manifest);

            // Transition VmStatus → Error (fires lifecycle side-effects: ingress, billing, events).
            await lifecycleManager.TransitionAsync(
                vm.Id,
                VmStatus.Error,
                TransitionContext.NodeOffline(nodeId));

            // Re-fetch after TransitionAsync — lifecycle manager persists the VM internally,
            // so writing against the original vm object would produce a stale overwrite.
            var updated = await _dataStore.GetVmAsync(vm.Id);
            if (updated == null) continue;

            updated.LazysyncStatus = lazysyncStatus;
            var msgLevel = lazysyncStatus == LazysyncStatus.Unrecoverable
                ? VmMessageLevel.Error
                : lazysyncStatus == LazysyncStatus.Recovering
                    ? VmMessageLevel.Warning
                    : VmMessageLevel.Info;
            updated.PushMessage(statusMessage, msgLevel, "healthmonitor");
            await _dataStore.SaveVmAsync(updated);

            _logger.Log(
                lazysyncStatus == LazysyncStatus.Unrecoverable ? LogLevel.Error :
                lazysyncStatus == LazysyncStatus.Recovering ? LogLevel.Warning :
                lazysyncStatus == LazysyncStatus.Migrating ? LogLevel.Information :
                                                                 LogLevel.Debug,    // Lost
                "VM {VmId} ({Name}) on offline node {NodeId}: {LazysyncStatus}",
                vm.Id, vm.Name, nodeId, lazysyncStatus);
        }
    }

    /// <summary>
    /// Pure classification — no I/O. Determines the correct LazysyncStatus
    /// for a VM whose host node has just gone offline.
    /// </summary>
    private static LazysyncStatus ClassifyOfflineVm(VirtualMachine vm, ManifestRecord? manifest)
    {
        // Ephemeral: user explicitly opted out of replication — data loss is expected.
        if (vm.Spec.ReplicationFactor == 0)
            return LazysyncStatus.Lost;

        // Replicated, but no confirmed blocks exist yet (seeding never completed).
        if (manifest == null || manifest.ConfirmedVersion == 0)
            return LazysyncStatus.Unrecoverable;

        // Confirmed copy exists but lags behind the latest lazysync cycle.
        // Recovery is possible from confirmedVersion with minor data loss.
        if (manifest.ConfirmedVersion < manifest.Version)
            return LazysyncStatus.Recovering;

        // Fully caught up — confirmed version matches current version.
        // Migration can proceed with zero data loss.
        return LazysyncStatus.Migrating;
    }

    /// <summary>
    /// Builds a human-readable StatusMessage for a VM classified by ClassifyOfflineVm.
    /// manifest is non-null whenever status is Recovering or Migrating.
    /// </summary>
    private static string BuildOfflineStatusMessage(LazysyncStatus status, ManifestRecord? manifest) =>
        status switch
        {
            LazysyncStatus.Lost =>
                "Node offline — ephemeral VM lost (replicationFactor=0, data loss expected)",

            LazysyncStatus.Unrecoverable =>
                "Node offline — no confirmed replica exists, redeployment from scratch required",

            LazysyncStatus.Recovering =>
                $"Node offline — recovering from confirmed v{manifest!.ConfirmedVersion} " +
                $"(current v{manifest.Version}; changes since last confirmed version may be lost)",

            LazysyncStatus.Migrating =>
                $"Node offline — migrating from confirmed v{manifest!.ConfirmedVersion} " +
                "(fully replicated, no data loss expected)",

            _ => "Node offline"
        };

    private string GenerateNodeJwtToken(string nodeId, string walletAddress, string machineId, string jti)
    {
        // Get JWT configuration (same as user JWT)
        var jwtKey = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT key not configured. Set Jwt:Key in appsettings or via environment variable Jwt__Key.");
        var jwtIssuer = _configuration["Jwt:Issuer"] ?? "orchestrator";
        var jwtAudience = _configuration["Jwt:Audience"] ?? "orchestrator-client";

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        // Create claims for the node
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, nodeId),
            new Claim("node_id", nodeId),
            new Claim("wallet", walletAddress),
            new Claim("machine_id", machineId),
            new Claim(ClaimTypes.Role, "node"),
            new Claim(JwtRegisteredClaimNames.Jti, jti),
            new Claim(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        // Create long-lived token for nodes (1 year expiration)
        // Nodes don't need frequent token refresh like users
        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddYears(1), // Long-lived for nodes
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ============================================================================
    // Node Search & Marketplace (moved from NodeMarketplaceService)
    // ============================================================================

    public async Task<List<NodeAdvertisement>> SearchNodesAsync(NodeSearchCriteria criteria)
    {
        var nodes = (await _dataStore.GetAllNodesAsync()).AsEnumerable();

        // Only show nodes that have fulfilled all platform obligations.
        // A node still deploying its DHT, Relay, or BlockStore VM is not
        // ready to host user workloads and must not appear in the marketplace.
        // Nodes with no obligations (hardware below eligibility thresholds)
        // are not blocked — they have nothing to fulfill.
        nodes = nodes.Where(n =>
            n.SystemVmObligations.Count == 0 ||
            n.SystemVmObligations.All(o => o.Status == SystemVmStatus.Active));

        // Filter by online status
        if (criteria.OnlineOnly)
        {
            nodes = nodes.Where(n => n.Status == NodeStatus.Online);
        }

        // Filter by tags (node must have ALL specified tags)
        if (criteria.Tags?.Any() == true)
        {
            nodes = nodes.Where(n =>
                criteria.Tags.All(tag =>
                    n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }

        // Filter by region
        if (!string.IsNullOrEmpty(criteria.Region))
        {
            nodes = nodes.Where(n =>
                n.Locality.Region.Equals(criteria.Region, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by GPU requirement
        if (criteria.RequiresGpu == true)
        {
            nodes = nodes.Where(n => n.HardwareInventory.SupportsGpu);
        }

        // Filter by uptime
        if (criteria.MinUptimePercent.HasValue)
        {
            nodes = nodes.Where(n => n.UptimePercentage >= criteria.MinUptimePercent.Value);
        }

        // Filter by price
        if (criteria.MaxPricePerPoint.HasValue)
        {
            nodes = nodes.Where(n => n.Pricing.CpuPerHour <= criteria.MaxPricePerPoint.Value);
        }

        /// Filter by available capacity
        if (criteria.MinAvailableComputePoints.HasValue)
        {
            nodes = nodes.Where(n =>
                (n.TotalResources.ComputePoints - n.ReservedResources.ComputePoints - n.UsedResources.ComputePoints) >=
                criteria.MinAvailableComputePoints.Value);
        }

        // Filter by available GPU VRAM (Proxied workloads)
        if (criteria.MinAvailableGpuVramBytes.HasValue)
        {
            nodes = nodes.Where(n =>
            {
                var totalProxiedVram = n.HardwareInventory.Gpus
                    .Where(g => g.IsAvailableForProxiedSharing)
                    .Sum(g => g.MemoryBytes);
                var availableVram = totalProxiedVram
                    - n.UsedResources.GpuVramBytes
                    - n.ReservedResources.GpuVramBytes;
                return availableVram >= criteria.MinAvailableGpuVramBytes.Value;
            });
        }

        // Convert to advertisements
        var advertisements = nodes.Select(n => ToAdvertisement(n)).ToList();

        // Sort
        advertisements = criteria.SortBy?.ToLower() switch
        {
            "price" => criteria.SortDescending
                ? advertisements.OrderByDescending(a => a.Pricing.CpuPerHour).ToList()
                : advertisements.OrderBy(a => a.Pricing.CpuPerHour).ToList(),
            "uptime" => criteria.SortDescending
                ? advertisements.OrderByDescending(a => a.UptimePercentage).ToList()
                : advertisements.OrderBy(a => a.UptimePercentage).ToList(),
            "capacity" => criteria.SortDescending
                ? advertisements.OrderByDescending(a => a.AvailableComputePoints).ToList()
                : advertisements.OrderBy(a => a.AvailableComputePoints).ToList(),
            _ => advertisements.OrderByDescending(a => a.UptimePercentage).ToList() // Default sort by uptime
        };

        return await Task.FromResult(advertisements);
    }

    public async Task<List<NodeAdvertisement>> GetFeaturedNodesAsync()
    {
        // Featured nodes criteria:
        // 1. High uptime (>95%)
        // 2. Good capacity
        // 3. Online
        // 4. Has description (curated)

        var featuredNodes = _dataStore.ActiveNodes.Values
            .Where(n =>
                n.Status == NodeStatus.Online &&
                n.UptimePercentage >= 95.0 &&
                !string.IsNullOrEmpty(n.Description) &&
                (n.AllocatedResources.ComputePoints - n.UsedResources.ComputePoints - n.ReservedResources.ComputePoints) > 10 &&
                (n.SystemVmObligations.Count == 0 ||
                 n.SystemVmObligations.All(o => o.Status == SystemVmStatus.Active)))
            .OrderByDescending(n => n.UptimePercentage)
            .ThenByDescending(n => n.TotalVmsHosted)
            .Take(10)
            .Select(n => ToAdvertisement(n))
            .ToList();

        return await Task.FromResult(featuredNodes);
    }

    public async Task<NodeAdvertisement?> GetNodeAdvertisementAsync(string nodeId)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return null;

        return ToAdvertisement(node);
    }

    /// <summary>
    /// Convert Node to NodeAdvertisement (DTO for marketplace/public browsing)
    /// </summary>
    private NodeAdvertisement ToAdvertisement(Node node)
    {
        return new NodeAdvertisement
        {
            NodeId = node.Id,
            OperatorName = node.Name,
            Description = node.Description,
            Region = node.Locality.Region,
            Zone = node.Locality.Zone ?? "default",
            Country = node.Locality.Country,
            JurisdictionTags = node.Locality.JurisdictionTags,
            LocationMismatch = node.Locality.LocationMismatch,
            Tags = node.Tags,
            Capabilities = new NodeCapabilities
            {
                HasGpu = node.HardwareInventory.SupportsGpu,
                GpuModel = node.HardwareInventory.Gpus.FirstOrDefault()?.Model,
                GpuCount = node.HardwareInventory.Gpus.Count > 0 ? node.HardwareInventory.Gpus.Count : null,
                GpuMemoryBytes = node.HardwareInventory.Gpus.FirstOrDefault()?.MemoryBytes,
                SupportsProxiedGpu = node.HardwareInventory.HasProxiedCapableGpu,
                TotalGpuVramBytes = node.HardwareInventory.Gpus.Sum(g => g.MemoryBytes),
                // Operator ceiling: use AllocatedResources.GpuVramBytes when set;
                // fall back to physical proxy-eligible sum for nodes that have not
                // run decloud allocate since GpuVramPercent support was deployed.
                AllocatedGpuVramBytes = node.AllocatedResources.GpuVramBytes > 0
                    ? node.AllocatedResources.GpuVramBytes
                    : node.HardwareInventory.Gpus
                        .Where(g => g.IsAvailableForProxiedSharing)
                        .Sum(g => g.MemoryBytes),
                AvailableGpuVramBytes = Math.Max(0,
                    (node.AllocatedResources.GpuVramBytes > 0
                        ? node.AllocatedResources.GpuVramBytes
                        : node.HardwareInventory.Gpus
                            .Where(g => g.IsAvailableForProxiedSharing)
                            .Sum(g => g.MemoryBytes))
                    - node.UsedResources.GpuVramBytes
                    - node.ReservedResources.GpuVramBytes),
                GpuVramPerGbPerHour = node.Pricing.GpuVramPerGbPerHour > 0
                    ? Math.Max(node.Pricing.GpuVramPerGbPerHour, _pricingConfig.FloorGpuVramPerGbPerHour)
                    : _pricingConfig.DefaultGpuVramPerGbPerHour,
                HasNvmeStorage = node.HardwareInventory.Storage.Any(s => s.Type == StorageType.NVMe),
                HighBandwidth = (node.HardwareInventory.Network.BandwidthBitsPerSecond ?? 0) > 1_000_000_000,
                CpuModel = node.HardwareInventory.Cpu.Model,
                CpuCores = node.HardwareInventory.Cpu.PhysicalCores,
            },
            UptimePercentage = node.UptimePercentage,
            TotalVmsHosted = node.TotalVmsHosted,
            SuccessfulVmCompletions = node.SuccessfulVmCompletions,
            RegisteredAt = node.RegisteredAt,
            Pricing = node.Pricing,

            IsOnline = node.Status == NodeStatus.Online,
            SchedulingReady = node.IsSchedulingReady,
            AllocatedComputePoints = node.AllocatedResources.ComputePoints,
            AvailableComputePoints = Math.Max(0, node.AllocatedResources.ComputePoints - node.UsedResources.ComputePoints - node.ReservedResources.ComputePoints),
            AllocatedMemoryBytes = node.AllocatedResources.MemoryBytes,
            AvailableMemoryBytes = Math.Max(0, node.AllocatedResources.MemoryBytes - node.UsedResources.MemoryBytes - node.ReservedResources.MemoryBytes),
            AllocatedStorageBytes = node.AllocatedResources.StorageBytes,
            AvailableStorageBytes = Math.Max(0, node.AllocatedResources.StorageBytes - node.UsedResources.StorageBytes - node.ReservedResources.StorageBytes)
        };
    }

    // =========================================================================
    // Service Readiness Processing
    // =========================================================================

    /// <summary>
    /// Update VM's service readiness from heartbeat data reported by node agent.
    /// Matches reported services by name and updates status/timestamps.
    /// </summary>
    private void UpdateServiceReadiness(VirtualMachine vm, List<HeartbeatServiceInfo> reportedServices)
    {
        var changed = false;

        foreach (var reported in reportedServices)
        {
            var service = vm.Services.FirstOrDefault(s => s.Name == reported.Name);
            if (service == null)
                continue;

            var newStatus = Enum.TryParse<ServiceStatus>(reported.Status, true, out var parsed)
                ? parsed
                : ServiceStatus.Pending;

            // Always update StatusMessage (may change even if status stays the same)
            if (service.StatusMessage != reported.StatusMessage)
            {
                service.StatusMessage = reported.StatusMessage;
                changed = true;
            }

            if (service.Status != newStatus)
            {
                // Guard: Ready is a terminal success state — don't regress to TimedOut.
                // This prevents a race where the node agent's timeout fires after the
                // service already reached Ready (e.g., cloud-init done at 298s, but the
                // node's 300s timer expired before the next heartbeat reported Ready).
                if (service.Status == ServiceStatus.Ready &&
                    newStatus == ServiceStatus.TimedOut)
                {
                    _logger.LogDebug(
                        "VM {VmId} service '{ServiceName}' ignoring TimedOut — already Ready (ReadyAt: {ReadyAt})",
                        vm.Id, service.Name, service.ReadyAt);
                    continue;
                }

                var oldStatus = service.Status;
                service.Status = newStatus;
                service.LastCheckAt = DateTime.UtcNow;

                if (newStatus == ServiceStatus.Ready && service.ReadyAt == null)
                {
                    service.ReadyAt = reported.ReadyAt ?? DateTime.UtcNow;
                }

                if (newStatus is ServiceStatus.Failed or ServiceStatus.TimedOut && reported.StatusMessage != null)
                {
                    _logger.LogWarning(
                        "VM {VmId} service '{ServiceName}' readiness: {OldStatus} → {NewStatus} — {Message}",
                        vm.Id, service.Name, oldStatus, newStatus, reported.StatusMessage);
                }
                else
                {
                    _logger.LogInformation(
                        "VM {VmId} service '{ServiceName}' readiness: {OldStatus} → {NewStatus}",
                        vm.Id, service.Name, oldStatus, newStatus);
                }

                changed = true;
            }
        }

        if (changed && vm.IsFullyReady)
        {
            _logger.LogInformation(
                "VM {VmId} all services ready ({Count} services)",
                vm.Id, vm.Services.Count);
        }
    }

    /// <summary>
    /// Extract a libp2p peer ID from a StatusMessage string.
    /// Expected format: "peerId=12D3KooW..." (anywhere in the message).
    /// Returns null if no peer ID is found.
    /// </summary>
    private static string? ExtractPeerId(string? statusMessage)
    {
        if (string.IsNullOrEmpty(statusMessage))
            return null;

        // Match "peerId=" followed by a libp2p peer ID (base58btc multihash, typically 12D3KooW... or Qm...)
        var match = System.Text.RegularExpressions.Regex.Match(
            statusMessage, @"peerId=([A-Za-z0-9]{20,})");

        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Extract connected peers count from a StatusMessage string.
    /// Expected format: "connectedPeers=3" (anywhere in the message).
    /// Returns null if not found (node agent may not report this yet).
    /// </summary>
    private static int? ExtractConnectedPeers(string? statusMessage)
    {
        if (string.IsNullOrEmpty(statusMessage))
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            statusMessage, @"connectedPeers=(\d+)");

        return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : null;
    }
}