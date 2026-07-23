using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Enums;
using DeCloud.Shared.Json;
using DeCloud.Shared.Models;
using Microsoft.Extensions.Options;
using Orchestrator.Interfaces;
using Orchestrator.Interfaces.VmScheduling;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;
using Orchestrator.Services.Payment;
using Orchestrator.Services.SystemVm;
using Orchestrator.Services.VmScheduling;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orchestrator.Services;

public class VmService : IVmService
{
    private readonly DataStore _dataStore;
    private readonly INodeCommandService _commandService;
    private readonly IVmSchedulingService _schedulingService;
    private readonly ISchedulingConfigService _configService;
    private readonly IConstraintEvaluator _constraintEvaluator;
    private readonly IEventService _eventService;
    private readonly IVmNotificationService _notifications;
    private readonly ICentralIngressService _ingressService;
    private readonly ITemplateService _templateService;
    private readonly IVmNameService _nameService;
    private readonly PricingConfig _pricingConfig;
    private readonly IConfiguration _configuration;
    private readonly ICloudInitRenderer _cloudInitRenderer;
    private readonly ITosService _tosService;
    private readonly IWalletBlocklistService _blocklistService;
    private readonly ILogger<VmService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public VmService(
    DataStore dataStore,
    INodeCommandService commandService,
    IVmSchedulingService schedulingService,
    ISchedulingConfigService configService,
    IConstraintEvaluator constraintEvaluator,
    IEventService eventService,
    IVmNotificationService notificationService,
    ICentralIngressService ingressService,
    ITemplateService templateService,
    IVmNameService nameService,
    IOptions<PricingConfig> pricingConfig,
    IConfiguration configuration,
    ICloudInitRenderer cloudInitRenderer,
    ITosService tosService,
    IWalletBlocklistService blocklistService,
    ILogger<VmService> logger,
    IServiceProvider serviceProvider)
    {
        _dataStore = dataStore;
        _commandService = commandService;
        _schedulingService = schedulingService;
        _configService = configService;
        _constraintEvaluator = constraintEvaluator;
        _eventService = eventService;
        _notifications = notificationService;
        _ingressService = ingressService;
        _templateService = templateService;
        _nameService = nameService;
        _pricingConfig = pricingConfig.Value;
        _configuration = configuration;
        _cloudInitRenderer = cloudInitRenderer;
        _tosService = tosService;
        _blocklistService = blocklistService;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task<CreateVmResponse> CreateVmAsync(string userId, CreateVmRequest request, string? targetNodeId = null)
    {
        var isSystemVm = request.Role is VmRole.Relay or VmRole.Dht or VmRole.BlockStore;

        // ════════════════════════════════════════════════════════════════════════
        // Validate scheduling constraints (Phase B). Reject malformed constraints
        // before any resource is allocated. ValidateSet walks the list and
        // returns the first error prefixed with the constraint's index, e.g.:
        //   "Constraint #2: Unknown target 'node.foobar'"
        // See docs/SCHEDULING.md §7 for the constraint vocabulary.
        // ════════════════════════════════════════════════════════════════════════
        if (request.Spec.Constraints is { Count: > 0 })
        {
            var constraintError = _constraintEvaluator.ValidateSet(request.Spec.Constraints);
            if (constraintError is not null)
            {
                _logger.LogWarning(
                    "VM creation rejected for user {UserId}: {Error}",
                    userId, constraintError);
                return new CreateVmResponse(string.Empty, VmStatus.Pending,
                    $"Invalid constraint: {constraintError}", "INVALID_CONSTRAINT");
            }
            // Unwrap JsonElement values (from JSON deserialization) into native
            // C# types. MongoDB's ObjectSerializer cannot serialize JsonElement
            // directly. Must run after ValidateSet (which reads Value) and before
            // any SaveVmAsync call.
            foreach (var c in request.Spec.Constraints) c.NormalizeValue();
        }

        // ════════════════════════════════════════════════════════════════════════
        // VM Name Pipeline: sanitize → validate → unique suffix → uniqueness check
        // System VMs (relay, DHT) pass through as-is (names are code-generated)
        // ════════════════════════════════════════════════════════════════════════
        var (canonicalName, nameError) = await _nameService.GenerateCanonicalNameAsync(request.Name, userId);
        if (canonicalName == null)
        {
            return new CreateVmResponse(string.Empty, VmStatus.Pending,
                nameError ?? "Invalid VM name", "INVALID_NAME");
        }

        // Validate user exists
        User? user = null;
        if (_dataStore.Users.TryGetValue(userId, out var existingUser))
        {
            user = existingUser;
        }

        // ── Compliance: enforcement gate (tenant VMs only) ───────────────────────────
        // One predicate over two boundaries: an internal suspension (User.Status) or a
        // provenance-bearing denylist entry. Checked server-side at action time, never
        // via a JWT claim, so a block takes effect immediately. Withhold-of-service
        // only — funds are untouched. The reason is deliberately not surfaced.
        if (!isSystemVm && await _blocklistService.IsWalletBlockedAsync(userId))
        {
            return new CreateVmResponse(string.Empty, VmStatus.Pending,
                "Your account is not permitted to deploy.", "WALLET_BLOCKED");
        }

        // ── Compliance: Terms of Service gate (tenant VMs only) ──────────────────────
        // System VMs deploy under userId "system" and are exempt. Checked server-side
        // here — never via the JWT claim — so a freshly required (re-)acceptance takes
        // effect immediately rather than after the ~60-min access-token lifetime.
        if (!isSystemVm && !await _tosService.HasAcceptedCurrentAsync(userId))
        {
            return new CreateVmResponse(string.Empty, VmStatus.Pending,
                "You must accept the current Terms of Service before deploying.",
                "TOS_NOT_ACCEPTED");
        }

        // Check quotas
        if (user != null && !isSystemVm)
        {
            if (user.Quotas.CurrentVms >= user.Quotas.MaxVms)
            {
                return new CreateVmResponse(string.Empty, VmStatus.Pending,
                    "VM quota exceeded", "QUOTA_EXCEEDED");
            }

            if (user.Quotas.CurrentVirtualCpuCores + request.Spec.VirtualCpuCores > user.Quotas.MaxVirtualCpuCores)
            {
                return new CreateVmResponse(string.Empty, VmStatus.Pending,
                    "CPU quota exceeded", "QUOTA_EXCEEDED");
            }
        }

        // Generate memorable password
        var password = isSystemVm ? null : GenerateMemorablePassword();

        // Validate and apply replication factor.
        // System VMs are always ephemeral (factor=0): they redeploy from cloud-init,
        // not from disk state. Tenant VMs default to 3, validated to allowed tiers.
        var replicationFactor = isSystemVm
            ? 0
            : request.Spec.ReplicationFactor switch
            {
                0 or 1 or 3 or 5 => request.Spec.ReplicationFactor,
                _ => 3 // Clamp unsupported values to default
            };
        request.Spec.ReplicationFactor = replicationFactor;

        // ════════════════════════════════════════════════════════════════════════
        // Default base image for tenant VMs
        // ════════════════════════════════════════════════════════════════════════
        // The OS is a user CHOICE WITH A DEFAULT. OS-agnostic templates leave
        // RecommendedSpec.ImageId empty on purpose; templates whose cloud-init is
        // OS-specific pin it themselves. Until now that default lived only inside
        // the legacy create-VM form (and template-detail.js hardcoded
        // "ubuntu-22.04"), so a deploy from any other client — API, CLI, the new
        // app — sent an empty ImageId. BaseImageUrlResolver then had nothing to
        // resolve, and the node correctly rejected the payload as
        // "missing BaseImageUrl on a fresh deploy".
        //
        // ubuntu-22.04 is the default because it is the only registry image with a
        // PINNED SHA256 — so the path most VMs take is the content-verified one.
        // Before moving the default to ubuntu-24.04 (longer support runway), pin
        // its hash in the image registry first, or the default silently drops to
        // permissive download-and-record mode.
        //
        // Scoped to tenant VMs deliberately: system VMs (Relay/DHT/BlockStore) are
        // debian-12 based and set their own image. Silently handing one an Ubuntu
        // base would be worse than the loud empty-value failure it replaces — the
        // node's guard stays the backstop there.
        if (!isSystemVm && string.IsNullOrWhiteSpace(request.Spec.ImageId))
        {
            request.Spec.ImageId =
                _configuration["PlatformDefaults:DefaultImageId"] ?? "ubuntu-22.04";

            _logger.LogDebug(
                "VM {Name}: no ImageId specified — applying platform default {ImageId}",
                canonicalName, request.Spec.ImageId);
        }

        // ════════════════════════════════════════════════════════════════════════
        // P2.2 — Default TemplateId to platform-general for tenant General VMs
        // ════════════════════════════════════════════════════════════════════════
        // Per UNIFIED_CLOUDINIT_PIPELINE.md §5 Phase 2.2: every General tenant VM
        // goes through the platform-general template (seeded by P2.1). The cloud-
        // init processing block below detects request.TemplateId and routes to the
        // "Phase 1: Template-based" branch — this default fills in TemplateId for
        // requests that arrived without one.
        //
        // Custom cloud-init still takes priority — users explicitly bringing their
        // own cloud-init bypass the template. System VMs are unaffected (handled
        // by the obligation pipeline, not by VmService.CreateVmAsync).
        //
        // On seeder failure (platform-general not in MongoDB), log a warning and
        // fall through to the legacy "no template" path on the node-agent. This is
        // deliberate conservatism for Phase 2 — a transient seed failure shouldn't
        // break tenant deploys. Phase 4 cleanup removes the legacy path entirely.
        if (!isSystemVm
            && request.Role == VmRole.General
            && string.IsNullOrEmpty(request.TemplateId)
            && string.IsNullOrEmpty(request.CustomCloudInit))
        {
            var general = await _templateService.GetTemplateBySlugAsync("platform-general");
            if (general is not null)
            {
                request = request with { TemplateId = general.Id };
                _logger.LogDebug(
                    "VM {Name}: defaulted to platform-general template (id={TemplateId})",
                    canonicalName, general.Id);
            }
            else
            {
                _logger.LogWarning(
                    "VM {Name}: no TemplateId or CustomCloudInit provided and platform-general " +
                    "not found in MongoDB (seeder may have failed or not run yet) — falling " +
                    "through to legacy node-side cloud-init path.",
                    canonicalName);
            }
        }

        var vm = new VirtualMachine
        {
            Id = Guid.NewGuid().ToString(),
            Name = canonicalName,
            Role = request.Role,
            SubdomainTier = SubdomainTier.Free,
            OwnerId = isSystemVm ? null : userId,
            OwnerWallet = isSystemVm ? null : user?.WalletAddress,
            Spec = request.Spec,
            Status = VmStatus.Pending,
            LazysyncStatus = replicationFactor == 0
                ? LazysyncStatus.Pending   // ephemeral — stays Pending forever
                : LazysyncStatus.Pending,  // will advance to Seeding on first cycle
            Labels = request.Labels ?? [],
            BillingInfo = new VmBillingInfo
            {
                HourlyRateCrypto = 0,   // Set in ScheduleVmAsync once a node is selected
                CryptoSymbol = "USDC"
            },
            NetworkConfig = new VmNetworkConfig
            {
                Hostname = canonicalName
            },
        };

        // ════════════════════════════════════════════════════════════════════════
        // Cloud-Init Processing (Priority: Custom > Template > None)
        // ════════════════════════════════════════════════════════════════════════

        // Phase 2: Custom cloud-init (overrides template)
        if (!string.IsNullOrEmpty(request.CustomCloudInit))
        {
            vm.Spec.CloudInitUserData = request.CustomCloudInit;

            // Store environment variables for substitution
            if (request.EnvironmentVariables != null && request.EnvironmentVariables.Any())
            {
                vm.Labels["custom:cloud-init-vars"] = JsonSerializer.Serialize(request.EnvironmentVariables);
            }

            _logger.LogInformation(
                "VM {VmId} created with custom cloud-init ({Length} bytes)",
                vm.Id, request.CustomCloudInit.Length);
        }
        // Phase 1: Template-based cloud-init (current)
        else if (!string.IsNullOrEmpty(request.TemplateId))
        {
            var template = await _templateService.GetTemplateByIdAsync(request.TemplateId);
            if (template != null)
            {
                // Store template metadata
                vm.TemplateId = template.Id;
                vm.TemplateName = template.Name;
                vm.TemplateVersion = template.Version;

                // Merge environment variables (template defaults + request overrides)
                var mergedEnvVars = new Dictionary<string, string>(template.DefaultEnvironmentVariables);
                if (request.EnvironmentVariables != null)
                {
                    foreach (var kvp in request.EnvironmentVariables)
                    {
                        mergedEnvVars[kvp.Key] = kvp.Value;
                    }
                }

                // Process cloud-init with variable substitution
                // Note: We'll do final substitution after node assignment when we have all variables
                if (!string.IsNullOrEmpty(template.CloudInitTemplate))
                {
                    // Store raw template and variables for later processing
                    vm.Spec.CloudInitUserData = template.CloudInitTemplate;
                    vm.Labels["template:cloud-init-vars"] = JsonSerializer.Serialize(mergedEnvVars);
                }

                // Configure ingress based on template's exposed ports
                if (template.ExposedPorts?.Any() == true)
                {
                    var primaryPort = template.ExposedPorts
                        .Where(p => p.IsPublic)
                        .OrderBy(p => p.Port)
                        .FirstOrDefault();

                    if (primaryPort != null)
                    {
                        // Use central ingress service to generate subdomain (respects configured base domain)
                        var subdomain = _ingressService.GenerateSubdomain(vm);

                        vm.IngressConfig = new VmIngressConfig
                        {
                            DefaultSubdomain = subdomain,
                            DefaultPort = primaryPort.Port,
                            DefaultSubdomainEnabled = true
                        };

                        _logger.LogInformation(
                            "VM {VmId} ingress configured: {Subdomain} -> port {Port}",
                            vm.Id, subdomain, primaryPort.Port);
                    }
                }

                // Set container image from template (for GPU container deployment)
                if (!string.IsNullOrEmpty(template.ContainerImage))
                {
                    vm.Spec.ContainerImage = template.ContainerImage;
                }

                // ── GPU mode propagation ─────────────────────────────────────
                // If the request didn't explicitly set a GpuMode, inherit from
                // the template so the scheduler picks a GPU-capable node and the
                // node agent receives the correct gpuMode in the CreateVm payload.
                if (vm.Spec.GpuMode == GpuMode.None && template.DefaultGpuMode != GpuMode.None)
                {
                    vm.Spec.GpuMode = template.DefaultGpuMode;
                    _logger.LogInformation(
                        "VM {VmId} inherited GpuMode={GpuMode} from template {TemplateName}",
                        vm.Id, template.DefaultGpuMode, template.Name);
                }

                if (!string.IsNullOrEmpty(template.DefaultUsername))
                    vm.Spec.SshUsername = template.DefaultUsername;

                _logger.LogInformation(
                    "VM {VmId} created from template {TemplateName} (v{Version})",
                    vm.Id, template.Name, template.Version);

                // Build service readiness list from template ExposedPorts
                vm.Services = BuildServiceList(template);
            }
            else
            {
                _logger.LogWarning("Template {TemplateId} not found for VM creation", request.TemplateId);
            }
        }

        // Ensure all VMs have at least the System service (cloud-init check)
        if (vm.Services.Count == 0)
        {
            vm.Services = BuildDefaultServiceList();
        }

        // Validate required labels for system VMs before persisting.
        // The NodeAgent reads these labels to render the cloud-init template;
        // missing labels cause silent failures on the node.
        if (isSystemVm)
        {
            var labelError = SystemVmLabelSchema.Validate(vm.Role, vm.Labels);
            if (labelError != null)
            {
                _logger.LogError(
                    "System VM {VmId} ({VmType}) failed label validation: {Error}",
                    vm.Id, vm.Role, labelError);
                return new CreateVmResponse(string.Empty, VmStatus.Pending,
                    labelError, "INVALID_LABELS");
            }
        }

        // Save to DataStore with persistence
        await _dataStore.SaveVmAsync(vm);

        // Update user quotas
        if (!isSystemVm && user != null)
        {
            user.Quotas.CurrentVms++;
            user.Quotas.CurrentVirtualCpuCores += request.Spec.VirtualCpuCores;
            user.Quotas.CurrentMemoryBytes += request.Spec.MemoryBytes;
            user.Quotas.CurrentStorageBytes += request.Spec.DiskBytes;
            await _dataStore.SaveUserAsync(user);
        }

        _logger.LogInformation("VM queued for scheduling: {VmId} ({Name}) for user {UserId}",
            vm.Id, vm.Name, userId);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmScheduled,
            ResourceType = "vm",
            ResourceId = vm.Id,
            UserId = userId,
            Payload = new Dictionary<string, object>
            {
                ["Name"] = vm.Name,
                ["CpuCores"] = vm.Spec.VirtualCpuCores,
                ["MemoryBytes"] = vm.Spec.MemoryBytes,
                ["DiskBytes"] = vm.Spec.DiskBytes,
                ["ImageId"] = vm.Spec.ImageId
            }
        });

