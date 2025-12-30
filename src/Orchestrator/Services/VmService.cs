using DeCloud.Shared.Models;
using Orchestrator.Data;
using Orchestrator.Models;
using System.Security.Cryptography;
using System.Text.Json;

namespace Orchestrator.Services;

public interface IVmService
{
    Task<CreateVmResponse> CreateVmAsync(string userId, CreateVmRequest request);
    Task<VirtualMachine?> GetVmAsync(string vmId);
    Task<List<VirtualMachine>> GetVmsByUserAsync(string userId, VmStatus? statusFilter = null);
    Task<List<VirtualMachine>> GetVmsByNodeAsync(string nodeId);
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
    private readonly INodeService _nodeService;
    private readonly IEventService _eventService;
    private readonly ICentralIngressService _ingressService;
    private readonly SchedulingConfiguration _schedulingConfig;
    private readonly ILogger<VmService> _logger;

    public VmService(
        DataStore dataStore,
        INodeService nodeService,
        IEventService eventService,
        ICentralIngressService ingressService,
        SchedulingConfiguration schedulingConfig,
        ILogger<VmService> logger)
    {
        _dataStore = dataStore;
        _nodeService = nodeService;
        _eventService = eventService;
        _ingressService = ingressService;
        _schedulingConfig = schedulingConfig;
        _logger = logger;
    }

    public async Task<CreateVmResponse> CreateVmAsync(string userId, CreateVmRequest request)
    {
        // Validate user exists
        User? user = null;
        if (_dataStore.Users.TryGetValue(userId, out var existingUser))
        {
            user = existingUser;
        }

        // Check quotas
        if (user != null)
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
        var password = GenerateMemorablePassword();

        // Calculate pricing
        var hourlyRate = CalculateHourlyRate(request.Spec);

        var vm = new VirtualMachine
        {
            VmId = Guid.NewGuid().ToString(),
            Name = request.Name,
            OwnerId = userId,
            OwnerWallet = user?.WalletAddress ?? string.Empty,
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
                Hostname = SanitizeHostname(request.Name)
            }
        };

        // Save to DataStore with persistence
        await _dataStore.SaveVmAsync(vm);

        // Update user quotas
        if (user != null)
        {
            user.Quotas.CurrentVms++;
            user.Quotas.CurrentVirtualCpuCores += request.Spec.VirtualCpuCores;
            user.Quotas.CurrentMemoryBytes += request.Spec.MemoryBytes;
            user.Quotas.CurrentStorageBytes += request.Spec.DiskBytes;
            await _dataStore.SaveUserAsync(user);
        }

        _logger.LogInformation("VM queued for scheduling: {VmId} ({Name}) for user {UserId}",
            vm.VmId, vm.Name, userId);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmCreated,
            ResourceType = "vm",
            ResourceId = vm.VmId,
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
        await TryScheduleVmAsync(vm, password);

