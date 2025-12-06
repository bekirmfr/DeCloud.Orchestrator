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
    Task<bool> SecurePasswordAsync(string vmId, string userId, string encryptedPassword);
}

public class VmService : IVmService
{
    private readonly DataStore _dataStore;
    private readonly INodeService _nodeService;
    private readonly IEventService _eventService;
    private readonly ILogger<VmService> _logger;

    public VmService(
        DataStore dataStore,
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

            if (user.Quotas.CurrentCpuCores + request.Spec.CpuCores > user.Quotas.MaxCpuCores)
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

        // Set password in spec (will be cleared after encryption)
        vm.Spec.Password = password;

        // Save to DataStore with persistence
        await _dataStore.SaveVmAsync(vm);

        // Update user quotas
        if (user != null)
        {
            user.Quotas.CurrentVms++;
            user.Quotas.CurrentCpuCores += request.Spec.CpuCores;
            user.Quotas.CurrentMemoryMb += request.Spec.MemoryMb;
            user.Quotas.CurrentStorageGb += request.Spec.DiskGb;
            await _dataStore.SaveUserAsync(user);
        }

        _logger.LogInformation("VM created: {VmId} ({Name}) for user {UserId}",
            vm.Id, vm.Name, userId);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmCreated,
            ResourceType = "vm",
            ResourceId = vm.Id,
            UserId = userId,
            Payload = new Dictionary<string, object>
            {
                ["name"] = vm.Name,
                ["cpuCores"] = vm.Spec.CpuCores,
                ["memoryMb"] = vm.Spec.MemoryMb,
                ["diskGb"] = vm.Spec.DiskGb,
                ["imageId"] = vm.Spec.ImageId ?? ""
            }
        });

        // Immediately try to schedule
        await TryScheduleVmAsync(vm);

        return new CreateVmResponse(
            vm.Id,
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

        vm.Spec.EncryptedPassword = encryptedPassword;
        vm.Spec.Password = null;
        vm.Spec.PasswordSecured = true;

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

        _dataStore.AddPendingCommand(vm.NodeId, command);

        vm.Status = action switch
        {
            VmAction.Start => VmStatus.Provisioning,
            VmAction.Stop or VmAction.ForceStop => VmStatus.Stopping,
            _ => vm.Status
        };

        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation("VM action {Action} queued for {VmId}", action, vmId);

        return true;
    }

    public async Task<bool> DeleteVmAsync(string vmId, string? userId = null)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
            return false;

        if (userId != null && vm.OwnerId != userId)
            return false;

        if (!string.IsNullOrEmpty(vm.NodeId))
        {
            var command = new NodeCommand(
                Guid.NewGuid().ToString(),
                NodeCommandType.DeleteVm,
                JsonSerializer.Serialize(new { VmId = vmId })
            );

            _dataStore.AddPendingCommand(vm.NodeId, command);
        }

        // Mark as deleted (soft delete with persistence)
        await _dataStore.DeleteVmAsync(vmId);
        await _dataStore.SaveVmAsync(vm);

        // Release reserved resources from node
        if (!string.IsNullOrEmpty(vm.NodeId) &&
            _dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            node.ReservedResources.CpuCores = Math.Max(0,
                node.ReservedResources.CpuCores - vm.Spec.CpuCores);
            node.ReservedResources.MemoryMb = Math.Max(0,
                node.ReservedResources.MemoryMb - vm.Spec.MemoryMb);
            node.ReservedResources.StorageGb = Math.Max(0,
                node.ReservedResources.StorageGb - vm.Spec.DiskGb);

            await _dataStore.SaveNodeAsync(node);
            _logger.LogInformation(
                "Released resources for deleted VM {VmId} on node {NodeId}: " +
                "{CpuCores}c, {MemoryMb}MB, {StorageGb}GB",
                vmId, node.Id, vm.Spec.CpuCores, vm.Spec.MemoryMb, vm.Spec.DiskGb);
        }

        // Update user quotas
        if (_dataStore.Users.TryGetValue(vm.OwnerId, out var user))
        {
            user.Quotas.CurrentVms = Math.Max(0, user.Quotas.CurrentVms - 1);
            user.Quotas.CurrentCpuCores = Math.Max(0, user.Quotas.CurrentCpuCores - vm.Spec.CpuCores);
            user.Quotas.CurrentMemoryMb = Math.Max(0, user.Quotas.CurrentMemoryMb - vm.Spec.MemoryMb);
            user.Quotas.CurrentStorageGb = Math.Max(0, user.Quotas.CurrentStorageGb - vm.Spec.DiskGb);
            await _dataStore.SaveUserAsync(user);
        }

        _logger.LogInformation("VM {VmId} deleted by user {UserId}", vmId, userId ?? "system");

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmDeleted,
            ResourceType = "vm",
            ResourceId = vmId,
            UserId = userId
        });

        return true;
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
        await _dataStore.SaveVmAsync(vm);

        var availableNodes = await _nodeService.GetAvailableNodesForVmAsync(vm.Spec);

        if (!availableNodes.Any())
        {
            _logger.LogWarning("No available nodes for VM {VmId}", vm.Id);
            vm.Status = VmStatus.Pending;
            vm.StatusMessage = "Waiting for available resources";
            await _dataStore.SaveVmAsync(vm);
            return;
        }

        var selectedNode = availableNodes.First();

        // Reserve resources
        selectedNode.AvailableResources.CpuCores -= vm.Spec.CpuCores;
        selectedNode.AvailableResources.MemoryMb -= vm.Spec.MemoryMb;
        selectedNode.AvailableResources.StorageGb -= vm.Spec.DiskGb;
        selectedNode.ReservedResources.CpuCores += vm.Spec.CpuCores;
        selectedNode.ReservedResources.MemoryMb += vm.Spec.MemoryMb;
        selectedNode.ReservedResources.StorageGb += vm.Spec.DiskGb;

        await _dataStore.SaveNodeAsync(selectedNode);

        vm.NodeId = selectedNode.Id;
        vm.Status = VmStatus.Provisioning;
        vm.NetworkConfig.PrivateIp = GeneratePrivateIp();

        await _dataStore.SaveVmAsync(vm);

        string? sshPublicKey = vm.Spec.SshPublicKey;
        if (string.IsNullOrEmpty(sshPublicKey) && _dataStore.Users.TryGetValue(vm.OwnerId, out var owner))
        {
            if (owner.SshKeys.Any())
            {
                sshPublicKey = string.Join("\n", owner.SshKeys.Select(k => k.PublicKey));
            }
        }

        string? imageUrl = vm.Spec.ImageUrl;
        if (string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(vm.Spec.ImageId))
        {
            imageUrl = GetImageUrl(vm.Spec.ImageId);
        }

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
        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation("VM {VmId} scheduled on node {NodeId}", vm.Id, selectedNode.Id);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmScheduled,
            ResourceType = "vm",
            ResourceId = vm.Id,
            UserId = vm.OwnerId,
            NodeId = selectedNode.Id,
            Payload = new Dictionary<string, object>
            {
                ["nodeId"] = selectedNode.Id,
                ["nodeName"] = selectedNode.Name
            }
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
        var baseMemoryRate = 0.005m;
        var baseStorageRate = 0.0001m;

        return (spec.CpuCores * baseCpuRate) +
               (spec.MemoryMb / 1024m * baseMemoryRate) +
               (spec.DiskGb * baseStorageRate);
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