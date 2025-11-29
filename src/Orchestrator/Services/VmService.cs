using System.Security.Cryptography;
using System.Text.Json;
using Orchestrator.Data;
using Orchestrator.Models;

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
    Task SchedulePendingVmsAsync();
}

public class VmService : IVmService
{
    private readonly OrchestratorDataStore _dataStore;
    private readonly INodeService _nodeService;
    private readonly IEventService _eventService;
    private readonly ILogger<VmService> _logger;

    public VmService(
        OrchestratorDataStore dataStore,
        INodeService nodeService,
        IEventService eventService,
        ILogger<VmService> logger)
    {
        _dataStore = dataStore;
        _nodeService = nodeService;
        _eventService = eventService;
        _logger = logger;
    }

    public async Task<CreateVmResponse> CreateVmAsync(string userId, CreateVmRequest request)
    {
        // In VM creation logic
        string? generatedPassword = null;
        string? sshPublicKey = null;

        // Get user's SSH keys
        var userSshKey = request.Spec.SshPublicKey;

        if (string.IsNullOrWhiteSpace(userSshKey))
        {
            // Generate secure random password
            generatedPassword = GenerateSecurePassword(16);
            request.Spec.Password = generatedPassword;  // Or store encrypted
            request.Spec.PasswordShownToUser = false;
        }
        else
        {
            request.Spec.SshPublicKey = sshPublicKey;
        }

        // Validate image exists
        if (!_dataStore.Images.ContainsKey(request.Spec.ImageId) && string.IsNullOrEmpty(request.Spec.ImageUrl))
        {
            return new CreateVmResponse(string.Empty, VmStatus.Error, $"Unknown image: {request.Spec.ImageId}");
        }

        // Get user and check quotas
        if (_dataStore.Users.TryGetValue(userId, out var user))
        {
            if (user.Quotas.CurrentVms >= user.Quotas.MaxVms)
            {
                return new CreateVmResponse(string.Empty, VmStatus.Error, "VM quota exceeded");
            }
            if (user.Quotas.CurrentCpuCores + request.Spec.CpuCores > user.Quotas.MaxCpuCores)
            {
                return new CreateVmResponse(string.Empty, VmStatus.Error, "CPU quota exceeded");
            }
        }

        // Determine pricing
        var hourlyRate = CalculateHourlyRate(request.Spec);

        var vm = new VirtualMachine
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            OwnerId = userId,
            OwnerWallet = user?.WalletAddress ?? string.Empty,
            Spec = request.Spec,
            Status = VmStatus.Pending,
            Labels = request.Labels ?? new(),
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

        _dataStore.VirtualMachines.TryAdd(vm.Id, vm);

        // Update user quotas
        if (user != null)
        {
            user.Quotas.CurrentVms++;
            user.Quotas.CurrentCpuCores += request.Spec.CpuCores;
            user.Quotas.CurrentMemoryMb += request.Spec.MemoryMb;
            user.Quotas.CurrentStorageGb += request.Spec.DiskGb;
        }

        _logger.LogInformation("VM created: {VmId} ({Name}) for user {UserId}", vm.Id, vm.Name, userId);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmCreated,
            ResourceType = "vm",
            ResourceId = vm.Id,
            UserId = userId,
            Payload = new { vm.Name, vm.Spec }
        });

        // Immediately try to schedule
        await TryScheduleVmAsync(vm);

        return new CreateVmResponse(vm.Id, vm.Status, "VM created and queued for scheduling");
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

        // Filter by user if specified
        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(v => v.OwnerId == userId);
        }

        // Filter by status
        if (queryParams.Filters?.TryGetValue("status", out var status) == true)
        {
            if (Enum.TryParse<VmStatus>(status, true, out var statusEnum))
            {
                query = query.Where(v => v.Status == statusEnum);
            }
        }

        // Filter by search term
        if (!string.IsNullOrEmpty(queryParams.Search))
        {
            var search = queryParams.Search.ToLower();
            query = query.Where(v =>
                v.Name.ToLower().Contains(search) ||
                v.Id.ToLower().Contains(search));
        }

        // Exclude deleted
        query = query.Where(v => v.Status != VmStatus.Deleted);

        var totalCount = query.Count();

        // Sort
        query = queryParams.SortBy?.ToLower() switch
        {
            "name" => queryParams.SortDescending ? query.OrderByDescending(v => v.Name) : query.OrderBy(v => v.Name),
            "status" => queryParams.SortDescending ? query.OrderByDescending(v => v.Status) : query.OrderBy(v => v.Status),
            _ => query.OrderByDescending(v => v.CreatedAt)
        };

        // Paginate
        var items = query
            .Skip((queryParams.Page - 1) * queryParams.PageSize)
            .Take(queryParams.PageSize)
            .Select(v => new VmSummary(
                v.Id,
                v.Name,
                v.Status,
                v.PowerState,
                v.NodeId,
                v.Spec,
                v.NetworkConfig,
                v.CreatedAt,
                v.UpdatedAt
            ))
            .ToList();

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

        // Check ownership if userId provided
        if (userId != null && vm.OwnerId != userId)
            return false;

        var commandType = action switch
        {
            VmAction.Start => NodeCommandType.StartVm,
            VmAction.Stop => NodeCommandType.StopVm,
            VmAction.Restart => NodeCommandType.StopVm, // Will need follow-up start
            VmAction.ForceStop => NodeCommandType.StopVm,
            _ => (NodeCommandType?)null
        };

        if (commandType == null || string.IsNullOrEmpty(vm.NodeId))
        {
            _logger.LogWarning("Cannot perform action {Action} on VM {VmId}", action, vmId);
            return false;
        }

        // Queue command to node
        var command = new NodeCommand(
            Guid.NewGuid().ToString(),
            commandType.Value,
            JsonSerializer.Serialize(new { VmId = vmId, Action = action.ToString() })
        );

        _dataStore.AddPendingCommand(vm.NodeId, command);

        // Update status based on action
        vm.Status = action switch
        {
            VmAction.Start => VmStatus.Provisioning,
            VmAction.Stop or VmAction.ForceStop => VmStatus.Stopping,
            _ => vm.Status
        };
        vm.UpdatedAt = DateTime.UtcNow;

        _logger.LogInformation("VM action {Action} queued for {VmId}", action, vmId);

        return true;
    }

    public async Task<bool> DeleteVmAsync(string vmId, string? userId = null)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
            return false;

        if (userId != null && vm.OwnerId != userId)
            return false;

        // If running on a node, queue deletion command
        if (!string.IsNullOrEmpty(vm.NodeId))
        {
            var command = new NodeCommand(
                Guid.NewGuid().ToString(),
                NodeCommandType.DeleteVm,
                JsonSerializer.Serialize(new { VmId = vmId })
            );
            _dataStore.AddPendingCommand(vm.NodeId, command);

            // Release resources on node
            if (_dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
            {
                node.AvailableResources.CpuCores += vm.Spec.CpuCores;
                node.AvailableResources.MemoryMb += vm.Spec.MemoryMb;
                node.AvailableResources.StorageGb += vm.Spec.DiskGb;
            }
        }

        // Update user quotas
        if (_dataStore.Users.TryGetValue(vm.OwnerId, out var user))
        {
            user.Quotas.CurrentVms--;
            user.Quotas.CurrentCpuCores -= vm.Spec.CpuCores;
            user.Quotas.CurrentMemoryMb -= vm.Spec.MemoryMb;
            user.Quotas.CurrentStorageGb -= vm.Spec.DiskGb;
        }

        vm.Status = VmStatus.Deleted;
        vm.UpdatedAt = DateTime.UtcNow;

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmDeleted,
            ResourceType = "vm",
            ResourceId = vmId,
            UserId = vm.OwnerId
        });

        _logger.LogInformation("VM deleted: {VmId}", vmId);

        return true;
    }

    public Task<bool> UpdateVmStatusAsync(string vmId, VmStatus status, string? message = null)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
            return Task.FromResult(false);

        var oldStatus = vm.Status;
        vm.Status = status;
        vm.StatusMessage = message;
        vm.UpdatedAt = DateTime.UtcNow;

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

        _logger.LogInformation("VM {VmId} status changed: {OldStatus} -> {NewStatus}", vmId, oldStatus, status);

        return Task.FromResult(true);
    }

    public Task<bool> UpdateVmMetricsAsync(string vmId, VmMetrics metrics)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
            return Task.FromResult(false);

        vm.LatestMetrics = metrics;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Background task to schedule pending VMs
    /// </summary>
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

    private async Task TryScheduleVmAsync(VirtualMachine vm)
    {
        vm.Status = VmStatus.Scheduling;
        vm.UpdatedAt = DateTime.UtcNow;

        // Find available nodes
        var availableNodes = await _nodeService.GetAvailableNodesForVmAsync(vm.Spec);

        if (!availableNodes.Any())
        {
            _logger.LogWarning("No available nodes for VM {VmId}", vm.Id);
            vm.Status = VmStatus.Pending;
            vm.StatusMessage = "Waiting for available resources";
            return;
        }

        // Pick the best node (first in sorted list)
        var selectedNode = availableNodes.First();

        // Reserve resources
        selectedNode.AvailableResources.CpuCores -= vm.Spec.CpuCores;
        selectedNode.AvailableResources.MemoryMb -= vm.Spec.MemoryMb;
        selectedNode.AvailableResources.StorageGb -= vm.Spec.DiskGb;
        selectedNode.ReservedResources.CpuCores += vm.Spec.CpuCores;
        selectedNode.ReservedResources.MemoryMb += vm.Spec.MemoryMb;
        selectedNode.ReservedResources.StorageGb += vm.Spec.DiskGb;

        // Assign VM to node
        vm.NodeId = selectedNode.Id;
        vm.Status = VmStatus.Provisioning;
        vm.NetworkConfig.PrivateIp = GeneratePrivateIp();

        // Get user's SSH key if available
        string? sshPublicKey = vm.Spec.SshPublicKey; // First check if provided in spec
        if (string.IsNullOrEmpty(sshPublicKey) && _dataStore.Users.TryGetValue(vm.OwnerId, out var owner))
        {
            // Use user's first SSH key if they have one
            sshPublicKey = owner.SshKeys.FirstOrDefault()?.PublicKey;
        }

        // Resolve image URL from imageId
        string? imageUrl = vm.Spec.ImageUrl;
        if (string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(vm.Spec.ImageId))
        {
            imageUrl = GetImageUrl(vm.Spec.ImageId);
        }

        // Queue creation command with full details for Node Agent
        var command = new NodeCommand(
            Guid.NewGuid().ToString(),
            NodeCommandType.CreateVm,
            JsonSerializer.Serialize(new
            {
                VmId = vm.Id,
                Name = vm.Name,
                VCpus = vm.Spec.CpuCores,
                MemoryBytes = vm.Spec.MemoryMb * 1024L * 1024L,
                DiskBytes = vm.Spec.DiskGb * 1024L * 1024L * 1024L,
                BaseImageUrl = imageUrl,
                BaseImageHash = "",
                SshPublicKey = sshPublicKey ?? "",
                TenantId = vm.OwnerId,
                LeaseId = vm.Id,
                Network = new
                {
                    MacAddress = "",
                    IpAddress = vm.NetworkConfig.PrivateIp,
                    Gateway = "",
                    VxlanVni = 0,
                    AllowedPorts = new List<int>()
                },
                Password = vm.Spec.Password ?? ""
            })
        );

        _dataStore.AddPendingCommand(selectedNode.Id, command);

        _logger.LogInformation("VM {VmId} scheduled on node {NodeId} with image {ImageUrl}",
            vm.Id, selectedNode.Id, imageUrl);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmScheduled,
            ResourceType = "vm",
            ResourceId = vm.Id,
            UserId = vm.OwnerId,
            Payload = new { NodeId = selectedNode.Id }
        });
    }

    private static string GenerateSecurePassword(int length)
    {
        const string chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%";
        var random = RandomNumberGenerator.Create();
        var bytes = new byte[length];
        random.GetBytes(bytes);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    private static string? GetImageUrl(string imageId)
    {
        // Map image IDs to cloud image URLs
        return imageId.ToLower() switch
        {
            "ubuntu-24.04" => "https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img",
            "ubuntu-22.04" => "https://cloud-images.ubuntu.com/jammy/current/jammy-server-cloudimg-amd64.img",
            "ubuntu-20.04" => "https://cloud-images.ubuntu.com/focal/current/focal-server-cloudimg-amd64.img",
            "debian-12" => "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
            "debian-11" => "https://cloud.debian.org/images/cloud/bullseye/latest/debian-11-generic-amd64.qcow2",
            "fedora-40" => "https://download.fedoraproject.org/pub/fedora/linux/releases/40/Cloud/x86_64/images/Fedora-Cloud-Base-Generic.x86_64-40-1.14.qcow2",
            "alpine-3.19" => "https://dl-cdn.alpinelinux.org/alpine/v3.19/releases/cloud/nocloud_alpine-3.19.1-x86_64-bios-cloudinit-r0.qcow2",
            _ => null
        };
    }

    private decimal CalculateHourlyRate(VmSpec spec)
    {
        // Simple pricing model: base rate per resource
        decimal cpuRate = 0.005m * spec.CpuCores;
        decimal memoryRate = 0.002m * (spec.MemoryMb / 1024m);
        decimal storageRate = 0.0001m * spec.DiskGb;
        decimal gpuRate = spec.RequiresGpu ? 0.10m : 0;

        return cpuRate + memoryRate + storageRate + gpuRate;
    }

    private static string SanitizeHostname(string name)
    {
        return new string(name.ToLower()
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Take(63)
            .ToArray())
            .Trim('-');
    }

    private static string GeneratePrivateIp()
    {
        var random = new Random();
        return $"10.0.{random.Next(0, 256)}.{random.Next(1, 255)}";
    }
}