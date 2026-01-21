using Orchestrator.Persistence;
using Orchestrator.Models;
using Microsoft.Extensions.Logging;

namespace Orchestrator.Services;

/// <summary>
/// Manages relay node infrastructure
/// </summary>
public interface IRelayNodeService
{
    /// <summary>
    /// Check if a node is eligible to be a relay
    /// </summary>
    bool IsEligibleForRelay(Node node);

    /// <summary>
    /// Automatically deploy relay VM on eligible node
    /// Called during node registration
    /// </summary>
    Task<string?> DeployRelayVmAsync(Node node, IVmService vmService, CancellationToken ct = default);

    /// <summary>
    /// Find the best relay node for a CGNAT node
    /// </summary>
    Task<Node?> FindBestRelayForCgnatNodeAsync(Node cgnatNode, CancellationToken ct = default);

    /// <summary>
    /// Assign a CGNAT node to a relay
    /// </summary>
    Task<bool> AssignCgnatNodeToRelayAsync(Node cgnatNode, Node relayNode, CancellationToken ct = default);
}

/// <summary>
/// Manages relay node infrastructure
/// </summary>
public class RelayNodeService : IRelayNodeService
{
    private readonly DataStore _dataStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly IWireGuardManager _wireGuardManager;
    private readonly ILogger<RelayNodeService> _logger;

    // Criteria for relay eligibility
    private const int MIN_CORES_FOR_RELAY = 2;
    private const long MIN_RAM_FOR_RELAY = 4L * 1024 * 1024 * 1024; // 32GB
    private const long MIN_BANDWIDTH_FOR_RELAY = 50L * 1024 * 1024; // 100 Mbps

    public RelayNodeService(
        DataStore dataStore,
        IServiceProvider serviceProvider,
        IWireGuardManager wireGuardManager,
        ILogger<RelayNodeService> logger)
    {
        _dataStore = dataStore;
        _serviceProvider = serviceProvider;
        _wireGuardManager = wireGuardManager;
        _logger = logger;
    }

    /// <summary>
    /// Check if a node is eligible to be a relay
    /// </summary>
    public bool IsEligibleForRelay(Node node)
    {
        // Must have public IP (NAT type = None)
        if (node.HardwareInventory.Network.NatType != NatType.None)
        {
            _logger.LogDebug(
                "Node {NodeId} not eligible for relay: NAT type = {NatType}",
                node.Id, node.HardwareInventory.Network.NatType);
            return false;
        }

        // Must have sufficient resources
        if (node.TotalResources.ComputePoints < MIN_CORES_FOR_RELAY)
        {
            _logger.LogDebug(
                "Node {NodeId} not eligible for relay: insufficient CPU",
                node.Id);
            return false;
        }

        if (node.HardwareInventory.Memory.TotalBytes < MIN_RAM_FOR_RELAY)
        {
            _logger.LogDebug(
                "Node {NodeId} not eligible for relay: insufficient RAM",
                node.Id);
            return false;
        }

        // Check bandwidth if available
        var bandwidth = node.HardwareInventory.Network.BandwidthBitsPerSecond;
        if (bandwidth.HasValue && bandwidth.Value < MIN_BANDWIDTH_FOR_RELAY)
        {
            _logger.LogDebug(
                "Node {NodeId} not eligible for relay: insufficient bandwidth",
                node.Id);
            return false;
        }

        _logger.LogInformation(
            "Node {NodeId} is ELIGIBLE for relay service",
            node.Id);

        return true;
    }

