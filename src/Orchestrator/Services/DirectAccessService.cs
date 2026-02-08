using System.Text.Json;
using Orchestrator.Persistence;
using Orchestrator.Models;
using Microsoft.Extensions.Logging;

namespace Orchestrator.Services;

/// <summary>
/// Manages Smart Port Allocation for VMs.
/// Coordinates port allocation on nodes via NodeAgent commands and DNS management via Cloudflare.
/// </summary>
public class DirectAccessService
{
    private readonly DataStore _dataStore;
    private readonly INodeCommandService _nodeCommandService;
    private readonly DirectAccessDnsService _dnsService;
    private readonly ILogger<DirectAccessService> _logger;

    public DirectAccessService(
        DataStore dataStore,
        INodeCommandService nodeCommandService,
        DirectAccessDnsService dnsService,
        ILogger<DirectAccessService> logger)
    {
        _dataStore = dataStore;
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
            var vm = await _dataStore.GetVmAsync(vmId);
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

            // Get VM's node to determine allocation strategy
            var vmNode = await _dataStore.GetNodeAsync(vm.NodeId);
            if (vmNode == null)
            {
                return new AllocatePortResponse(
                    string.Empty, vmPort, 0, protocol, string.Empty,
                    Success: false, Error: $"Node {vm.NodeId} not found");
            }

            // Determine target node for port allocation (relay for CGNAT, direct for non-CGNAT)
            string targetNodeId;
            bool isRelayForwarding = false;
            string? tunnelDestinationIp = null;

            if (vmNode.CgnatInfo != null)
            {
                // CGNAT VM requires 3-hop forwarding:
                // External → Relay Host → Relay VM → CGNAT Node → VM
                var relayNode = await _dataStore.GetNodeAsync(vmNode.CgnatInfo.AssignedRelayNodeId);
                if (relayNode == null || string.IsNullOrEmpty(relayNode.PublicIp))
                {
                    return new AllocatePortResponse(
                        string.Empty, vmPort, 0, protocol, string.Empty,
                        Success: false, 
                        Error: $"Relay node {vmNode.CgnatInfo.AssignedRelayNodeId} not found or has no public IP");
                }

                _logger.LogInformation(
                    "Setting up 3-hop forwarding for CGNAT VM {VmId}: " +
                    "Relay {RelayId} ({RelayIp}) → CGNAT {NodeId} ({TunnelIp}) → VM ({VmIp}:{VmPort})",
                    vmId, relayNode.Id, relayNode.PublicIp, vmNode.Id, vmNode.CgnatInfo.TunnelIp, vm.NetworkConfig.PrivateIp, vmPort);

                // STEP 1: Allocate port on relay node (2nd hop: relay VM → CGNAT node's tunnel IP)
                var relayPayload = new
                {
                    VmId = vmId,
                    VmPrivateIp = vmNode.CgnatInfo.TunnelIp,  // Forward to CGNAT node's tunnel IP
                    VmPort = 0,  // Placeholder - will use same port as public
                    Protocol = (int)protocol,
                    Label = $"{label} (relay→tunnel)",
                    IsRelayForwarding = true,
                    TunnelDestinationIp = vmNode.CgnatInfo.TunnelIp
                };

                var relayCommand = new NodeCommand(
                    Guid.NewGuid().ToString(),
                    NodeCommandType.AllocatePort,
                    JsonSerializer.Serialize(relayPayload),
                    RequiresAck: true,
                    TargetResourceId: vmId
                );

                _dataStore.RegisterCommand(
                    relayCommand.CommandId,
                    vmId,
                    relayNode.Id,
                    NodeCommandType.AllocatePort
                );

                _logger.LogInformation("Sending AllocatePort to relay node {RelayId}", relayNode.Id);
                var relayResult = await _nodeCommandService.DeliverCommandAsync(relayNode.Id, relayCommand, ct);

                if (!relayResult.Success)
                {
                    _logger.LogError("Failed to deliver command to relay node: {Message}", relayResult.Message);
                    return new AllocatePortResponse(
                        string.Empty, vmPort, 0, protocol, string.Empty,
                        Success: false, Error: $"Failed to setup relay forwarding: {relayResult.Message}");
                }

                // Create a placeholder mapping for the relay command so the acknowledgment handler can update it
                var tempMapping = new DirectAccessPortMapping
                {
                    VmPort = 0,  // Placeholder - relay doesn't map to a specific VM port
                    PublicPort = 0,  // Will be filled by acknowledgment
                    Protocol = protocol,
                    CreatedAt = DateTime.UtcNow
                };
                vm.DirectAccess.PortMappings.Add(tempMapping);
                await _dataStore.SaveVmAsync(vm);

                // Wait for relay node to allocate a public port
                _logger.LogDebug("Waiting for relay node to allocate public port...");
                var publicPort = await WaitForPortAllocationAsync(vmId, 0, protocol, relayCommand.CommandId, ct);

                if (publicPort <= 0)
                {
                    _logger.LogError("Relay node did not allocate a public port within timeout");
                    
                    // Clean up placeholder mapping
                    vm = await _dataStore.GetVmAsync(vmId);
                    if (vm?.DirectAccess != null)
                    {
                        var placeholderMapping = vm.DirectAccess.PortMappings
                            .FirstOrDefault(m => m.VmPort == 0 && m.Protocol == protocol);
                        if (placeholderMapping != null)
                        {
                            vm.DirectAccess.PortMappings.Remove(placeholderMapping);
                            await _dataStore.SaveVmAsync(vm);
                        }
                    }
                    
                    return new AllocatePortResponse(
                        string.Empty, vmPort, 0, protocol, string.Empty,
                        Success: false, Error: "Relay port allocation failed or timed out");
                }

                _logger.LogInformation(
                    "✓ Relay node allocated public port {PublicPort} for CGNAT VM {VmId}",
                    publicPort, vmId);

                // Remove the temporary placeholder mapping (it was only needed for acknowledgment handling)
                vm = await _dataStore.GetVmAsync(vmId);  // Refresh VM state
                if (vm?.DirectAccess != null)
                {
                    var placeholderMapping = vm.DirectAccess.PortMappings
                        .FirstOrDefault(m => m.VmPort == 0 && m.Protocol == protocol && m.PublicPort == publicPort);
                    if (placeholderMapping != null)
                    {
                        vm.DirectAccess.PortMappings.Remove(placeholderMapping);
                        await _dataStore.SaveVmAsync(vm);
                        _logger.LogDebug("Removed temporary placeholder mapping for relay port");
                    }
                }


                // STEP 2: Allocate port on CGNAT node (3rd hop: CGNAT node → VM's local IP)
                var cgnatPayload = new
                {
                    VmId = vmId,
                    VmPrivateIp = vm.NetworkConfig.PrivateIp,  // Forward to VM's local IP  
                    VmPort = vmPort,  // VM's actual service port
                    PublicPort = publicPort,  // Use same port as relay allocated
                    Protocol = (int)protocol,
                    Label = "${label} (node→vm)",
                    IsRelayForwarding = false  // Direct local forwarding
                };

                var cgnatCommand = new NodeCommand(
                    Guid.NewGuid().ToString(),
                    NodeCommandType.AllocatePort,
                    JsonSerializer.Serialize(cgnatPayload),
                    RequiresAck: true,
                    TargetResourceId: vmId
                );

                _dataStore.RegisterCommand(
                    cgnatCommand.CommandId,
                    vmId,
                    vmNode.Id,
                    NodeCommandType.AllocatePort
                );

                _logger.LogInformation(
                    "Sending AllocatePort to CGNAT node {NodeId} with port {PublicPort}",
                    vmNode.Id, publicPort);
                var cgnatResult = await _nodeCommandService.DeliverCommandAsync(vmNode.Id, cgnatCommand, ct);

                if (!cgnatResult.Success)
                {
                    _logger.LogError("Failed to deliver command to CGNAT node: {Message}", cgnatResult.Message);
                    
                    // Rollback: Remove relay port allocation
                    _logger.LogWarning("Rolling back relay port allocation for VM {VmId}", vmId);
                    try
                    {
                        await RemovePortAsync(vmId, vmPort, ct);
                    }
                    catch (Exception rollbackEx)
                    {
                        _logger.LogError(rollbackEx, "Failed to rollback relay port allocation");
                    }
                    
                    return new AllocatePortResponse(
                        string.Empty, vmPort, 0, protocol, string.Empty,
                        Success: false, Error: $"Failed to setup CGNAT node forwarding: {cgnatResult.Message}");
                }

                _logger.LogInformation(
                    "✓ 3-hop forwarding complete for VM {VmId}: " +
                    "External:{PublicPort} → Relay → CGNAT:{PublicPort} → VM:{VmPort}",
                    vmId, publicPort, vmPort);

                // Create mapping with allocated port
                var cgnatMapping = new DirectAccessPortMapping
                {
                    VmPort = vmPort,
                    PublicPort = publicPort,
                    Protocol = protocol,
                    Label = label
                };

                vm.DirectAccess.PortMappings.Add(cgnatMapping);
                vm.UpdatedAt = DateTime.UtcNow;
                await _dataStore.SaveVmAsync(vm);

                var connectionString = GenerateConnectionString(
                    vm.DirectAccess.DnsName ?? "unknown",
                    publicPort,
                    label);

                return new AllocatePortResponse(
                    cgnatMapping.Id,
                    vmPort,
                    publicPort,
                    protocol,
                    connectionString,
                    Success: true);
            }
            else
            {
                // Direct access node - allocate normally
                if (string.IsNullOrEmpty(vmNode.PublicIp))
                {
                   return new AllocatePortResponse(
                        string.Empty, vmPort, 0, protocol, string.Empty,
                        Success: false, Error: $"Node {vm.NodeId} has no public IP");
                }

                targetNodeId = vmNode.Id;

                _logger.LogInformation(
                    "Allocating port on node {NodeId} ({NodeIp}) for VM {VmId}",
                    vmNode.Id, vmNode.PublicIp, vmId);
            }

            _logger.LogInformation(
                "Allocating port {VmPort} ({Protocol}) for VM {VmId} ({VmIp})",
                vmPort, protocol, vmId, vm.NetworkConfig.PrivateIp);

            // Send command to target node (relay for CGNAT, vm node for direct)
            var payload = new
            {
                VmId = vmId,
                VmPrivateIp = vm.NetworkConfig.PrivateIp,
                VmPort = vmPort,
                Protocol = (int)protocol,
                Label = label,
                // NEW: Relay forwarding information
                IsRelayForwarding = isRelayForwarding,
                TunnelDestinationIp = tunnelDestinationIp
            };

            var command = new NodeCommand(
                Guid.NewGuid().ToString(),
                NodeCommandType.AllocatePort,
                JsonSerializer.Serialize(payload),
                RequiresAck: true,
                TargetResourceId: vmId
            );

            _dataStore.RegisterCommand(
                command.CommandId,
                vmId,
                targetNodeId,  // Register with target node (relay or vm node)
                NodeCommandType.AllocatePort
            );

            var result = await _nodeCommandService.DeliverCommandAsync(targetNodeId, command, ct);

            if (!result.Success)
            {
                _logger.LogError("NodeAgent command delivery failed for port allocation: {Message}", result.Message);
                return new AllocatePortResponse(
                    string.Empty, vmPort, 0, protocol, string.Empty,
                    Success: false, Error: $"Failed to deliver command to node: {result.Message}");
            }

            // Create placeholder mapping (will be updated by acknowledgment handler)
            var mapping = new DirectAccessPortMapping
            {
                VmPort = vmPort,
                PublicPort = 0,  // Placeholder - will be updated by acknowledgment
                Protocol = protocol,
                Label = label
            };

            vm.DirectAccess.PortMappings.Add(mapping);
            vm.UpdatedAt = DateTime.UtcNow;

            await _dataStore.SaveVmAsync(vm);

            _logger.LogDebug(
                "Created placeholder mapping for VM {VmId}, waiting for acknowledgment with actual port...",
                vmId);

            // Wait for acknowledgment to update the public port
            var actualPort = await WaitForPortAllocationAsync(vmId, vmPort, protocol, command.CommandId, ct);

            if (actualPort > 0)
            {
                _logger.LogInformation(
                    "✓ Port allocated for VM {VmId}: {VmPort} → {PublicPort} ({Protocol})",
                    vmId, vmPort, actualPort, protocol);

                var connectionString = GenerateConnectionString(
                    vm.DirectAccess.DnsName ?? "unknown",
                    actualPort,
                    label);

                return new AllocatePortResponse(
                    mapping.Id,
                    vmPort,
                    actualPort,
                    protocol,
                    connectionString,
                    Success: true);
            }
            else
            {
                _logger.LogWarning(
                    "Port allocation acknowledgment timed out for VM {VmId} - port may still be allocated but not confirmed",
                    vmId);

                // Return with publicPort=0 and indicate timeout
                return new AllocatePortResponse(
                    mapping.Id,
                    vmPort,
                    0,
                    protocol,
                    "Port allocation in progress - please check status in a moment",
                    Success: true);  // Still success, just delayed
            }
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
            var vm = await _dataStore.GetVmAsync(vmId);
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

            // Get the VM's node
            var vmNode = await _dataStore.GetNodeAsync(vm.NodeId);
            if (vmNode == null)
            {
                _logger.LogError("VM {VmId} references non-existent node {NodeId}", vmId, vm.NodeId);
                return false;
            }

            // Check if this is a CGNAT VM requiring 3-hop cleanup
            bool isCgnatVm = vmNode.CgnatInfo?.AssignedRelayNodeId != null;

            if (isCgnatVm)
            {
                // 3-HOP CLEANUP: Remove from both CGNAT node and relay node
                _logger.LogInformation(
                    "Cleaning up 3-hop forwarding for CGNAT VM {VmId}: Relay {RelayId} + CGNAT {NodeId}",
                    vmId, vmNode.CgnatInfo!.AssignedRelayNodeId, vmNode.Id);

                // STEP 1: Remove from CGNAT node (3rd hop: node → VM)
                var cgnatPayload = new
                {
                    VmId = vmId,
                    VmPort = vmPort,
                    Protocol = (int)mapping.Protocol
                };

                var cgnatCommand = new NodeCommand(
                    Guid.NewGuid().ToString(),
                    NodeCommandType.RemovePort,
                    JsonSerializer.Serialize(cgnatPayload),
                    RequiresAck: true,
                    TargetResourceId: vmId
                );

                _dataStore.RegisterCommand(
                    cgnatCommand.CommandId,
                    vmId,
                    vmNode.Id,
                    NodeCommandType.RemovePort
                );

                _logger.LogInformation("Sending RemovePort to CGNAT node {NodeId} for port {PublicPort}", vmNode.Id, mapping.PublicPort);
                var cgnatResult = await _nodeCommandService.DeliverCommandAsync(vmNode.Id, cgnatCommand, ct);

                if (!cgnatResult.Success)
                {
                    _logger.LogWarning("Failed to deliver RemovePort to CGNAT node (continuing anyway): {Message}", cgnatResult.Message);
                }

                // STEP 2: Remove from relay node (2nd hop: relay → tunnel)
                var relayNode = await _dataStore.GetNodeAsync(vmNode.CgnatInfo.AssignedRelayNodeId);
                if (relayNode != null)
                {
                    // Relay stores mappings with VmPort=PublicPort (not 0)
                    var relayPayload = new
                    {
                        VmId = vmId,
                        VmPort = mapping.PublicPort,  // Relay uses publicPort as vmPort
                        Protocol = (int)mapping.Protocol
                    };

                    var relayCommand = new NodeCommand(
                        Guid.NewGuid().ToString(),
                        NodeCommandType.RemovePort,
                        JsonSerializer.Serialize(relayPayload),
                        RequiresAck: true,
                        TargetResourceId: vmId
                    );

                    _dataStore.RegisterCommand(
                        relayCommand.CommandId,
                        vmId,
                        relayNode.Id,
                        NodeCommandType.RemovePort
                    );

                    _logger.LogInformation("Sending RemovePort to relay node {RelayId} for port {PublicPort}", relayNode.Id, mapping.PublicPort);
                    var relayResult = await _nodeCommandService.DeliverCommandAsync(relayNode.Id, relayCommand, ct);

                    if (!relayResult.Success)
                    {
                        _logger.LogWarning("Failed to deliver RemovePort to relay node (continuing anyway): {Message}", relayResult.Message);
                    }
                }
                else
                {
                    _logger.LogWarning("Relay node {RelayId} not found for CGNAT VM cleanup", vmNode.CgnatInfo.AssignedRelayNodeId);
                }
            }
            else
            {
                // NORMAL DIRECT ACCESS: Remove from single node
                var payload = new
                {
                    VmId = vmId,
                    VmPort = vmPort,
                    Protocol = (int)mapping.Protocol
                };

                var command = new NodeCommand(
                    Guid.NewGuid().ToString(),
                    NodeCommandType.RemovePort,
                    JsonSerializer.Serialize(payload),
                    RequiresAck: true,
                    TargetResourceId: vmId
                );

                _dataStore.RegisterCommand(
                    command.CommandId,
                    vmId,
                    vm.NodeId,
                    NodeCommandType.RemovePort
                );

                var result = await _nodeCommandService.DeliverCommandAsync(vm.NodeId, command, ct);

                if (!result.Success)
                {
                    _logger.LogWarning("NodeAgent command delivery failed for port removal (continuing anyway): {Message}", result.Message);
                }
            }

            // Remove from VM configuration
            vm.DirectAccess.PortMappings.Remove(mapping);
            vm.UpdatedAt = DateTime.UtcNow;

            await _dataStore.SaveVmAsync(vm);

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
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            return null;
        }