        // Immediately try to schedule
        await TryScheduleVmAsync(vm, password, targetNodeId);

        return new CreateVmResponse(
            vm.Id,
            vm.Status,
            "VM created and queued for scheduling",
            Error: null,
            Password: password);
    }

    public async Task<bool> SecurePasswordAsync(string vmId, string userId, string encryptedPassword)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
            return false;

        if (vm.OwnerId != userId)
            return false;

        vm.Spec.WalletEncryptedPassword = encryptedPassword;

        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation("Password secured for VM {VmId}", vmId);
        return true;
    }

    public async Task<List<VirtualMachine>> GetVmsByUserAsync(string userId, VmStatus? statusFilter = null)
    {
        var userVms = await _dataStore.GetVmsByUserAsync(userId);
        var vms = userVms
            .Where(v => !statusFilter.HasValue || v.Status == statusFilter.Value)
            .OrderByDescending(v => v.CreatedAt)
            .ToList() ?? [];

        return vms;
    }

    public async Task<PagedResult<VmSummaryDto>> ListVmsAsync(string? userId, ListQueryParams queryParams)
    {
        var query = (await _dataStore.GetAllVMsAsync()).AsEnumerable();

        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(v => v.OwnerId == userId);
        }

        if (queryParams.Filters?.TryGetValue("status", out var status) == true)
        {
            if (Enum.TryParse<VmStatus>(status, true, out var statusEnum))
            {
                query = query.Where(v => v.Status == statusEnum);
            }
        }

        if (!string.IsNullOrEmpty(queryParams.Search))
        {
            var search = queryParams.Search.ToLower();
            query = query.Where(v =>
                v.Name.ToLower().Contains(search) ||
                v.Id.ToLower().Contains(search));
        }

        query = query.Where(v => v.Status != VmStatus.Deleted);

        var totalCount = query.Count();

        query = queryParams.SortBy?.ToLower() switch
        {
            "name" => queryParams.SortDescending ? query.OrderByDescending(v => v.Name) : query.OrderBy(v => v.Name),
            "status" => queryParams.SortDescending ? query.OrderByDescending(v => v.Status) : query.OrderBy(v => v.Status),
            _ => query.OrderByDescending(v => v.CreatedAt)
        };

        var vmsWithPagination = query
            .Skip((queryParams.Page - 1) * queryParams.PageSize)
            .Take(queryParams.PageSize)
            .ToList();

        // ========================================================================
        // For each VM, enrich the networkConfig with SSH jump host details
        // This keeps all network information properly organized in one object
        // ========================================================================

        var itemTasks = vmsWithPagination.Select(async v =>
        {
            // Clone the existing network config to avoid modifying the original
            var networkConfig = new VmNetworkConfig
            {
                IsIpAssigned = v.NetworkConfig.IsIpAssigned,
                PrivateIp = v.NetworkConfig.PrivateIp,
                PublicIp = v.NetworkConfig.PublicIp,
                Hostname = v.NetworkConfig.Hostname,
                PortMappings = v.NetworkConfig.PortMappings,
                OverlayNetworkId = v.NetworkConfig.OverlayNetworkId,

                // Default values (will be populated if node exists)
                SshJumpHost = null,
                SshJumpPort = 2222, // Alternative SSH port (22 blocked by some ISPs)
                NodeAgentHost = null,
                NodeAgentPort = 5100
            };

            // Populate node connection details if VM is assigned to a node
            if (!string.IsNullOrEmpty(v.NodeId))
            {
                var node = await _dataStore.GetNodeAsync(v.NodeId);
                if (node != null)
                {
                    // ============================================================
                    // SECURITY: Only expose what's needed for connection
                    // ============================================================
                    networkConfig.SshJumpHost = node.PublicIp;
                    networkConfig.SshJumpPort = 2222; // Standard SSH port
                    networkConfig.NodeAgentHost = node.PublicIp;
                    networkConfig.NodeAgentPort = node.AgentPort > 0 ? node.AgentPort : 5100;

                    _logger.LogDebug(
                        "VM {VmId} on node {NodeId}: SSH={SshHost}:{SshPort}, Agent={AgentHost}:{AgentPort}",
                        v.Id, v.NodeId,
                        networkConfig.SshJumpHost, networkConfig.SshJumpPort,
                        networkConfig.NodeAgentHost, networkConfig.NodeAgentPort);
                }
                else
                {
                    _logger.LogWarning(
                        "VM {VmId} references non-existent node {NodeId} - connection details unavailable",
                        v.Id, v.NodeId);
                }
            }

            return new VmSummaryDto(
                Id: v.Id,
                Name: v.Name,
                Status: v.Status,
                SubdomainTier: v.SubdomainTier,
                PowerState: v.PowerState,
                NodeId: v.NodeId,
                NodePublicIp: networkConfig.SshJumpHost,
                NodeAgentPort: networkConfig.NodeAgentPort,
                Spec: v.Spec,
                NetworkConfig: networkConfig,
                CreatedAt: v.CreatedAt,
                UpdatedAt: v.UpdatedAt,
                TemplateId: v.TemplateId,
                Services: v.Services,
                ComplianceHold: v.ComplianceHold
            );
        });

        var items = await Task.WhenAll(itemTasks);

        return new PagedResult<VmSummaryDto>
        {
            Items = items.ToList(),
            TotalCount = totalCount,
            Page = queryParams.Page,
            PageSize = queryParams.PageSize
        };
    }

    public async Task<bool> PerformVmActionAsync(string vmId, VmAction action, string? userId = null)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
            return false;

        if (userId != null && vm.OwnerId != userId)
            return false;

        var commandType = action switch
        {
            VmAction.Start => NodeCommandType.StartVm,
            VmAction.Stop => NodeCommandType.StopVm,
            VmAction.Restart => NodeCommandType.StopVm,
            VmAction.ForceStop => NodeCommandType.StopVm,
            _ => (NodeCommandType?)null
        };

        if (commandType == null || string.IsNullOrEmpty(vm.NodeId))
        {
            _logger.LogWarning("Cannot perform action {Action} on VM {VmId}", action, vmId);
            return false;
        }

        var command = new NodeCommand(
            Guid.NewGuid().ToString(),
            commandType.Value,
            JsonSerializer.Serialize(new
            {
                VmId = vmId,
                Action = action.ToString(),
                // Propagate force so the node powers off immediately (virsh destroy)
                // for ForceStop, rather than a graceful shutdown the guest can ignore.
                Force = action == VmAction.ForceStop
            })
        );

        _dataStore.RegisterCommand(
            command.CommandId,
            vmId,
            vm.NodeId,
            commandType.Value
        );

        vm.ActiveCommandId = command.CommandId;
        vm.ActiveCommandType = commandType.Value;
        vm.ActiveCommandIssuedAt = DateTime.UtcNow;

        await _commandService.DeliverCommandAsync(vm.NodeId, command);

        vm.Status = action switch
        {
            VmAction.Start => VmStatus.Provisioning,
            VmAction.Stop or VmAction.ForceStop => VmStatus.Stopping,
            _ => vm.Status
        };

        vm.PushMessage($"{action} command sent to node.", VmMessageLevel.Info, "user");

        await _dataStore.SaveVmAsync(vm);
        await _notifications.BroadcastStatusAsync(vmId, vm.OwnerId, vm.Status, $"{action} command sent to node.");  // ← optimistic in-flight

        _logger.LogInformation(
            "VM action {Action} queued for {VmId} (command: {CommandId})",
            action, vmId, command.CommandId);

        return true;
    }

    public async Task<VmHoldResult> SetVmComplianceHoldAsync(string vmId, bool held, string? reference = null)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
            return new VmHoldResult(false, "VM_NOT_FOUND", null, false);

        // Never hold platform infrastructure VMs.
        if (vm.Category == VmCategory.System)
            return new VmHoldResult(false, "SYSTEM_VM", vm.OwnerId, false);

        var ownerId = vm.OwnerId;
        var wasActive = vm.Status is not (VmStatus.Stopped or VmStatus.Stopping
            or VmStatus.Deleted or VmStatus.Deleting);

        vm.ComplianceHold = held;
        vm.ComplianceHoldReference = held ? reference : null;
        vm.UpdatedAt = DateTime.UtcNow;
        vm.PushMessage(
            held ? "Administratively held (compliance)." : "Compliance hold lifted.",
            VmMessageLevel.Info, "compliance");
        await _dataStore.SaveVmAsync(vm);

        // Holding a running VM stops it. Force-stop (immediate power-off) so a guest
        // cannot ignore a graceful ACPI shutdown. The hold (persisted above) is on a
        // field the heartbeat/lifecycle sync never touches, so once stopped the owner
        // still can't restart it — and heartbeat re-enforcement re-stops it if it ever
        // comes back up.
        var stopped = false;
        if (held && wasActive)
            stopped = await PerformVmActionAsync(vmId, VmAction.ForceStop);

        _logger.LogInformation(
            "Compliance hold {State} for VM {VmId} (stopped={Stopped})",
            held ? "set" : "lifted", vmId, stopped);

        return new VmHoldResult(true, null, ownerId, stopped);
    }

    public async Task<string?> RequestScanAsync(string vmId, CancellationToken ct = default)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null || string.IsNullOrEmpty(vm.NodeId))
        {
            _logger.LogWarning("RequestScanAsync: VM {VmId} not found or has no host node", vmId);
            return null;
        }

        var commandId = Guid.NewGuid().ToString();
        var payload = JsonSerializer.Serialize(new { vmId }, JsonOptions.Wire);
        var command = new NodeCommand(commandId, NodeCommandType.ScanVm, payload);

        // Register for ack routing, but DO NOT stamp ActiveCommand* — a scan is not a
        // lifecycle command and must not occupy that single slot.
        _dataStore.RegisterCommand(commandId, vmId, vm.NodeId, NodeCommandType.ScanVm);
        await _commandService.DeliverCommandAsync(vm.NodeId, command);

        _logger.LogInformation("RequestScanAsync: issued ScanVm {CommandId} for VM {VmId} on node {NodeId}",
            commandId, vmId, vm.NodeId);
        return commandId;
    }

    public async Task<bool> DeleteVmAsync(string vmId, string? userId = null)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning("DeleteVm called for non-existent VM {VmId}", vmId);
            return false;
        }

        // Evidence preservation: a held VM cannot be deleted by ANY caller —
        // owner, admin endpoint, cleanup service, or takedown. Lift the hold
        // first (POST /api/admin/compliance/resume-vm), deliberately.
        // Service-boundary guard; the controller check remains as the
        // user-facing error message.
        if (vm.ComplianceHold)
        {
            _logger.LogWarning(
                "Refusing to delete VM {VmId} — administratively held (evidence preservation)",
                vmId);
            return false;
        }

        if (userId != null && vm.OwnerId != userId)
        {
            _logger.LogWarning(
                "User {UserId} attempted to delete VM {VmId} owned by {OwnerId}",
                userId, vmId, vm.OwnerId);
            return false;
        }

        // Already being deleted or deleted
        if (vm.Status == VmStatus.Deleting || vm.Status == VmStatus.Deleted)
        {
            _logger.LogInformation("VM {VmId} already in deletion process", vmId);
            return true;
        }

        _logger.LogInformation(
            "Initiating deletion for VM {VmId} (Owner: {Owner}, Node: {Node})",
            vmId, vm.OwnerId, vm.NodeId ?? "none");

        // Step 1: Mark as DELETING (waiting for node confirmation)
        var oldStatus = vm.Status;
        vm.Status = VmStatus.Deleting;
        vm.Status = VmStatus.Deleting;
        vm.UpdatedAt = DateTime.UtcNow;
        vm.PushMessage("Deletion initiated, waiting for node confirmation", VmMessageLevel.Info, "user");
        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "VM {VmId} status changed: {OldStatus} → Deleting",
            vmId, oldStatus);

        // Step 2: Send delete command to node if assigned
        if (!string.IsNullOrEmpty(vm.NodeId))
        {
            var command = new NodeCommand(
                CommandId: Guid.NewGuid().ToString(),
                Type: NodeCommandType.DeleteVm,
                Payload: JsonSerializer.Serialize(new { VmId = vmId }),
                RequiresAck: true,
                TargetResourceId: vmId
            );

            // Register in command registry (primary lookup mechanism)
            _dataStore.RegisterCommand(
                command.CommandId,
                vmId,
                vm.NodeId,
                NodeCommandType.DeleteVm
            );

            // Also store on VM for backup lookup and status visibility
            vm.ActiveCommandId = command.CommandId;
            vm.ActiveCommandType = NodeCommandType.DeleteVm;
            vm.ActiveCommandIssuedAt = DateTime.UtcNow;

            // Queue command for node
            await _commandService.DeliverCommandAsync(vm.NodeId, command);

            _logger.LogInformation(
                "DeleteVm command {CommandId} queued for VM {VmId} on node {NodeId}",
                command.CommandId, vmId, vm.NodeId);

            // Update status message (keep command ID for legacy fallback)
            vm.PushMessage($"Delete command sent to node {vm.NodeId}.", VmMessageLevel.Info, "user");
            await _dataStore.SaveVmAsync(vm);
        }
        else
        {
            // No node assigned - transition directly to Deleted via lifecycle manager
            _logger.LogInformation(
                "VM {VmId} has no node assignment - completing deletion immediately",
                vmId);

            var lifecycleManager = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
            await lifecycleManager.TransitionAsync(vmId, VmStatus.Deleted, TransitionContext.Manual("No node - immediate deletion"));
            return true;
        }

        // Emit event
        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmDeleting,
            ResourceType = "vm",
            ResourceId = vmId,
            NodeId = vm.NodeId,
            UserId = userId
        });

        _logger.LogInformation(
            "VM {VmId} deletion initiated - awaiting node confirmation",
            vmId);

        // Resources will be freed after node confirms deletion
        return true;
    }

    // CompleteVmDeletionAsync moved to VmLifecycleManager.OnVmDeletedAsync

    public async Task<bool> UpdateVmStatusAsync(string vmId, VmStatus status, string? message = null)
    {
        // Delegate to centralized lifecycle manager for consistent side effects
        var lifecycleManager = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
        return await lifecycleManager.TransitionAsync(vmId, status, TransitionContext.Manual(message));
    }

    public async Task<bool> UpdateVmMetricsAsync(string vmId, VmMetrics metrics)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
            return false;

        vm.LatestMetrics = metrics;
        await _dataStore.SaveVmAsync(vm);

        return true;
    }

    // Scheduling pending vms requires access to plain text passwords, which is a security risk.

    /* 
    public async Task SchedulePendingVmsAsync()
    {
        var pendingVms = _dataStore.VirtualMachines.Values
            .Where(v => v.Status == VmStatus.Pending)
            .ToList();

        foreach (var vm in pendingVms)
        {
            await TryScheduleVmAsync(vm);
        }
    }
    */
    private async Task TryScheduleVmAsync(VirtualMachine vm, string? password = null, string? targetNodeId = null)
    {
        // ========================================
        // STEP 1: Calculate compute point cost FIRST
        // ========================================
        var config = await _configService.GetConfigAsync();// .TierRequirements[vm.Spec.QualityTier];
        var tierConfig = config.Tiers[vm.Spec.QualityTier];
        var isSystemVmScheduling = vm.Category is VmCategory.System;
        var pointCost = isSystemVmScheduling
            ? vm.Spec.ComputePointCost
            : vm.Spec.VirtualCpuCores * (int)tierConfig.GetPointsPerVCpu(config.BaselineBenchmark, config.BaselineOvercommitRatio);

        // CRITICAL: Store point cost in VM spec before scheduling
        vm.Spec.ComputePointCost = pointCost;

        _logger.LogInformation(
            "VM {VmId} ({Name}): Tier={Tier}, vCPUs={VCpus}, ComputePointCost={Points}",
            vm.Id, vm.Name, vm.Spec.QualityTier, vm.Spec.VirtualCpuCores, pointCost);

        // ========================================
        // STEP 2: Select node
        // ========================================

        Node? selectedNode = null;
        if (!string.IsNullOrEmpty(targetNodeId))
        {
            selectedNode = await _dataStore.GetNodeAsync(targetNodeId);

            // System VMs (Relay, DHT, BlockStore) are orchestrator-controlled:
            // they target a specific node by design and bypass eligibility checks.
            // User VMs must pass all hard filters even when the user picked a node
            // directly from the marketplace — blockstore readiness, KVM, tier
            // eligibility, architecture, and resource headroom all apply.
            var isSystemVm = vm.Role is VmRole.Relay or VmRole.Dht or VmRole.BlockStore;
            if (!isSystemVm && selectedNode != null)
            {
                var rejectionReason = await _schedulingService.ValidateNodeForVmAsync(
                    selectedNode,
                    vm.Spec);

                if (rejectionReason != null)
                {
                    _logger.LogWarning(
                        "Targeted node {NodeId} rejected for VM {VmId}: {Reason}",
                        targetNodeId, vm.Id, rejectionReason);
                    vm.Status = VmStatus.Pending;
                    vm.StatusMessage = $"Target node does not meet requirements: {rejectionReason}";
                    throw new InvalidOperationException(vm.StatusMessage);
                }
            }

            _logger.LogInformation(
                "Using target node {NodeId} for VM {VmId} ({VmType} deployment)",
                targetNodeId, vm.Id, vm.Role);
        }
        else
        {
            // Scheduling via constraint engine — all requirements are in spec.Constraints.
            selectedNode = await _schedulingService.SelectBestNodeForVmAsync(
                vm.Spec);

            _logger.LogInformation(
                "Scheduling VM {VmId} via constraint engine (tier: {Tier}, constraints: {Count})",
                vm.Id, vm.Spec.QualityTier, vm.Spec.Constraints?.Count ?? 0);
        }

        if (selectedNode == null)
        {
            _logger.LogWarning(
                "No suitable node found for VM {VmId} - Tier: {Tier}, Points: {Points}, Constraints: {Count}",
                vm.Id, vm.Spec.QualityTier, pointCost, vm.Spec.Constraints?.Count ?? 0);

            vm.Status = VmStatus.Pending;
            vm.StatusMessage = "No suitable node available for the specified requirements.";
            throw new InvalidOperationException(vm.StatusMessage);
        }

        await _dataStore.SaveVmAsync(vm);

        // ========================================
        // STEP 3: Reserve resources on node
        // ========================================
        selectedNode.ReservedResources.ComputePoints += pointCost;
        selectedNode.ReservedResources.MemoryBytes += vm.Spec.MemoryBytes;
        selectedNode.ReservedResources.StorageBytes += vm.Spec.DiskBytes;
        // Reserve VRAM for Proxied GPU VMs so FILTER 5 sees the hold before the
        // heartbeat confirms the VM as running.
        if (vm.Spec.GpuMode == GpuMode.Proxied && vm.Spec.GpuVramBytes > 0)
            selectedNode.ReservedResources.GpuVramBytes += vm.Spec.GpuVramBytes.Value;

        await _dataStore.SaveNodeAsync(selectedNode);

        _logger.LogInformation(
            "Reserved resources for VM {VmId} on node {NodeId}: {Cpu}c, {Mem}MB, {Storage}GB, {Points} points ({Tier}). " +
            "Node utilization: {AllocatedPoints}/{TotalPoints} points ({Percent:F1}%)",
            vm.Id, selectedNode.Id, vm.Spec.VirtualCpuCores, vm.Spec.MemoryBytes / (1024 * 1024), vm.Spec.DiskBytes / (1024 * 1024 * 1024), pointCost, vm.Spec.QualityTier,
            selectedNode.ReservedResources.ComputePoints,
            selectedNode.AllocatedResources.ComputePoints,
            (double)selectedNode.ReservedResources.ComputePoints /
            Math.Max(1, selectedNode.AllocatedResources.ComputePoints) * 100);

        // ========================================
        // STEP 4: Update VM assignment
        // ========================================
        vm.NodeId = selectedNode.Id;
        vm.TargetNodeId = selectedNode.Id;
        vm.Status = VmStatus.Provisioning;
        vm.NetworkConfig.PrivateIp = GeneratePrivateIp();

        // For Passthrough mode, stamp GpuVramBytes from the selected node's GPU so
        // billing uses the same per-GB-per-hour formula as Proxied mode. Only set
        // when the scheduler hasn't already provided a value.
        if (vm.Spec.GpuMode == GpuMode.Passthrough && !(vm.Spec.GpuVramBytes > 0))
        {
            var assignedGpuVram = selectedNode.HardwareInventory.Gpus.FirstOrDefault()?.MemoryBytes ?? 0L;
            if (assignedGpuVram > 0)
                vm.Spec.GpuVramBytes = assignedGpuVram;
        }

        // Stamp the per-hour rate using HourlyRateCalculator — the single
        // source of truth for the VM cost formula. Pass block count + size
        // when known (populated by LazysyncManager each cycle); falls back
        // to a 5%-of-disk estimate before the first lazysync cycle.
        vm.BillingInfo.HourlyRateCrypto = HourlyRateCalculator.Calculate(
            vm.Spec, selectedNode.Pricing, _pricingConfig,
            vm.CurrentManifestBlockCount, vm.CurrentManifestBlockSizeKb).Total;

        // Track VM hosting in node reputation
        var reputationService = _serviceProvider.GetService<INodeReputationService>();
        if (reputationService != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await reputationService.IncrementVmsHostedAsync(selectedNode.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to increment VMs hosted for node {NodeId}", selectedNode.Id);
                }
            });
        }

        // ========================================
        // STEP 5: Get SSH public key
        // ========================================
        string? sshPublicKey = vm.Spec.SshPublicKey;
        if (string.IsNullOrEmpty(sshPublicKey)
            && !string.IsNullOrEmpty(vm.OwnerId)
            && _dataStore.Users.TryGetValue(vm.OwnerId, out var owner))
        {
            if (owner.SshKeys.Any())
            {
                sshPublicKey = string.Join("\n", owner.SshKeys.Select(k => k.PublicKey));
            }
        }

        // Stamp resolved keys back to the spec so downstream
        // consumers (renderer's SshAuthorizedKeysBlockResolver, audit logs,
        // billing, diagnostic dumps) see the same value the VM actually got.
        // Idempotent — no-op when spec already had keys.
        vm.Spec.SshPublicKey = sshPublicKey;

        // ========================================
        // STEP 6: Resolve base image (URL + SHA256)
        // ========================================
        // Both URL and hash travel together so the target node can verify the
        // bytes it downloads. Empty hash is permissive: node downloads, hashes,
        // reports back via heartbeat. See BASE_IMAGE_DESIGN.md §4.
        var imageDescriptor = GetImageDescriptor(
            vm.Spec.ImageId,
            selectedNode.HardwareInventory?.Cpu?.Architecture);
        string? imageUrl = imageDescriptor?.Url;
        string imageHash = imageDescriptor?.Sha256 ?? string.Empty;

        // ════════════════════════════════════════════════════════════════════════
        // STEP 6.5: Render cloud-init (unified pipeline)
        // ════════════════════════════════════════════════════════════════════════
        // Single pipeline for all templates. The renderer handles:
        //   Pass 1:  declared __VARNAME__ statics via resolver registry
        //   Pass 1b: ${VARNAME} platform variables from UserSuppliedStatics
        //   Pass 2:  ${ARTIFACT_URL:name} / ${ARTIFACT_SHA256:name} from Artifacts
        //   Pass 3:  validation (if strictValidation enabled)
        //
        // Legacy ${VARNAME} marketplace templates (no declared Variables) work
        // because Pass 1b substitutes from UserSuppliedStatics, and Pass 2
        // handles artifacts. No separate code path needed.

        string? processedCloudinitUserData = vm.Spec.CloudInitUserData;

        VmTemplate? template = !string.IsNullOrEmpty(vm.TemplateId)
            ? await _templateService.GetTemplateByIdAsync(vm.TemplateId)
            : null;

        if (!string.IsNullOrEmpty(vm.Spec.CloudInitUserData))
        {
            try
            {
                var rawArch = selectedNode.HardwareInventory?.Cpu?.Architecture;
                var archTag = rawArch?.ToLowerInvariant() switch
                {
                    "arm64" or "aarch64" => "arm64",
                    "x86_64" or "amd64" => "amd64",
                    _ => "amd64",
                };

                var orchestratorUrl = _configuration["OrchestratorClient:BaseUrl"];
                if (string.IsNullOrWhiteSpace(orchestratorUrl))
                {
                    throw new InvalidOperationException(
                        "Configuration 'OrchestratorClient:BaseUrl' is not set. " +
                        "Required for cloud-init rendering.");
                }

                // ── Build UserSuppliedStatics ──────────────────────────────
                // Three-layer merge (increasing priority):
                //   Layer 1: template.DefaultEnvironmentVariables
                //   Layer 2: custom/template env vars from VM labels
                //   Layer 3: platform variables from GetAvailableVariables
                //            + password + SSH keys

                var userSupplied = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

                // Layer 1: template defaults
                if (template?.DefaultEnvironmentVariables != null)
                {
                    foreach (var (k, v) in template.DefaultEnvironmentVariables)
                        userSupplied[k] = v;
                }

                // Layer 2: label overrides
                string? envVarsLabel = null;
                if (vm.Labels.TryGetValue("custom:cloud-init-vars", out var customVars))
                    envVarsLabel = customVars;
                else if (vm.Labels.TryGetValue("template:cloud-init-vars", out var templateVars))
                    envVarsLabel = templateVars;

                if (!string.IsNullOrEmpty(envVarsLabel))
                {
                    try
                    {
                        var envVars = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            envVarsLabel);
                        if (envVars != null)
                            foreach (var (k, v) in envVars)
                                userSupplied[k] = v;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "VM {VmId}: failed to parse cloud-init env vars from labels",
                            vm.Id);
                    }
                }

                // Layer 3: platform variables (highest priority)
                var platformVars = _templateService.GetAvailableVariables(vm, selectedNode);
                foreach (var (k, v) in platformVars)
                    userSupplied[k] = v;

                if (!string.IsNullOrEmpty(password))
                    userSupplied["DECLOUD_PASSWORD"] = password;

                // Platform secrets
                userSupplied["ADMIN_PASSWORD"] = password ?? "";
                userSupplied["SSH_PUBLIC_KEYS"] = sshPublicKey ?? "";

                // ── Build ResolutionContext ────────────────────────────────
                var ctx = new ResolutionContext(
                    Node: selectedNode,
                    Obligation: null,
                    Vm: vm,
                    Template: template ?? new VmTemplate { Slug = "custom" },
                    OrchestratorUrl: orchestratorUrl,
                    TargetArchitecture: archTag,
                    UserSuppliedStatics: userSupplied);

                // Marketplace templates typically have no declared Variables,
                // so skip strict validation (it would flag ${VARNAME}
                // patterns as undeclared __VARNAME__ leaks). Templates WITH
                // declared Variables get full validation.
                bool strict = template is { Variables.Count: > 0 };

                processedCloudinitUserData = await _cloudInitRenderer.RenderAsync(
                    template ?? new VmTemplate
                    {
                        Slug = "custom",
                        CloudInitTemplate = vm.Spec.CloudInitUserData!,
                        Variables = new(),
                        Artifacts = new(),
                    },
                    ctx, CancellationToken.None, strictValidation: strict);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "VM {VmId}: cloud-init rendering failed for template {Slug}. " +
                    "Check Variables declarations, resolver registry, and " +
                    "UserSuppliedStatics population.",
                    vm.Id, template?.Slug ?? "custom");
                throw;
            }
        }

        // ========================================
        // STEP 7: Resolve GPU mode and assignment
        // ========================================
        // The orchestrator owns GPU scheduling — it sets gpuMode explicitly
        // based on the VM's requested mode and the node's capabilities.
        // The node agent no longer auto-detects GPU mode.
        string? gpuPciAddress = null;
        var deploymentMode = vm.Spec.DeploymentMode;
        var resolvedGpuMode = vm.Spec.GpuMode;

        if (resolvedGpuMode == GpuMode.Passthrough)
        {
            // VFIO passthrough — assign a specific GPU PCI address
            var passthroughGpu = selectedNode.HardwareInventory.Gpus
                .FirstOrDefault(g => g.IsAvailableForPassthrough);

            if (passthroughGpu != null)
            {
                gpuPciAddress = passthroughGpu.PciAddress;
                deploymentMode = DeploymentMode.VirtualMachine;
                _logger.LogInformation(
                    "VM {VmId} assigned GPU {GpuModel} via passthrough (GpuMode=1) at PCI {PciAddress} on node {NodeId}",
                    vm.Id, passthroughGpu.Model, gpuPciAddress, selectedNode.Id);
            }
            else
            {
                // Node passed scheduling filter but GPU may have been claimed between filter and here.
                // Send gpuMode=1 without gpuPciAddress — node agent will auto-assign from its pool.
                _logger.LogWarning(
                    "VM {VmId} requested passthrough GPU but no specific GPU available on node {NodeId}. " +
                    "Sending gpuMode=1 without gpuPciAddress — node agent will auto-assign from pool.",
                    vm.Id, selectedNode.Id);
            }
        }
        else if (resolvedGpuMode == GpuMode.Proxied)
        {
            // Proxied mode — shared GPU over virtio-vsock, no PCI address needed
            _logger.LogInformation(
                "VM {VmId} using proxied GPU (GpuMode=2) on node {NodeId} — shared GPU, no IOMMU required",
                vm.Id, selectedNode.Id);
            // gpuPciAddress stays null (not used for proxied mode)
        }
        // GpuMode.None — no GPU setup needed

        // Store resolved deployment mode
        vm.Spec.DeploymentMode = deploymentMode;

        // ========================================
        // STEP 8: Create command with ALL required fields
        // ========================================

        // Typed wire contract — single source of truth for the CreateVm payload.
        // Enum encoding (string vs int) is owned by the enum types' [JsonConverter]
        // attributes, so it is symmetric with the node's typed deserialization;
        // we no longer hand-cast to int here.
        var createPayload = new CreateVmPayload
        {
            VmId = vm.Id,
            Name = vm.Name,
            Category = vm.Category,
            Role = vm.Role,

            OwnerId = vm.OwnerId,
            OwnerWallet = vm.OwnerWallet,

            VirtualCpuCores = vm.Spec.VirtualCpuCores,
            MemoryBytes = vm.Spec.MemoryBytes,
            DiskBytes = vm.Spec.DiskBytes,
            QualityTier = vm.Spec.QualityTier,
            ComputePointCost = vm.Spec.ComputePointCost,

            // GPU scheduling: orchestrator sets GpuMode explicitly (node agent no longer auto-detects)
            GpuMode = resolvedGpuMode,
            GpuPciAddress = gpuPciAddress,
            GpuVramBytes = vm.Spec.GpuVramBytes,

            DeploymentMode = deploymentMode,
            ContainerImage = vm.Spec.ContainerImage,
            BaseImageUrl = imageUrl,
            BaseImageHash = imageHash,
            // Cloud-init template (variables already substituted). Property name
            // now matches the node's VmSpec.CloudInitUserData — fixes the previous
            // CloudinitUserData/UserData wire mismatch that silently dropped it.
            CloudInitUserData = processedCloudinitUserData,
            SshPublicKey = sshPublicKey ?? "",
            // EnvironmentVariables intentionally not set here: the orchestrator
            // delivers container/cloud-init env via the "custom:cloud-init-vars"
            // label, not as a wire dict. Left null to preserve existing behavior.

            // Transient secrets — stripped from the orchestrator record after delivery.
            Password = password,
            Labels = vm.Labels,

            ReplicationFactor = vm.Spec.ReplicationFactor,
            TargetNodeId = vm.TargetNodeId,

            // Per-service readiness definitions for VmReadinessMonitor on the node agent.
            // CheckType travels as a string (matches SystemVmServiceDeclaration's convention).
            Services = vm.Services.Select(s => new VmServiceDefinition
            {
                Name = s.Name,
                Port = s.Port,
                Protocol = s.Protocol,
                CheckType = s.CheckType.ToString(),
                HttpPath = s.HttpPath,
                ExecCommand = s.ExecCommand,
                LivenessCheck = s.LivenessCheck,
                TimeoutSeconds = s.TimeoutSeconds
            }).ToList(),

            // P2.5 — node-side IVmDeploymentPipeline prefetches these into the
            // local artifact cache before VM creation. Empty list when no template
            // (custom cloud-init, container, or template-less). Pipeline arch-filters internally.
            Artifacts = template?.Artifacts ?? new List<TemplateArtifact>()
        };

        var command = new NodeCommand(
            Guid.NewGuid().ToString(),
            NodeCommandType.CreateVm,
            JsonSerializer.Serialize(createPayload),
            RequiresAck: true,
            TargetResourceId: vm.Id
        );

        vm.ActiveCommandId = command.CommandId;
        vm.ActiveCommandType = NodeCommandType.CreateVm;
        vm.ActiveCommandIssuedAt = DateTime.UtcNow;

        // Register in command registry (primary lookup mechanism)
        _dataStore.RegisterCommand(
            command.CommandId,
            vm.Id,
            vm.NodeId,
            NodeCommandType.CreateVm
        );

        await _commandService.DeliverCommandAsync(selectedNode.Id, command);

        // SECURITY: Strip sensitive labels after command delivery.
        // The node agent received them in the command payload; they must not persist
        // in the orchestrator database or be exposed via API responses.
        StripSensitiveLabels(vm.Labels);

        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "{VmType} VM {VmId} scheduled on node {NodeId}", vm.Role, vm.Id, selectedNode.Id);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmScheduled,
            ResourceType = "vm",
            ResourceId = vm.Id,
            UserId = vm.OwnerId,
            NodeId = selectedNode.Id
        });
    }

    private static string GenerateMemorablePassword()
    {
        var adjectives = new[] { "happy", "bright", "swift", "calm", "bold", "wise", "brave", "cool", "warm", "kind" };
        var nouns = new[] { "cloud", "river", "mountain", "forest", "ocean", "tiger", "eagle", "phoenix", "dragon", "wolf" };
        var verbs = new[] { "runs", "jumps", "flies", "swims", "climbs", "soars", "dances", "sings", "glides", "roars" };

        var random = RandomNumberGenerator.GetInt32(0, int.MaxValue);
        var adj = adjectives[random % adjectives.Length];
        var noun = nouns[(random / 10) % nouns.Length];
        var verb = verbs[(random / 100) % verbs.Length];
        var num = RandomNumberGenerator.GetInt32(10, 100);

        return $"{adj}-{noun}-{verb}-{num}";
    }

    private static string GeneratePrivateIp()
    {
        var random = RandomNumberGenerator.GetInt32(2, 254);
        return $"10.100.0.{random}";
    }

    /// <summary>
    /// Resolve a tenant VM imageId + node architecture to a base image
    /// descriptor (URL + SHA256) from the consolidated image registry.
    /// Returns null if the imageId is not in the catalogue or the node's
    /// architecture is not supported by that image.
    /// See BASE_IMAGE_DESIGN.md §7.
    /// </summary>
    private BaseImageDescriptor? GetImageDescriptor(string imageId, string? nodeArchitecture)
    {
        if (!_dataStore.Images.TryGetValue(imageId, out var image))
            return null;
        var archTag = VmImage.NormaliseArchTag(nodeArchitecture) ?? "amd64";
        return image.ByArchitecture.GetValueOrDefault(archTag);
    }

    // SettleTemplateFeeAsync and AutoAllocateTemplatePortsAsync moved to VmLifecycleManager.cs

    // =========================================================================
    // Service Readiness Helpers
    // =========================================================================

    /// <summary>
    /// Build the per-service readiness list from a template's ExposedPorts.
    /// Always includes the implicit "System" (cloud-init) service as the first entry.
    /// Check strategies are inferred from port protocol unless explicitly overridden.
    /// </summary>
    private static List<VmServiceModel> BuildServiceList(VmTemplate template)
    {
        var services = new List<VmServiceModel>
        {
            // Implicit "System" service — cloud-init completion check
            new VmServiceModel
            {
                Name = "System",
                CheckType = CheckType.CloudInitDone,
                Status = ServiceStatus.Pending,
                TimeoutSeconds = 300
            }
        };

        if (template.ExposedPorts == null)
            return services;

        foreach (var port in template.ExposedPorts)
        {
            var service = new VmServiceModel
            {
                Name = string.IsNullOrEmpty(port.Description) ? $"Port {port.Port}" : port.Description,
                Port = port.Port,
                Protocol = port.Protocol,
                Status = ServiceStatus.Pending
            };

            if (port.ReadinessCheck != null)
            {
                // Explicit override from template author
                service.CheckType = port.ReadinessCheck.Strategy switch
                {
                    CheckStrategy.TcpPort => CheckType.TcpPort,
                    CheckStrategy.HttpGet => CheckType.HttpGet,
                    CheckStrategy.ExecCommand => CheckType.ExecCommand,
                    _ => CheckType.TcpPort
                };
                service.HttpPath = port.ReadinessCheck.HttpPath;
                service.ExecCommand = port.ReadinessCheck.ExecCommand;
                service.LivenessCheck = port.ReadinessCheck.LivenessCheck;
                service.TimeoutSeconds = port.ReadinessCheck.TimeoutSeconds;
            }
            else
            {
                // Infer check strategy from protocol
                var (checkType, httpPath) = InferCheckStrategy(port.Protocol);
                service.CheckType = checkType;
                service.HttpPath = httpPath;
                service.TimeoutSeconds = 300;
            }

            services.Add(service);
        }

        return services;
    }

    /// <summary>
    /// Default service list for VMs without a template (bare VMs).
    /// Only the System (cloud-init) service.
    /// </summary>
    private static List<VmServiceModel> BuildDefaultServiceList()
    {
        return new List<VmServiceModel>
        {
            new VmServiceModel
            {
                Name = "System",
                CheckType = CheckType.CloudInitDone,
                Status = ServiceStatus.Pending,
                TimeoutSeconds = 300
            }
        };
    }

    /// <summary>
    /// Infer the readiness check strategy from the port protocol.
    /// </summary>
    private static (CheckType checkType, string? httpPath) InferCheckStrategy(string protocol)
    {
        return protocol.ToLowerInvariant() switch
        {
            "http" or "https" or "ws" or "wss" => (CheckType.HttpGet, "/"),
            "tcp" or "both" => (CheckType.TcpPort, null),
            _ => (CheckType.TcpPort, null) // default to TCP for unknown protocols
        };
    }

    /// <summary>
    /// Labels that carry secrets needed only for the initial CreateVm command.
    /// These are delivered to the node agent in the command payload and must be
    /// stripped before the VM record is persisted to the database.
    /// </summary>
    private static readonly HashSet<string> SensitiveLabels = new(StringComparer.Ordinal)
    {
        "wireguard-private-key",
    };

    /// <summary>
    /// Remove labels that contain secrets from the VM's label dictionary.
    /// Called after the CreateVm command has been delivered to the node agent
    /// so the secrets are never persisted in the orchestrator database or
    /// exposed via API responses.
    /// </summary>
    private static void StripSensitiveLabels(Dictionary<string, string> labels)
    {
        foreach (var key in SensitiveLabels)
        {
            labels.Remove(key);
        }
    }
}