    /// <summary>
    /// Automatically deploy relay VM on eligible node
    /// Called during node registration
    /// </summary>
    public async Task<string?> DeployRelayVmAsync(Node node, IVmService vmService, CancellationToken ct = default)
    {
        if (!IsEligibleForRelay(node))
        {
            _logger.LogInformation(
                "Skipping relay deployment for node {NodeId} - not eligible",
                node.Id);
            return null;
        }

        _logger.LogInformation(
            "Deploying relay VM on node {NodeId}",
            node.Id);

        try
        {
            // ========================================
            // STEP 0: Allocate unique subnet for this relay
            // ========================================
            var relaySubnet = AllocateRelaySubnet();
            var relayTunnelIp = $"10.20.{relaySubnet}.254";

            _logger.LogInformation(
                "Relay on node {NodeId} assigned subnet {Subnet} (tunnel IP: {TunnelIp})",
                node.Id, relaySubnet, relayTunnelIp);

            // ========================================
            // STEP 1: Generate WireGuard keypair for relay VM
            // ========================================
            _logger.LogInformation(
                "Generating WireGuard keypair for relay VM on node {NodeId}",
                node.Id);

            var relayPrivateKey = await GenerateWireGuardPrivateKeyAsync(ct);
            var relayPublicKey = await DerivePublicKeyAsync(relayPrivateKey, ct);

            _logger.LogInformation(
                "Generated WireGuard keys for relay on node {NodeId} (public key: {PubKey})",
                node.Id, relayPublicKey);

            // ========================================
            // STEP 2: Create relay VM specification
            // ========================================

            var vmSpec = DeterminRelayConfiguration(node);

            // ========================================
            // STEP 3: Create relay VM with WireGuard private key
            // ========================================
            // The private key is passed to the VM via labels
            // The VM deployment process will read this and configure WireGuard
            var relayVm = await vmService.CreateVmAsync(
                userId: "system",
                request: new CreateVmRequest
                (
                    Name: $"relay-{node.Region}-{node.Id[..8]}",
                    Spec: vmSpec,
                    VmType: VmType.Relay,
                    NodeId: node.Id,
                    Labels: new Dictionary<string, string>
                    {
                        { "role", "relay" },
                        { "wireguard-private-key", relayPrivateKey },  // Pass private key to VM
                        { "relay-region", node.Region ?? "default" },
                        { "node-public-ip", node.PublicIp },
                        { "relay-subnet", relaySubnet.ToString() }
                    }
                ),
                node.Id
            );

            // ========================================
            // STEP 4: Initialize relay configuration with public key
            // ========================================
            node.RelayInfo = new RelayNodeInfo
            {
                RelayVmId = relayVm.VmId,
                WireGuardEndpoint = $"{node.PublicIp}:51820",
                WireGuardPublicKey = relayPublicKey,
                WireGuardPrivateKey = relayPrivateKey,
                TunnelIp = relayTunnelIp,
                RelaySubnet = relaySubnet,
                MaxCapacity = vmSpec.MaxConnections,
                CurrentLoad = 0,
                Region = node.Region ?? "default",
                Status = RelayStatus.Active,
                LastHealthCheck = DateTime.UtcNow
            };

            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "✓ Relay VM {VmId} deployed on node {NodeId} " +
                "(Subnet: 10.20.{Subnet}.0/24, Gateway: {TunnelIp}, Capacity: {Capacity})",
                relayVm.VmId, node.Id, relaySubnet, relayTunnelIp, vmSpec.MaxConnections);

            return relayVm.VmId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to deploy relay VM on node {NodeId}",
                node.Id);
            return null;
        }
    }

    /// <summary>
    /// Allocate next available relay subnet (1-254)
    /// Each relay gets a unique /24 subnet within 10.20.0.0/16
    /// </summary>
    private int AllocateRelaySubnet()
    {
        // Get all existing relay subnets
        var usedSubnets = _dataStore.Nodes.Values
            .Where(n => n.RelayInfo?.IsActive == true)
            .Select(n => n.RelayInfo.RelaySubnet)
            .Where(s => s > 0)  // Filter out uninitialized (0)
            .ToHashSet();

        // Find first available subnet (1-254)
        for (int subnet = 1; subnet <= 254; subnet++)
        {
            if (!usedSubnets.Contains(subnet))
            {
                _logger.LogInformation(
                    "Allocated relay subnet: 10.20.{Subnet}.0/24",
                    subnet);
                return subnet;
            }
        }

        throw new InvalidOperationException(
            "No available relay subnets (maximum 254 relays reached)");
    }

    private VmSpec DeterminRelayConfiguration(Node node)
    {
        // Determine relay VM spec based on node resources
        var computePoints = node.TotalResources.ComputePoints;
        if (computePoints >= 200)
        {
            return RelayVmSpec.Premium;
        }
        else if (computePoints >= 100)
        {
            return RelayVmSpec.High;
        }
        else if (computePoints >= 20)
        {
            return RelayVmSpec.Standard;
        }
        else
        {
            return RelayVmSpec.Basic;
        }
    }
    /// <summary>
    /// Find the best relay node for a CGNAT node
    /// </summary>
    public async Task<Node?> FindBestRelayForCgnatNodeAsync(
        Node cgnatNode,
        CancellationToken ct = default)
    {
        var relayNodes = _dataStore.Nodes.Values
            .Where(n => n.Status == NodeStatus.Online &&
                       n.RelayInfo != null &&
                       n.RelayInfo.IsActive &&
                       n.RelayInfo.Status == RelayStatus.Active &&
                       n.RelayInfo.CurrentLoad < n.RelayInfo.MaxCapacity)
            .ToList();

        if (!relayNodes.Any())
        {
            _logger.LogWarning("No available relay nodes for CGNAT node {NodeId}", cgnatNode.Id);
            return null;
        }

        // Score relays based on:
        // 1. Geographic proximity (same region = higher score)
        // 2. Current load (lower load = higher score)
        // 3. Capacity headroom

        var scoredRelays = relayNodes.Select(relay => new
        {
            Node = relay,
            Score = CalculateRelayScore(relay, cgnatNode)
        })
        .OrderByDescending(x => x.Score)
        .ToList();

        var bestRelay = scoredRelays.First().Node;

        _logger.LogInformation(
            "Selected relay {RelayId} for CGNAT node {CgnatId} " +
            "(Region: {Region}, Load: {Load}/{Capacity}, Score: {Score:F2})",
            bestRelay.Id, cgnatNode.Id,
            bestRelay.Region, bestRelay.RelayInfo!.CurrentLoad,
            bestRelay.RelayInfo.MaxCapacity, scoredRelays.First().Score);

        return bestRelay;
    }

    private double CalculateRelayScore(Node relayNode, Node cgnatNode)
    {
        var score = 100.0;

        // Geographic proximity (50 points max)
        if (relayNode.Region == cgnatNode.Region)
            score += 50;
        else if (relayNode.Zone == cgnatNode.Zone)
            score += 25;

        // Load factor (30 points max)
        var loadPercent = (double)relayNode.RelayInfo!.CurrentLoad / relayNode.RelayInfo.MaxCapacity;
        score += (1.0 - loadPercent) * 30;

        // Capacity headroom (20 points max)
        var headroom = relayNode.RelayInfo.MaxCapacity - relayNode.RelayInfo.CurrentLoad;
        score += Math.Min(headroom / 5.0, 20.0);

        return score;
    }

    /// <summary>
    /// Assign a CGNAT node to a relay
    /// </summary>
    public async Task<bool> AssignCgnatNodeToRelayAsync(
        Node cgnatNode,
        Node relayNode,
        CancellationToken ct = default)
    {
        if (relayNode.RelayInfo == null || !relayNode.RelayInfo.IsActive)
        {
            _logger.LogError("Cannot assign to inactive relay {RelayId}", relayNode.Id);
            return false;
        }

        if (relayNode.RelayInfo.CurrentLoad >= relayNode.RelayInfo.MaxCapacity)
        {
            _logger.LogError("Relay {RelayId} at full capacity", relayNode.Id);
            return false;
        }

        try
        {
            // Allocate tunnel IP for CGNAT node
            var tunnelIp = AllocateTunnelIp(relayNode);

            // Generate WireGuard configuration
            var wgConfig = await GenerateWireGuardConfigAsync(cgnatNode, relayNode, tunnelIp, ct);

            // Initialize CGNAT info
            cgnatNode.CgnatInfo = new CgnatNodeInfo
            {
                AssignedRelayNodeId = relayNode.Id,
                TunnelIp = tunnelIp,
                WireGuardConfig = wgConfig,
                PublicEndpoint = $"https://relay-{relayNode.Region}-{relayNode.Id[..8]}.vms.stackfi.tech",
                TunnelStatus = TunnelStatus.Connecting,
                LastHandshake = null
            };

            // Register CGNAT node with relay VM's WireGuard server
            var registered = await RegisterCgnatNodeWithRelayAsync(cgnatNode, relayNode, tunnelIp, ct);
            if (!registered)
            {
                _logger.LogWarning(
                    "Failed to register CGNAT node {NodeId} with relay VM - " +
                    "node will receive config but relay may not accept connection",
                    cgnatNode.Id);
            }

            // Update relay load
            relayNode.RelayInfo.CurrentLoad++;
            relayNode.RelayInfo.ConnectedNodeIds.Add(cgnatNode.Id);

            // Save both nodes
            await _dataStore.SaveNodeAsync(cgnatNode);
            await _dataStore.SaveNodeAsync(relayNode);

            _logger.LogInformation(
                "Assigned CGNAT node {CgnatId} to relay {RelayId} " +
                "(Tunnel IP: {TunnelIp}, Relay load: {Load}/{Capacity})",
                cgnatNode.Id, relayNode.Id, tunnelIp,
                relayNode.RelayInfo.CurrentLoad, relayNode.RelayInfo.MaxCapacity);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to assign CGNAT node {CgnatId} to relay {RelayId}",
                cgnatNode.Id, relayNode.Id);
            return false;
        }
    }

    /// <summary>
    /// Allocate tunnel IP for CGNAT node within relay's subnet
    /// Format: 10.20.{relaySubnet}.{2-253}
    /// </summary>
    private string AllocateTunnelIp(Node relayNode)
    {
        if (relayNode.RelayInfo == null)
        {
            throw new InvalidOperationException("Relay node has no RelayInfo");
        }

        var relaySubnet = relayNode.RelayInfo.RelaySubnet;
        if (relaySubnet == 0)
        {
            // Fallback for old relays without subnet assigned
            relaySubnet = 0;
            _logger.LogWarning(
                "Relay {RelayId} has no subnet assigned, using subnet 0",
                relayNode.Id);
        }

        // IPs in relay's subnet:
        // .1 = orchestrator peer on this relay's subnet (not used)
        // .254 = relay VM gateway
        // .2 - .253 = available for CGNAT nodes (252 IPs)
        var hostId = relayNode.RelayInfo.CurrentLoad + 2;

        if (hostId > 253)
        {
            throw new InvalidOperationException(
            $"Relay {relayNode.Id} (subnet 10.20.{relaySubnet}.0/24) has reached capacity " +
            $"(252 CGNAT nodes per relay, currently: {relayNode.RelayInfo.CurrentLoad})");
        }

        var tunnelIp = $"10.20.{relaySubnet}.{hostId}";

        _logger.LogDebug(
            "Allocated tunnel IP {TunnelIp} for CGNAT node on relay {RelayId} (subnet {Subnet}, load {Load})",
            tunnelIp, relayNode.Id, relaySubnet, relayNode.RelayInfo.CurrentLoad);

        return tunnelIp;
    }

    private async Task<string> GenerateWireGuardConfigAsync(
        Node cgnatNode,
        Node relayNode,
        string tunnelIp,
        CancellationToken ct = default)
    {
        try
        {
            // ========================================
            // STEP 1: Validate relay has a public key
            // ========================================
            if (relayNode.RelayInfo == null)
            {
                throw new InvalidOperationException(
                    $"Relay node {relayNode.Id} has no RelayInfo configured");
            }

            if (string.IsNullOrWhiteSpace(relayNode.RelayInfo.WireGuardPublicKey))
            {
                throw new InvalidOperationException(
                    $"Relay node {relayNode.Id} is missing WireGuard public key. " +
                    "This relay may have been created before the WireGuard key generation fix. " +
                    "Please redeploy the relay VM or manually configure its WireGuard public key.");
            }

            // ========================================
            // STEP 2: Generate private key for CGNAT node
            // ========================================
            var privateKey = await GenerateWireGuardPrivateKeyAsync(ct);

            // ========================================
            // STEP 3: Build configuration with real keys
            // ========================================
            // Determine VM network interface (default to virbr0 for libvirt)
            const string vmNetworkInterface = "virbr0";

            var config = $@"[Interface]
PrivateKey = {privateKey}
Address = {tunnelIp}/24
DNS = 8.8.8.8

# Enable IP forwarding for routing traffic between tunnel and VMs
PostUp = sysctl -w net.ipv4.ip_forward=1
PostUp = sysctl -w net.ipv4.conf.all.forwarding=1

# Allow forwarding from WireGuard tunnel to VM network
PostUp = iptables -A FORWARD -i %i -o {vmNetworkInterface} -j ACCEPT
PostUp = iptables -A FORWARD -i {vmNetworkInterface} -o %i -j ACCEPT

# NAT for traffic between tunnel and VM network
PostUp = iptables -t nat -A POSTROUTING -o {vmNetworkInterface} -j MASQUERADE

# Cleanup rules on shutdown (with error suppression)
PreDown = iptables -D FORWARD -i %i -o {vmNetworkInterface} -j ACCEPT 2>/dev/null || true
PreDown = iptables -D FORWARD -i {vmNetworkInterface} -o %i -j ACCEPT 2>/dev/null || true
PreDown = iptables -t nat -D POSTROUTING -o {vmNetworkInterface} -j MASQUERADE 2>/dev/null || true

[Peer]
PublicKey = {relayNode.RelayInfo.WireGuardPublicKey}
Endpoint = {relayNode.RelayInfo.WireGuardEndpoint}
AllowedIPs = 10.20.0.0/16
PersistentKeepalive = 25";

            _logger.LogInformation(
                "Generated WireGuard config for CGNAT node {CgnatId} → Relay {RelayId} " +
                "(Tunnel IP: {TunnelIp}, VM Network: {VmNetwork}, Routing: ENABLED)",
                cgnatNode.Id, relayNode.Id, tunnelIp, vmNetworkInterface);

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to generate WireGuard config for CGNAT node {CgnatId}",
                cgnatNode.Id);
            throw;
        }
    }


    /// <summary>
    /// Generate a WireGuard private key using the wg command
    /// </summary>
    private async Task<string> GenerateWireGuardPrivateKeyAsync(CancellationToken ct)
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wg",
                    Arguments = "genkey",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to generate WireGuard private key: {error}");
            }

            return output.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute 'wg genkey'");
            throw new InvalidOperationException(
                "WireGuard tools not available. Install: apt install wireguard-tools", ex);
        }
    }

    /// <summary>
    /// Derive public key from private key
    /// </summary>
    private async Task<string> DerivePublicKeyAsync(string privateKey, CancellationToken ct)
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sh",
                    Arguments = $"-c \"echo '{privateKey}' | wg pubkey\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to derive WireGuard public key: {error}");
            }

            return output.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to derive public key");
            throw;
        }
    }

    /// <summary>
    /// Register CGNAT node with relay VM's WireGuard server
    /// Calls the relay VM's API to add the node as a peer
    /// </summary>
    private async Task<bool> RegisterCgnatNodeWithRelayAsync(
        Node cgnatNode,
        Node relayNode,
        string tunnelIp,
        CancellationToken ct = default)
    {
        try
        {
            // Get CGNAT node's WireGuard public key from config
            var wgConfig = cgnatNode.CgnatInfo?.WireGuardConfig;
            if (string.IsNullOrEmpty(wgConfig))
            {
                _logger.LogError(
                    "Cannot register CGNAT node {NodeId}: No WireGuard config",
                    cgnatNode.Id);
                return false;
            }

            // Extract public key from config
            var publicKeyMatch = System.Text.RegularExpressions.Regex.Match(
                wgConfig,
                @"PrivateKey\s*=\s*([A-Za-z0-9+/=]+)");

            if (!publicKeyMatch.Success)
            {
                _logger.LogError(
                    "Cannot extract WireGuard key from CGNAT node {NodeId} config",
                    cgnatNode.Id);
                return false;
            }

            // Generate public key from private key
            var privateKey = publicKeyMatch.Groups[1].Value.Trim();
            var publicKey = await DerivePublicKeyAsync(privateKey, ct);

            // Call relay VM's API to add peer
            // Use relay's actual tunnel IP from RelayInfo
            var relayTunnelIp = relayNode.RelayInfo?.TunnelIp ?? "10.20.0.254";
            var relayApiUrl = $"http://{relayTunnelIp}:8080/api/relay/add-peer";

            var payload = new
            {
                public_key = publicKey,
                tunnel_ip = tunnelIp,
                allowed_ips = $"{tunnelIp}/32",
                persistent_keepalive = 25,
                description = $"CGNAT node {cgnatNode.Name} ({cgnatNode.Id})"
            };

            _logger.LogInformation(
                "Registering CGNAT node {NodeId} with relay {RelayId} at {ApiUrl}",
                cgnatNode.Id, relayNode.Id, relayApiUrl);

            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await httpClient.PostAsync(relayApiUrl, content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "✓ CGNAT node {NodeId} registered with relay {RelayId}",
                    cgnatNode.Id, relayNode.Id);
                return true;
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Failed to register CGNAT node {NodeId} with relay {RelayId}: {Error}",
                    cgnatNode.Id, relayNode.Id, error);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error registering CGNAT node {NodeId} with relay {RelayId}",
                cgnatNode.Id, relayNode.Id);
            return false;
        }
    }
}