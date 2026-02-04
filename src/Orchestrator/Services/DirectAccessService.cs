using Orchestrator.Data;
using Orchestrator.Models;
using Microsoft.Extensions.Logging;

namespace Orchestrator.Services;

/// <summary>
/// Manages Smart Port Allocation for VMs.
/// Coordinates port allocation on nodes via NodeAgent commands and DNS management via Cloudflare.
/// </summary>
public class DirectAccessService
{
    private readonly IMongoDbContext _dbContext;
    private readonly NodeCommandService _nodeCommandService;
    private readonly DirectAccessDnsService _dnsService;
    private readonly ILogger<DirectAccessService> _logger;

    public DirectAccessService(
        IMongoDbContext dbContext,
        NodeCommandService nodeCommandService,
        DirectAccessDnsService dnsService,
        ILogger<DirectAccessService> logger)
    {
        _dbContext = dbContext;
        _nodeCommandService = nodeCommandService;
        _dnsService = dnsService;
        _logger = logger;
    }

    /// <summary>
    /// Allocate a port for a VM. Creates iptables rules on the node and updates VM configuration.
    /// </summary>
    public async Task<AllocatePortResponse> AllocatePortAsync(
        string vmId,
        int vmPort,
        PortProtocol protocol = PortProtocol.TCP,
        string? label = null,
        CancellationToken ct = default)
    {
        try
        {
            // Get VM
            var vm = await _dbContext.VirtualMachines.FindOneAsync(v => v.Id == vmId, ct);
            if (vm == null)
            {
                return new AllocatePortResponse(
                    string.Empty, vmPort, 0, protocol, string.Empty,
                    Success: false, Error: "VM not found");
            }

            if (vm.Status != VmStatus.Running)
            {
                return new AllocatePortResponse(
                    string.Empty, vmPort, 0, protocol, string.Empty,
                    Success: false, Error: "VM must be running to allocate ports");
            }

            if (string.IsNullOrEmpty(vm.NetworkConfig.PrivateIp))
            {
                return new AllocatePortResponse(
                    string.Empty, vmPort, 0, protocol, string.Empty,
                    Success: false, Error: "VM does not have a private IP assigned");
            }

            // Initialize DirectAccess if needed
            if (vm.DirectAccess == null)
            {
                vm.DirectAccess = new VmDirectAccess();
            }

            // Check if port already allocated
            if (vm.DirectAccess.PortMappings.Any(m => m.VmPort == vmPort))
            {
                return new AllocatePortResponse(
                    string.Empty, vmPort, 0, protocol, string.Empty,
                    Success: false, Error: $"Port {vmPort} is already allocated for this VM");
            }

            // Ensure DNS is configured
            await EnsureDnsConfiguredAsync(vm, ct);

            _logger.LogInformation(
                "Allocating port {VmPort} ({Protocol}) for VM {VmId} ({VmIp})",
                vmPort, protocol, vmId, vm.NetworkConfig.PrivateIp);

            // Send command to NodeAgent to allocate port
            var payload = new
            {
                VmId = vmId,
                VmPrivateIp = vm.NetworkConfig.PrivateIp,
                VmPort = vmPort,
                Protocol = (int)protocol,
                Label = label
            };

            var success = await _nodeCommandService.SendCommandAsync(
                vm.NodeId,
                NodeCommandType.AllocatePort,
                payload,
                requiresAck: true,
                ct: ct);

            if (!success)
            {
                _logger.LogError("NodeAgent command failed for port allocation");
                return new AllocatePortResponse(
                    string.Empty, vmPort, 0, protocol, string.Empty,
                    Success: false, Error: "Failed to allocate port on node");
            }

            // Note: We don't know the allocated public port yet since the NodeAgent
            // allocates it from the pool. In a production system, we would either:
            // 1. Have the NodeAgent send back the allocated port in the acknowledgment
            // 2. Poll the NodeAgent for the port mapping
            // 3. Use a webhook for the NodeAgent to notify us
            //
            // For now, we'll create a placeholder mapping and update it later
            // when we implement proper acknowledgment handling.

            // Create mapping (with placeholder public port that will be updated)
            var mapping = new DirectAccessPortMapping
            {
                VmPort = vmPort,
                PublicPort = 0,  // Will be updated when we get acknowledgment from node
                Protocol = protocol,
                Label = label
            };

            vm.DirectAccess.PortMappings.Add(mapping);
            vm.UpdatedAt = DateTime.UtcNow;

            await _dbContext.VirtualMachines.UpdateOneAsync(vm, ct);

            var connectionString = GenerateConnectionString(
                vm.DirectAccess.DnsName ?? "unknown",
                mapping.PublicPort,
                label);

            _logger.LogInformation(
                "✓ Port allocated for VM {VmId}: {VmPort} → {PublicPort} ({Protocol})",
                vmId, vmPort, mapping.PublicPort, protocol);

            return new AllocatePortResponse(
                mapping.Id,
                vmPort,
                mapping.PublicPort,
                protocol,
                connectionString,
                Success: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error allocating port for VM {VmId}", vmId);
            return new AllocatePortResponse(
                string.Empty, vmPort, 0, protocol, string.Empty,
                Success: false, Error: ex.Message);
        }
    }

    /// <summary>
    /// Remove a port mapping from a VM
    /// </summary>
    public async Task<bool> RemovePortAsync(
        string vmId,
        int vmPort,
        CancellationToken ct = default)
    {
        try
        {
            var vm = await _dbContext.VirtualMachines.FindOneAsync(v => v.Id == vmId, ct);
            if (vm == null || vm.DirectAccess == null)
            {
                _logger.LogWarning("VM {VmId} not found or has no direct access configured", vmId);
                return false;
            }

            var mapping = vm.DirectAccess.PortMappings.FirstOrDefault(m => m.VmPort == vmPort);
            if (mapping == null)
            {
                _logger.LogWarning("Port mapping for port {VmPort} not found on VM {VmId}", vmPort, vmId);
                return false;
            }

            _logger.LogInformation(
                "Removing port mapping {VmPort} → {PublicPort} from VM {VmId}",
                vmPort, mapping.PublicPort, vmId);

            // Send command to NodeAgent
            var payload = new
            {
                VmId = vmId,
                VmPort = vmPort,
                Protocol = (int)mapping.Protocol
            };

            var success = await _nodeCommandService.SendCommandAsync(
                vm.NodeId,
                NodeCommandType.RemovePort,
                payload,
                requiresAck: true,
                ct: ct);

            if (!success)
            {
                _logger.LogWarning("NodeAgent command failed for port removal (continuing anyway)");
            }

            // Remove from VM configuration
            vm.DirectAccess.PortMappings.Remove(mapping);
            vm.UpdatedAt = DateTime.UtcNow;

            await _dbContext.VirtualMachines.UpdateOneAsync(vm, ct);

            _logger.LogInformation("✓ Port mapping removed for VM {VmId}: {VmPort}", vmId, vmPort);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing port mapping for VM {VmId}", vmId);
            return false;
        }
    }

    /// <summary>
    /// Get direct access info for a VM
    /// </summary>
    public async Task<DirectAccessInfoResponse?> GetDirectAccessInfoAsync(
        string vmId,
        CancellationToken ct = default)
    {
        var vm = await _dbContext.VirtualMachines.FindOneAsync(v => v.Id == vmId, ct);
        if (vm == null || vm.DirectAccess == null)
        {
            return null;
        }

        var mappings = vm.DirectAccess.PortMappings.Select(m => new PortMappingInfo(
            m.Id,
            m.VmPort,
            m.PublicPort,
            m.Protocol,
            m.Label,
            GenerateConnectionString(vm.DirectAccess.DnsName ?? "unknown", m.PublicPort, m.Label)
        )).ToList();

        return new DirectAccessInfoResponse(
            vm.DirectAccess.DnsName ?? string.Empty,
            mappings,
            vm.DirectAccess.IsDnsConfigured);
    }

    /// <summary>
    /// Quick-add a common service (SSH, MySQL, etc.)
    /// </summary>
    public async Task<AllocatePortResponse> QuickAddServiceAsync(
        string vmId,
        string serviceName,
        CancellationToken ct = default)
    {
        if (!CommonServices.Templates.TryGetValue(serviceName.ToLower(), out var template))
        {
            return new AllocatePortResponse(
                string.Empty, 0, 0, PortProtocol.TCP, string.Empty,
                Success: false, Error: $"Unknown service: {serviceName}");
        }

        return await AllocatePortAsync(
            vmId,
            template.Port,
            template.Protocol,
            template.Label,
            ct);
    }

    /// <summary>
    /// Remove all port mappings when a VM is deleted
    /// </summary>
    public async Task CleanupVmDirectAccessAsync(string vmId, CancellationToken ct = default)
    {
        try
        {
            var vm = await _dbContext.VirtualMachines.FindOneAsync(v => v.Id == vmId, ct);
            if (vm == null || vm.DirectAccess == null)
            {
                return;
            }

            _logger.LogInformation("Cleaning up direct access for VM {VmId}", vmId);

            // Remove all port mappings
            foreach (var mapping in vm.DirectAccess.PortMappings.ToList())
            {
                await RemovePortAsync(vmId, mapping.VmPort, ct);
            }

            // Remove DNS record
            if (!string.IsNullOrEmpty(vm.DirectAccess.CloudflareDnsRecordId))
            {
                await _dnsService.DeleteDnsRecordAsync(
                    vm.DirectAccess.CloudflareDnsRecordId,
                    ct);
            }

            _logger.LogInformation("✓ Direct access cleanup complete for VM {VmId}", vmId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up direct access for VM {VmId}", vmId);
        }
    }

    /// <summary>
    /// Ensure DNS is configured for a VM (create if not exists)
    /// </summary>
    private async Task EnsureDnsConfiguredAsync(VirtualMachine vm, CancellationToken ct)
    {
        if (vm.DirectAccess == null)
        {
            vm.DirectAccess = new VmDirectAccess();
        }

        if (vm.DirectAccess.IsDnsConfigured)
        {
            return;
        }

        // Get node public IP
        var node = await _dbContext.Nodes.FindOneAsync(n => n.Id == vm.NodeId, ct);
        if (node == null || string.IsNullOrEmpty(node.PublicIp))
        {
            throw new InvalidOperationException("Node not found or has no public IP");
        }

        // Generate subdomain (similar to ingress pattern)
        var id4 = vm.Id.Length >= 4 ? vm.Id.Substring(0, 4) : vm.Id;
        var subdomain = $"{SanitizeVmName(vm.Name)}-{id4}";
        var dnsName = $"{subdomain}.direct.stackfi.tech";

        _logger.LogInformation(
            "Creating DNS record {DnsName} → {NodeIp} for VM {VmId}",
            dnsName, node.PublicIp, vm.Id);

        // Create DNS record
        var recordId = await _dnsService.CreateOrUpdateDnsRecordAsync(
            dnsName,
            node.PublicIp,
            ct);

        if (string.IsNullOrEmpty(recordId))
        {
            throw new InvalidOperationException("Failed to create DNS record");
        }

        // Update VM
        vm.DirectAccess.Subdomain = subdomain;
        vm.DirectAccess.DnsName = dnsName;
        vm.DirectAccess.CloudflareDnsRecordId = recordId;
        vm.DirectAccess.IsDnsConfigured = true;
        vm.DirectAccess.DnsUpdatedAt = DateTime.UtcNow;

        await _dbContext.VirtualMachines.UpdateOneAsync(vm, ct);

        _logger.LogInformation("✓ DNS configured: {DnsName} → {NodeIp}", dnsName, node.PublicIp);
    }

    /// <summary>
    /// Sanitize VM name for DNS (alphanumeric + hyphens only)
    /// </summary>
    private string SanitizeVmName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "vm";
        }

        var sanitized = new string(name
            .ToLower()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());

        // Remove leading/trailing hyphens
        sanitized = sanitized.Trim('-');

        // Collapse multiple hyphens
        while (sanitized.Contains("--"))
        {
            sanitized = sanitized.Replace("--", "-");
        }

        return string.IsNullOrEmpty(sanitized) ? "vm" : sanitized;
    }

    /// <summary>
    /// Generate connection string example for a port mapping
    /// </summary>
    private string GenerateConnectionString(string dnsName, int publicPort, string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return $"{dnsName}:{publicPort}";
        }

        return label.ToLower() switch
        {
            "ssh" or "sftp" => $"ssh user@{dnsName} -p {publicPort}",
            "mysql" => $"mysql -h {dnsName} -P {publicPort} -u root -p",
            "postgresql" => $"psql -h {dnsName} -p {publicPort} -U postgres",
            "mongodb" => $"mongodb://{dnsName}:{publicPort}",
            "redis" => $"redis-cli -h {dnsName} -p {publicPort}",
            "rdp" => $"rdp://{dnsName}:{publicPort}",
            "minecraft" => $"Server Address: {dnsName}:{publicPort}",
            _ => $"{dnsName}:{publicPort}"
        };
    }
}
