using DeCloud.Shared.Models;
using Orchestrator.Persistence;
using Orchestrator.Exceptions;
using Orchestrator.Models;
using Org.BouncyCastle.Asn1.Ocsp;
using System.Security.Cryptography;
using System.Text.Json;
using Orchestrator.Services.VmScheduling;
using Orchestrator.Services;
using Orchestrator.Models.Payment;

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
    private readonly INodeService _nodeService;
    private readonly IVmSchedulingService _schedulingService;
    private readonly ISchedulingConfigService _configService;
    private readonly IEventService _eventService;
    private readonly ICentralIngressService _ingressService;
    private readonly INetworkLatencyTracker _latencyTracker;
    private readonly ILogger<VmService> _logger;

    public VmService(
        DataStore dataStore,
        INodeService nodeService,
        IVmSchedulingService schedulingService,
        ISchedulingConfigService configService,
        IEventService eventService,
        ICentralIngressService ingressService,
        INetworkLatencyTracker latencyTracker,
        ILogger<VmService> logger)
    {
        _dataStore = dataStore;
        _nodeService = nodeService;
        _schedulingService = schedulingService;
        _configService = configService;
        _eventService = eventService;
        _ingressService = ingressService;
        _latencyTracker = latencyTracker;
        _logger = logger;
    }

    public async Task<CreateVmResponse> CreateVmAsync(string userId, CreateVmRequest request, string? targetNodeId = null)
    {
        var isRelayVm = request.Spec.VmType == VmType.Relay;

        // Validate user exists
        User? user = null;
        if (_dataStore.Users.TryGetValue(userId, out var existingUser))
        {
            user = existingUser;
        }

        // Check quotas
        if (user != null && !isRelayVm)
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
        var password = isRelayVm ? null : GenerateMemorablePassword();

        // Calculate pricing
        var hourlyRate = CalculateHourlyRate(request.Spec);

        var vm = new VirtualMachine
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            VmType = request.VmType,
            OwnerId = isRelayVm ? null : userId,
            OwnerWallet = isRelayVm ? null : user?.WalletAddress,
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
            },
            NetworkMetrics = VmNetworkMetrics.CreateDefault(),
        };

        // Save to DataStore with persistence
        await _dataStore.SaveVmAsync(vm);

        // Update user quotas
        if (!isRelayVm && user != null)
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

                _logger.LogWarning(
                        "VM {VmId} references non-existent node {NodeId} - connection details unavailable",
                        v.Id, v.NodeId);
            }

            return new VmSummary(
                Id: v.Id,
                Name: v.Name,
                Status: v.Status,
                PowerState: v.PowerState,
                NodeId: v.NodeId,
                NodePublicIp: networkConfig.SshJumpHost,
                NodeAgentPort: networkConfig.NodeAgentPort,
                Spec: v.Spec,
                NetworkConfig: networkConfig,
                CreatedAt: v.CreatedAt,
                UpdatedAt: v.UpdatedAt
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
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning("VM {VmId} not found for deletion", vmId);
            return;
        }

        _logger.LogInformation(
            "Completing deletion for VM {VmId} (Owner: {Owner}, Node: {Node})",
            vm.Id, vm.OwnerId, vm.NodeId ?? "none");

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
        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        if (!string.IsNullOrEmpty(vm.NodeId) &&
            vm != null)
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

        await _ingressService.OnVmDeletedAsync(vm.Id);

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
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
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
        var pointCost = vm.Spec.VmType == VmType.Relay ? vm.Spec.ComputePointCost : vm.Spec.VirtualCpuCores *
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
            // Use specified node (for relay VMs)
            selectedNode = await _dataStore.GetNodeAsync(targetNodeId);
            _logger.LogInformation(
                "Using target node {NodeId} for VM {VmId} (relay deployment)",
                targetNodeId, vm.Id);
        }
        else
        {
            // Normal scheduling for user VMs
            selectedNode = await _schedulingService.SelectBestNodeForVmAsync(
                vm.Spec,
                vm.Spec.QualityTier);
        }

        if (selectedNode == null)
        {
            _logger.LogWarning(
                "No suitable node found for VM {VmId} - Tier: {Tier}, Points: {Points}",
                vm.Id, vm.Spec.QualityTier, pointCost);

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
            vm.Id, selectedNode.Id, vm.Spec.VirtualCpuCores, vm.Spec.MemoryBytes, vm.Spec.DiskBytes, pointCost, vm.Spec.QualityTier,
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
        // STEP 7: Create command with ALL required fields
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
                Network = new
                {
                    MacAddress = "",
                    IpAddress = vm.NetworkConfig.PrivateIp,
                    Gateway = "",
                    VxlanVni = 0,
                    AllowedPorts = new List<int>()
                },
                Password = password,
                Labels = vm.Labels
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
            NodeCommandType.DeleteVm
        );

        _dataStore.AddPendingCommand(selectedNode.Id, command);
        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "Relay VM {VmId} scheduled on node {NodeId}", vm.Id, selectedNode.Id);

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

    private static decimal CalculateHourlyRate(VmSpec spec)
    {
        var baseCpuRate = 0m;
        var baseMemoryRate = 0m;  // per GB
        var baseStorageRate = 0m; // per GB

        const decimal BYTES_PER_GB = 1024m * 1024m * 1024m;

        switch (spec.VmType)
        {
            case VmType.General:
                baseCpuRate = 0.01m;
                baseMemoryRate = 0.005m;  // per GB
                baseStorageRate = 0.0001m; // per GB
                break;
            case VmType.Compute:
                break;
            case VmType.Memory:
                break;
            case VmType.Storage:
                break;
            case VmType.Gpu:
                break;
            case VmType.Relay:
                return 0.005m; // Flat rate for relay VMs
            default:
                baseCpuRate = 0.01m;
                baseMemoryRate = 0.005m * 1024m * 1024m * 1024m;  // per GB
                baseStorageRate = 0.0001m * 1024m * 1024m * 1024m; // per GB
                break;
        }

        return 
            (spec.VirtualCpuCores * baseCpuRate) +
            ((spec.MemoryBytes / BYTES_PER_GB) * baseMemoryRate) +  // Divide by bytes per GB
            ((spec.DiskBytes / BYTES_PER_GB) * baseStorageRate);
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