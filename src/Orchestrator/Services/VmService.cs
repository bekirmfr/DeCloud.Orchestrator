using Microsoft.Extensions.Options;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;
using Orchestrator.Services.SystemVm;
using Orchestrator.Services.VmScheduling;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orchestrator.Services;

public interface IVmService
{
    Task<CreateVmResponse> CreateVmAsync(string userId, CreateVmRequest request, string? targetNodeId = null);
    Task<List<VirtualMachine>> GetVmsByUserAsync(string userId, VmStatus? statusFilter = null);
    Task<PagedResult<VmSummary>> ListVmsAsync(string? userId, ListQueryParams queryParams);
    Task<bool> PerformVmActionAsync(string vmId, VmAction action, string? userId = null);

    Task<bool> DeleteVmAsync(string vmId, string? userId = null);
    Task<bool> UpdateVmStatusAsync(string vmId, VmStatus status, string? message = null);
    Task<bool> UpdateVmMetricsAsync(string vmId, VmMetrics metrics);
    //Task SchedulePendingVmsAsync();
    Task<bool> SecurePasswordAsync(string vmId, string userId, string encryptedPassword);
}

public class VmService : IVmService
{
    private readonly DataStore _dataStore;
    private readonly INodeCommandService _commandService;
    private readonly IVmSchedulingService _schedulingService;
    private readonly ISchedulingConfigService _configService;
    private readonly IEventService _eventService;
    private readonly ICentralIngressService _ingressService;
    private readonly INetworkLatencyTracker _latencyTracker;
    private readonly ITemplateService _templateService;
    private readonly IVmNameService _nameService;
    private readonly PricingConfig _pricingConfig;
    private readonly ILogger<VmService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public VmService(
        DataStore dataStore,
        INodeCommandService commandService,
        IVmSchedulingService schedulingService,
        ISchedulingConfigService configService,
        IEventService eventService,
        ICentralIngressService ingressService,
        INetworkLatencyTracker latencyTracker,
        ITemplateService templateService,
        IVmNameService nameService,
        IOptions<PricingConfig> pricingConfig,
        ILogger<VmService> logger,
        IServiceProvider serviceProvider)
    {
        _dataStore = dataStore;
        _commandService = commandService;
        _schedulingService = schedulingService;
        _configService = configService;
        _eventService = eventService;
        _ingressService = ingressService;
        _latencyTracker = latencyTracker;
        _templateService = templateService;
        _nameService = nameService;
        _pricingConfig = pricingConfig.Value;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task<CreateVmResponse> CreateVmAsync(string userId, CreateVmRequest request, string? targetNodeId = null)
    {
        var isSystemVm = request.VmType is VmType.Relay or VmType.Dht;

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

        // Calculate pricing
        var hourlyRate = CalculateHourlyRate(request.Spec);

        var vm = new VirtualMachine
        {
            Id = Guid.NewGuid().ToString(),
            Name = canonicalName,
            VmType = request.VmType,
            SubdomainTier = SubdomainTier.Free,
            OwnerId = isSystemVm ? null : userId,
            OwnerWallet = isSystemVm ? null : user?.WalletAddress,
            Spec = request.Spec,
            Status = VmStatus.Pending,
            Labels = request.Labels ?? [],
            BillingInfo = new VmBillingInfo
            {
                HourlyRateCrypto = hourlyRate,
                CryptoSymbol = "USDC"
            },
            NetworkConfig = new VmNetworkConfig
            {
                Hostname = canonicalName
            },
            NetworkMetrics = VmNetworkMetrics.CreateDefault(),
        };

        // ════════════════════════════════════════════════════════════════════════
        // Cloud-Init Processing (Priority: Custom > Template > None)
        // ════════════════════════════════════════════════════════════════════════
        
        // Phase 2: Custom cloud-init (overrides template)
        if (!string.IsNullOrEmpty(request.CustomCloudInit))
        {
            vm.Spec.UserData = request.CustomCloudInit;
            
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
                    vm.Spec.UserData = template.CloudInitTemplate;
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

                // Promote VmType to Inference for GPU templates when the caller
                // left it at the default (General). This ensures the node agent
                // treats the workload correctly and billing/metrics are accurate.
                if (vm.Spec.RequiresGpu && vm.VmType == VmType.General)
                {
                    vm.VmType = VmType.Inference;
                    _logger.LogInformation(
                        "VM {VmId} promoted to VmType=Inference (template {TemplateName} requires GPU)",
                        vm.Id, template.Name);
                }

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
            var labelError = SystemVmLabelSchema.Validate(vm.VmType, vm.Labels);
            if (labelError != null)
            {
                _logger.LogError(
                    "System VM {VmId} ({VmType}) failed label validation: {Error}",
                    vm.Id, vm.VmType, labelError);
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

    public async Task<PagedResult<VmSummary>> ListVmsAsync(string? userId, ListQueryParams queryParams)
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

            return new VmSummary(
                Id: v.Id,
                Name: v.Name,
                SubdomainTier: v.SubdomainTier,
                Status: v.Status,
                PowerState: v.PowerState,
                NodeId: v.NodeId,
                NodePublicIp: networkConfig.SshJumpHost,
                NodeAgentPort: networkConfig.NodeAgentPort,
                Spec: v.Spec,
                NetworkConfig: networkConfig,
                CreatedAt: v.CreatedAt,
                UpdatedAt: v.UpdatedAt,
                TemplateId: v.TemplateId,
                Services: v.Services
            );
        });

        var items = await Task.WhenAll(itemTasks);

        return new PagedResult<VmSummary>
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
            JsonSerializer.Serialize(new { VmId = vmId, Action = action.ToString() })
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

        vm.StatusMessage = $"Action {action} command {command.CommandId} sent to node";

        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "VM action {Action} queued for {VmId} (command: {CommandId})",
            action, vmId, command.CommandId);

        return true;
    }