        return new CreateVmResponse(
            vm.VmId,
            vm.Status,
            "VM created and queued for scheduling",
            Error: null,
            Password: password);
    }

    public async Task<bool> SecurePasswordAsync(string vmId, string userId, string encryptedPassword)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
            return false;

        if (vm.OwnerId != userId)
            return false;

        vm.Spec.WalletEncryptedPassword = encryptedPassword;

        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation("Password secured for VM {VmId}", vmId);
        return true;
    }

    public Task<VirtualMachine?> GetVmAsync(string vmId)
    {
        _dataStore.VirtualMachines.TryGetValue(vmId, out var vm);
        return Task.FromResult(vm);
    }

    public Task<List<VirtualMachine>> GetVmsByUserAsync(string userId, VmStatus? statusFilter = null)
    {
        var vms = _dataStore.VirtualMachines.Values
            .Where(v => v.OwnerId == userId)
            .Where(v => !statusFilter.HasValue || v.Status == statusFilter.Value)
            .OrderByDescending(v => v.CreatedAt)
            .ToList();

        return Task.FromResult(vms);
    }

    public Task<List<VirtualMachine>> GetVmsByNodeAsync(string nodeId)
    {
        var vms = _dataStore.VirtualMachines.Values
            .Where(v => v.NodeId == nodeId)
            .Where(v => v.Status != VmStatus.Deleted)
            .ToList();

        return Task.FromResult(vms);
    }

    public Task<PagedResult<VmSummary>> ListVmsAsync(string? userId, ListQueryParams queryParams)
    {
        var query = _dataStore.VirtualMachines.Values.AsEnumerable();

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
                v.VmId.ToLower().Contains(search));
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

        var items = vmsWithPagination.Select(v =>
        {
            // Clone the existing network config to avoid modifying the original
            var enrichedNetworkConfig = new VmNetworkConfig
            {
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
                if (_dataStore.Nodes.TryGetValue(v.NodeId, out var node))
                {
                    // ============================================================
                    // SECURITY: Only expose what's needed for connection
                    // ============================================================
                    enrichedNetworkConfig.SshJumpHost = node.PublicIp;
                    enrichedNetworkConfig.SshJumpPort = 2222; // Standard SSH port
                    enrichedNetworkConfig.NodeAgentHost = node.PublicIp;
                    enrichedNetworkConfig.NodeAgentPort = node.AgentPort > 0 ? node.AgentPort : 5100;

                    _logger.LogDebug(
                        "VM {VmId} on node {NodeId}: SSH={SshHost}:{SshPort}, Agent={AgentHost}:{AgentPort}",
                        v.VmId, v.NodeId,
                        enrichedNetworkConfig.SshJumpHost, enrichedNetworkConfig.SshJumpPort,
                        enrichedNetworkConfig.NodeAgentHost, enrichedNetworkConfig.NodeAgentPort);
                }
                else
                {
                    _logger.LogWarning(
                        "VM {VmId} references non-existent node {NodeId} - connection details unavailable",
                        v.VmId, v.NodeId);
                }
            }

            return new VmSummary(
                Id: v.VmId,
                Name: v.Name,
                Status: v.Status,
                PowerState: v.PowerState,
                NodeId: v.NodeId,
                NodePublicIp: enrichedNetworkConfig.SshJumpHost,
                NodeAgentPort: enrichedNetworkConfig.NodeAgentPort,
                Spec: v.Spec,
                NetworkConfig: enrichedNetworkConfig,  // <-- Enhanced with node connection details
                CreatedAt: v.CreatedAt,
                UpdatedAt: v.UpdatedAt
            );
        }).ToList();

        return Task.FromResult(new PagedResult<VmSummary>
        {
            Items = items,
            TotalCount = totalCount,
            Page = queryParams.Page,
            PageSize = queryParams.PageSize
        });
    }

    public async Task<bool> PerformVmActionAsync(string vmId, VmAction action, string? userId = null)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
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

        _dataStore.AddPendingCommand(vm.NodeId, command);

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
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
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
            _dataStore.AddPendingCommand(vm.NodeId, command);

            _logger.LogInformation(
                "DeleteVm command {CommandId} queued for VM {VmId} on node {NodeId}",
                command.CommandId, vmId, vm.NodeId);

            // Update status message (keep command ID for legacy fallback)
            vm.StatusMessage = $"Deletion command {command.CommandId} sent to node";
            await _dataStore.SaveVmAsync(vm);
        }
        else
        {
            // No node assigned - complete deletion immediately
            _logger.LogInformation(
                "VM {VmId} has no node assignment - completing deletion immediately",
                vmId);

            await CompleteVmDeletionAsync(vmId);
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

    /// <summary>
    /// Complete VM deletion after node confirmation
    /// Called by NodeService after receiving acknowledgment
    /// </summary>
    private async Task CompleteVmDeletionAsync(string vmId)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
        {
            _logger.LogWarning("VM {VmId} not found for deletion", vmId);
            return;
        }

        _logger.LogInformation(
            "Completing deletion for VM {VmId} (Owner: {Owner}, Node: {Node})",
            vm.VmId, vm.OwnerId, vm.NodeId ?? "none");

        // Mark as Deleted (final state before removal)
        vm.Status = VmStatus.Deleted;
        vm.StatusMessage = "Deletion confirmed by node";
        vm.StoppedAt = DateTime.UtcNow;
        vm.UpdatedAt = DateTime.UtcNow;
        await _dataStore.SaveVmAsync(vm);

        // ========================================
        // FREE RESOURCES INCLUDING POINTS
        // ========================================

        // Free reserved resources from node
        if (!string.IsNullOrEmpty(vm.NodeId) &&
            _dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            var cpuToFree = vm.Spec.VirtualCpuCores;
            var memToFree = vm.Spec.MemoryBytes;
            var storageToFree = vm.Spec.DiskBytes;
            var pointsToFree = vm.Spec.ComputePointCost;

            // Free resources
            node.ReservedResources.ComputePoints = Math.Max(0,
                node.ReservedResources.ComputePoints - pointsToFree);
            node.ReservedResources.MemoryBytes = Math.Max(0,
                node.ReservedResources.MemoryBytes - memToFree);
            node.ReservedResources.StorageBytes = Math.Max(0,
                node.ReservedResources.StorageBytes - storageToFree);

            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "Released reserved resources for VM {VmId} on node {NodeId}: " +
                "{CpuCores}c, {MemoryMb}MB, {StorageGb}GB, {Points} points. " +
                "Node utilization: {PointsUsed}/{PointsTotal} points ({Percent:F1}%)",
                vmId, node.Id, cpuToFree, memToFree, storageToFree, pointsToFree,
                node.ReservedResources.ComputePoints,
                node.TotalResources.ComputePoints,
                (double)node.ReservedResources.ComputePoints /
                Math.Max(1, node.TotalResources.ComputePoints) * 100);
        }

        // Update user quotas (unchanged)
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
                "Updated quotas for user {UserId}: VMs={VMs}, CPU={CPU}c, MEM={MEM}MB",
                user.Id, user.Quotas.CurrentVms, user.Quotas.CurrentVirtualCpuCores,
                user.Quotas.CurrentMemoryBytes);
        }

        await _ingressService.OnVmDeletedAsync(vm.VmId);

        // Emit completion event (unchanged)
        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmDeleted,
            ResourceType = "vm",
            ResourceId = vmId,
            NodeId = vm.NodeId,
            UserId = vm.OwnerId
        });

        _logger.LogInformation("VM {VmId} deletion completed successfully", vmId);
    }

    public async Task<bool> UpdateVmStatusAsync(string vmId, VmStatus status, string? message = null)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
            return false;

        var oldStatus = vm.Status;
        vm.Status = status;
        vm.StatusMessage = message;

        if (status == VmStatus.Running && oldStatus != VmStatus.Running)
        {
            vm.StartedAt = DateTime.UtcNow;
            vm.PowerState = VmPowerState.Running;
        }
        else if (status == VmStatus.Stopped)
        {
            vm.StoppedAt = DateTime.UtcNow;
            vm.PowerState = VmPowerState.Off;
        }

        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation("VM {VmId} status changed: {OldStatus} -> {NewStatus}",
            vmId, oldStatus, status);

        return true;
    }

    public async Task<bool> UpdateVmMetricsAsync(string vmId, VmMetrics metrics)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
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

    private async Task TryScheduleVmAsync(VirtualMachine vm, string password)
    {
        // ========================================
        // STEP 1: Calculate compute point cost FIRST
        // ========================================
        var policy = _schedulingConfig.TierPolicies[vm.Spec.QualityTier];
        var pointCost = vm.Spec.VirtualCpuCores * policy.PointsPerVCpu;

        // CRITICAL: Store point cost in VM spec before scheduling
        vm.Spec.ComputePointCost = pointCost;

        _logger.LogInformation(
            "VM {VmId} ({Name}): Tier={Tier}, vCPUs={VCpus}, ComputePointCost={Points}",
            vm.VmId, vm.Name, vm.Spec.QualityTier, vm.Spec.VirtualCpuCores, pointCost);

        // ========================================
        // STEP 2: Select node (now with correct point cost)
        // ========================================
        var selectedNode = await _nodeService.SelectBestNodeForVmAsync(
            vm.Spec,
            vm.Spec.QualityTier);

        if (selectedNode == null)
        {
            _logger.LogWarning(
                "No suitable node found for VM {VmId} - Tier: {Tier}, Points: {Points}",
                vm.VmId, vm.Spec.QualityTier, pointCost);

            vm.Status = VmStatus.Pending;
            vm.StatusMessage = "No suitable node available";
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
            vm.VmId, selectedNode.Id, vm.Spec.VirtualCpuCores, vm.Spec.MemoryBytes, vm.Spec.DiskBytes, pointCost, vm.Spec.QualityTier,
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

        // ========================================
        // STEP 5: Get SSH public key
        // ========================================
        string? sshPublicKey = vm.Spec.SshPublicKey;
        if (string.IsNullOrEmpty(sshPublicKey) && _dataStore.Users.TryGetValue(vm.OwnerId, out var owner))
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
        // STEP 7: Create command with ALL required fields
        // ========================================

        var command = new NodeCommand(
            Guid.NewGuid().ToString(),
            NodeCommandType.CreateVm,
            JsonSerializer.Serialize(new
            {
                VmId = vm.VmId,
                Name = vm.Name,
                OwnerId = vm.OwnerId,
                OwnerWallet = vm.OwnerWallet,
                VirtualCpuCores = vm.Spec.VirtualCpuCores,
                MemoryBytes = vm.Spec.MemoryBytes,
                DiskBytes = vm.Spec.DiskBytes,
                QualityTier = (int)vm.Spec.QualityTier,
                ComputePointCost = vm.Spec.ComputePointCost,
                BaseImageUrl = imageUrl,
                SshPublicKey = sshPublicKey ?? "",
                LeaseId = vm.VmId,
                Network = new
                {
                    MacAddress = "",
                    IpAddress = vm.NetworkConfig.PrivateIp,
                    Gateway = "",
                    VxlanVni = 0,
                    AllowedPorts = new List<int>()
                },
                Password = password
            }),
            RequiresAck: true,
            TargetResourceId: vm.VmId
        );

        _dataStore.AddPendingCommand(selectedNode.Id, command);
        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "VM {VmId} scheduled on node {NodeId} with command containing ComputePointCost={Points}",
            vm.VmId, selectedNode.Id, vm.Spec.ComputePointCost);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmScheduled,
            ResourceType = "vm",
            ResourceId = vm.VmId,
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

    private static decimal CalculateHourlyRate(VmSpec spec)
    {
        var baseCpuRate = 0.01m;
        var baseMemoryRate = 0.005m * 1024m * 1024m * 1024m;  // per GB
        var baseStorageRate = 0.0001m * 1024m * 1024m * 1024m; // per GB

        return (spec.VirtualCpuCores * baseCpuRate) +
               (spec.MemoryBytes * baseMemoryRate) +
               (spec.DiskBytes * baseStorageRate);
    }

    private static string GeneratePrivateIp()
    {
        var random = RandomNumberGenerator.GetInt32(2, 254);
        return $"10.100.0.{random}";
    }

    private static string SanitizeHostname(string name)
    {
        return new string(name.ToLower()
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Take(63)
            .ToArray());
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
            _ => null
        };
    }
}