        // If DirectAccess is not initialized, return empty info (no port mappings yet)
        if (vm.DirectAccess == null)
        {
            return new DirectAccessInfoResponse(
                string.Empty,
                new List<PortMappingInfo>(),
                IsDnsConfigured: false);
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
            var vm = await _dataStore.GetVmAsync(vmId);
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

        // Get VM's node
        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        if (node == null)
        {
            throw new InvalidOperationException("Node not found");
        }

        // Determine the correct public IP for DNS:
        // - For CGNAT nodes: use the relay node's public IP
        // - For direct access nodes: use the node's own public IP
        string targetIp;
        string targetDescription;

        if (node.CgnatInfo != null)
        {
            // CGNAT node - use relay node's public IP
            var relayNode = await _dataStore.GetNodeAsync(node.CgnatInfo.AssignedRelayNodeId);
            if (relayNode == null || string.IsNullOrEmpty(relayNode.PublicIp))
            {
                throw new InvalidOperationException(
                    $"CGNAT node {node.Id} has no assigned relay or relay has no public IP");
            }

            targetIp = relayNode.PublicIp;
            targetDescription = $"relay {relayNode.Id}";

            _logger.LogInformation(
                "Using relay node {RelayId} IP {RelayIp} for DNS (CGNAT VM {VmId})",
                relayNode.Id, relayNode.PublicIp, vm.Id);
        }
        else
        {
            // Direct access node - use node's own public IP
            if (string.IsNullOrEmpty(node.PublicIp))
            {
                throw new InvalidOperationException("Node has no public IP");
            }

            targetIp = node.PublicIp;
            targetDescription = $"node {node.Id}";
        }

        // Generate subdomain (similar to ingress pattern)
        var id4 = vm.Id.Length >= 4 ? vm.Id.Substring(0, 4) : vm.Id;
        var subdomain = $"{SanitizeVmName(vm.Name)}-{id4}";
        var dnsName = $"{subdomain}.direct.stackfi.tech";

        _logger.LogInformation(
            "Creating DNS record {DnsName} → {TargetIp} ({Description}) for VM {VmId}",
            dnsName, targetIp, targetDescription, vm.Id);

        // Create DNS record (optional - gracefully degrade if Cloudflare not configured)
        var recordId = await _dnsService.CreateOrUpdateDnsRecordAsync(
            dnsName,
            targetIp,
            ct);

        if (string.IsNullOrEmpty(recordId))
        {
            _logger.LogWarning(
                "DNS record creation skipped for VM {VmId}. " +
                "Cloudflare may not be configured. " +
                "Port allocation will work but without DNS name. " +
                "Access via node IP: {NodeIp}",
                vm.Id, node.PublicIp);

            // Update VM with partial DNS info (no record ID, DNS not configured)
            vm.DirectAccess.Subdomain = subdomain;
            // Do not persist dnsName when DNS is not configured to avoid implying it is active.
            vm.DirectAccess.DnsName = null;
            vm.DirectAccess.CloudflareDnsRecordId = null;
            vm.DirectAccess.IsDnsConfigured = false;
            vm.DirectAccess.DnsUpdatedAt = DateTime.UtcNow;
        }
        else
        {
            // Update VM with full DNS configuration
            vm.DirectAccess.Subdomain = subdomain;
            vm.DirectAccess.DnsName = dnsName;
            vm.DirectAccess.CloudflareDnsRecordId = recordId;
            vm.DirectAccess.IsDnsConfigured = true;
            vm.DirectAccess.DnsUpdatedAt = DateTime.UtcNow;

            _logger.LogInformation("✓ DNS configured: {DnsName} → {TargetIp}", dnsName, targetIp);
        }

        await _dataStore.SaveVmAsync(vm);
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

    /// <summary>
    /// Wait for port allocation acknowledgment by polling the VM for the updated public port.
    /// The acknowledgment handler (NodeService.ProcessCommandAcknowledgmentAsync) updates the mapping asynchronously.
    /// </summary>
    private async Task<int> WaitForPortAllocationAsync(
        string vmId,
        int vmPort,
        PortProtocol protocol,
        string commandId,
        CancellationToken ct)
    {
        const int maxAttempts = 60;  // 60 attempts = 30 seconds max wait (increased from 10s to handle processing delays)
        const int delayMs = 500;     // Poll every 500ms

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Add small delay before checking (except first time)
            if (attempt > 0)
            {
                await Task.Delay(delayMs, ct);
            }

            // Reload VM to get latest state
            var vm = await _dataStore.GetVmAsync(vmId);
            if (vm?.DirectAccess == null)
            {
                _logger.LogWarning(
                    "VM {VmId} or DirectAccess configuration disappeared during acknowledgment wait",
                    vmId);
                return 0;
            }

            // Check if the mapping has been updated with a public port
            var mapping = vm.DirectAccess.PortMappings
                .FirstOrDefault(m => m.VmPort == vmPort && m.Protocol == protocol);

            if (mapping != null && mapping.PublicPort > 0)
            {
                _logger.LogDebug(
                    "Acknowledgment received for VM {VmId} after {Attempts} attempts ({Ms}ms): port {PublicPort}",
                    vmId, attempt + 1, (attempt + 1) * delayMs, mapping.PublicPort);
                return mapping.PublicPort;
            }

            _logger.LogDebug(
                "Waiting for acknowledgment... attempt {Attempt}/{MaxAttempts} (command: {CommandId})",
                attempt + 1, maxAttempts, commandId);
        }

        _logger.LogWarning(
            "Acknowledgment timeout for VM {VmId} after {Seconds}s (command: {CommandId})",
            vmId, (maxAttempts * delayMs) / 1000, commandId);

        return 0;  // Timeout
    }
}