    public async Task<bool> DeleteVmAsync(string vmId, string? userId = null)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning("DeleteVm called for non-existent VM {VmId}", vmId);
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
        vm.StatusMessage = "Deletion initiated, waiting for node confirmation";
        vm.UpdatedAt = DateTime.UtcNow;
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
            vm.StatusMessage = $"Deletion command {command.CommandId} sent to node";
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
        var isSystemVmScheduling = vm.Spec.VmType is VmType.Relay or VmType.Dht;
        var pointCost = isSystemVmScheduling ? vm.Spec.ComputePointCost : vm.Spec.VirtualCpuCores *
            (int)tierConfig.GetPointsPerVCpu(config.BaselineBenchmark, config.BaselineOvercommitRatio);

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
            // Use specified node (for system VM deployments: relay, DHT, etc.)
            selectedNode = await _dataStore.GetNodeAsync(targetNodeId);
            _logger.LogInformation(
                "Using target node {NodeId} for VM {VmId} ({VmType} deployment)",
                targetNodeId, vm.Id, vm.VmType);
        }
        else
        {
            // Normal scheduling for user VMs with region/zone preferences
            selectedNode = await _schedulingService.SelectBestNodeForVmAsync(
                vm.Spec,
                vm.Spec.QualityTier,
                vm.Spec.Region,
                vm.Spec.Zone);

            _logger.LogInformation(
                "Scheduling VM {VmId} with preferences: Region={Region}, Zone={Zone}",
                vm.Id, vm.Spec.Region ?? "any", vm.Spec.Zone ?? "any");
        }

        if (selectedNode == null)
        {
            var regionConstraint = !string.IsNullOrEmpty(vm.Spec.Region)
                ? $" in region '{vm.Spec.Region}'"
                : "";
            var zoneConstraint = !string.IsNullOrEmpty(vm.Spec.Zone)
                ? $" zone '{vm.Spec.Zone}'"
                : "";

            _logger.LogWarning(
                "No suitable node found for VM {VmId} - Tier: {Tier}, Points: {Points}, Region: {Region}, Zone: {Zone}",
                vm.Id, vm.Spec.QualityTier, pointCost, vm.Spec.Region ?? "any", vm.Spec.Zone ?? "any");

            vm.Status = VmStatus.Pending;
            vm.StatusMessage = $"No suitable node available{regionConstraint}{zoneConstraint}";
            throw new InvalidOperationException(vm.StatusMessage);
        }
        
        await _dataStore.SaveVmAsync(vm);

        // ========================================
        // STEP 3: Reserve resources on node
        // ========================================
        selectedNode.ReservedResources.ComputePoints += pointCost;
        selectedNode.ReservedResources.MemoryBytes += vm.Spec.MemoryBytes;
        selectedNode.ReservedResources.StorageBytes += vm.Spec.DiskBytes;

        await _dataStore.SaveNodeAsync(selectedNode);

        _logger.LogInformation(
            "Reserved resources for VM {VmId} on node {NodeId}: {Cpu}c, {Mem}MB, {Storage}GB, {Points} points ({Tier}). " +
            "Node utilization: {AllocatedPoints}/{TotalPoints} points ({Percent:F1}%)",
            vm.Id, selectedNode.Id, vm.Spec.VirtualCpuCores, vm.Spec.MemoryBytes / (1024 * 1024), vm.Spec.DiskBytes / (1024 * 1024 * 1024), pointCost, vm.Spec.QualityTier,
            selectedNode.ReservedResources.ComputePoints,
            selectedNode.TotalResources.ComputePoints,
            (double)selectedNode.ReservedResources.ComputePoints /
            Math.Max(1, selectedNode.TotalResources.ComputePoints) * 100);

        // ========================================
        // STEP 4: Update VM assignment
        // ========================================
        vm.NodeId = selectedNode.Id;
        vm.Status = VmStatus.Provisioning;
        vm.NetworkConfig.PrivateIp = GeneratePrivateIp();

        // Recalculate hourly rate with node-specific pricing (replaces platform defaults)
        vm.BillingInfo.HourlyRateCrypto = CalculateHourlyRate(vm.Spec, selectedNode.Pricing);

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

        // ========================================
        // STEP 6: Get image URL
        // ========================================
        string? imageUrl = GetImageUrl(vm.Spec.ImageId);

        // ========================================
        // STEP 6.5: Process cloud-init with variable substitution
        // ========================================
        string? processedUserData = vm.Spec.UserData;
        if (!string.IsNullOrEmpty(vm.Spec.UserData))
        {
            try
            {
                // Get all available variables
                var variables = _templateService.GetAvailableVariables(vm, selectedNode);
                
                // Add password to variables (if available)
                if (!string.IsNullOrEmpty(password))
                {
                    variables["DECLOUD_PASSWORD"] = password;
                }
                
                // Merge with environment variables (from template or custom)
                // Priority: custom > template
                string? envVarsLabel = null;
                if (vm.Labels.TryGetValue("custom:cloud-init-vars", out var customVars))
                {
                    envVarsLabel = customVars;
                }
                else if (vm.Labels.TryGetValue("template:cloud-init-vars", out var templateVars))
                {
                    envVarsLabel = templateVars;
                }
                
                if (!string.IsNullOrEmpty(envVarsLabel))
                {
                    try
                    {
                        var envVars = JsonSerializer.Deserialize<Dictionary<string, string>>(envVarsLabel);
                        if (envVars != null)
                        {
                            foreach (var kvp in envVars)
                            {
                                variables[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse cloud-init environment variables for VM {VmId}", vm.Id);
                    }
                }
                
                // Substitute variables in cloud-init
                processedUserData = _templateService.SubstituteCloudInitVariables(vm.Spec.UserData, variables);
                
                var source = !string.IsNullOrEmpty(vm.TemplateId) ? $"template {vm.TemplateName}" : "custom cloud-init";
                _logger.LogInformation(
                    "Processed cloud-init for VM {VmId} from {Source}",
                    vm.Id, source);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process cloud-init for VM {VmId}", vm.Id);
                // Continue with unprocessed cloud-init as fallback
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

        var command = new NodeCommand(
            Guid.NewGuid().ToString(),
            NodeCommandType.CreateVm,
            JsonSerializer.Serialize(new
            {
                VmId = vm.Id,
                Name = vm.Name,
                VmType = (int) vm.VmType,
                OwnerId = vm.OwnerId,
                OwnerWallet = vm.OwnerWallet,
                VirtualCpuCores = vm.Spec.VirtualCpuCores,
                MemoryBytes = vm.Spec.MemoryBytes,
                DiskBytes = vm.Spec.DiskBytes,
                QualityTier = (int)vm.Spec.QualityTier,
                ComputePointCost = vm.Spec.ComputePointCost,
                BaseImageUrl = imageUrl,
                SshPublicKey = sshPublicKey ?? "",
                // GPU scheduling: orchestrator sets gpuMode explicitly (node agent no longer auto-detects)
                GpuMode = (int)resolvedGpuMode,
                GpuPciAddress = gpuPciAddress,
                DeploymentMode = (int)deploymentMode,
                ContainerImage = vm.Spec.ContainerImage,
                Network = new
                {
                    MacAddress = "",
                    IpAddress = vm.NetworkConfig.PrivateIp,
                    Gateway = "",
                    VxlanVni = 0,
                    AllowedPorts = new List<int>()
                },
                Password = password,
                UserData = processedUserData, // Cloud-init template (with variables substituted)
                Labels = vm.Labels,
                // Per-service readiness definitions for VmReadinessMonitor on node agent
                Services = vm.Services.Select(s => new
                {
                    s.Name,
                    s.Port,
                    s.Protocol,
                    CheckType = s.CheckType.ToString(),
                    s.HttpPath,
                    s.ExecCommand,
                    s.TimeoutSeconds
                }).ToList()
            }),
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
            "{VmType} VM {VmId} scheduled on node {NodeId}", vm.VmType, vm.Id, selectedNode.Id);

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

    /// <summary>
    /// Calculate the hourly rate for a VM based on its spec and the node's pricing.
    /// Uses node operator pricing if set, otherwise platform defaults.
    /// All rates are clamped to platform floor minimums.
    /// </summary>
    private decimal CalculateHourlyRate(VmSpec spec, NodePricing? nodePricing = null)
    {
        if (spec.VmType is VmType.Relay or VmType.Dht)
            return 0.005m; // Flat rate for system VMs

        const decimal BYTES_PER_GB = 1024m * 1024m * 1024m;
        var cfg = _pricingConfig;

        // Resolve per-resource rates: node pricing > platform default > floor
        var cpuRate = (nodePricing?.CpuPerHour > 0 ? nodePricing.CpuPerHour : cfg.DefaultCpuPerHour);
        var memRate = (nodePricing?.MemoryPerGbPerHour > 0 ? nodePricing.MemoryPerGbPerHour : cfg.DefaultMemoryPerGbPerHour);
        var storageRate = (nodePricing?.StoragePerGbPerHour > 0 ? nodePricing.StoragePerGbPerHour : cfg.DefaultStoragePerGbPerHour);

        // Enforce platform floor (nodes can't undercut)
        cpuRate = Math.Max(cpuRate, cfg.FloorCpuPerHour);
        memRate = Math.Max(memRate, cfg.FloorMemoryPerGbPerHour);
        storageRate = Math.Max(storageRate, cfg.FloorStoragePerGbPerHour);

        // Bandwidth tier pricing (per hour, platform-set)
        var bandwidthRate = spec.BandwidthTier switch
        {
            BandwidthTier.Basic => 0.002m,        // 10 Mbps
            BandwidthTier.Standard => 0.008m,     // 50 Mbps
            BandwidthTier.Performance => 0.020m,  // 200 Mbps
            _ => 0.040m                           // Unmetered
        };

        // Quality tier price multiplier (must match frontend QUALITY_TIERS)
        var tierMultiplier = spec.QualityTier switch
        {
            QualityTier.Guaranteed => 2.5m,
            QualityTier.Standard => 1.0m,
            QualityTier.Balanced => 0.6m,
            QualityTier.Burstable => 0.4m,
            _ => 1.0m
        };

        var resourceCost =
            (spec.VirtualCpuCores * cpuRate) +
            ((spec.MemoryBytes / BYTES_PER_GB) * memRate) +
            ((spec.DiskBytes / BYTES_PER_GB) * storageRate);

        return (resourceCost * tierMultiplier) + bandwidthRate;
    }

    private static string GeneratePrivateIp()
    {
        var random = RandomNumberGenerator.GetInt32(2, 254);
        return $"10.100.0.{random}";
    }

    private string? GetImageUrl(string imageId)
    {
        if (!_dataStore.Images.TryGetValue(imageId, out var image))
            return null;

        return imageId switch
        {
            "ubuntu-24.04" => "https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img",
            "ubuntu-22.04" => "https://cloud-images.ubuntu.com/jammy/current/jammy-server-cloudimg-amd64.img",
            "debian-12" => "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
            "fedora-40" => "https://download.fedoraproject.org/pub/fedora/linux/releases/40/Cloud/x86_64/images/Fedora-Cloud-Base-Generic.x86_64-40-1.14.qcow2",
            "alpine-3.19" => "https://dl-cdn.alpinelinux.org/alpine/v3.19/releases/cloud/alpine-virt-3.19.0-x86_64.qcow2",
            // System VMs use Debian 12 (~2 GiB) — smaller than Ubuntu (~3.5 GiB), systemd-compatible
            // so cloud-init templates work as-is. Both share the same cached base image.
            "debian-12-dht" or "debian-12-relay" =>
                "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
            _ => null
        };
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
    private static List<VmServiceStatus> BuildServiceList(VmTemplate template)
    {
        var services = new List<VmServiceStatus>
        {
            // Implicit "System" service — cloud-init completion check
            new VmServiceStatus
            {
                Name = "System",
                CheckType = CheckType.CloudInitDone,
                Status = ServiceReadiness.Pending,
                TimeoutSeconds = 300
            }
        };

        if (template.ExposedPorts == null)
            return services;

        foreach (var port in template.ExposedPorts)
        {
            var service = new VmServiceStatus
            {
                Name = string.IsNullOrEmpty(port.Description) ? $"Port {port.Port}" : port.Description,
                Port = port.Port,
                Protocol = port.Protocol,
                Status = ServiceReadiness.Pending
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
    private static List<VmServiceStatus> BuildDefaultServiceList()
    {
        return new List<VmServiceStatus>
        {
            new VmServiceStatus
            {
                Name = "System",
                CheckType = CheckType.CloudInitDone,
                Status = ServiceReadiness.Pending,
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