using Orchestrator.Data;
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
    private readonly ILogger<RelayNodeService> _logger;

    // Criteria for relay eligibility
    private const int MIN_CORES_FOR_RELAY = 16;
    private const long MIN_RAM_FOR_RELAY = 32L * 1024 * 1024 * 1024; // 32GB
    private const long MIN_BANDWIDTH_FOR_RELAY = 100L * 1024 * 1024; // 100 Mbps

    public RelayNodeService(
        DataStore dataStore,

        ILogger<RelayNodeService> logger)
    {
        _dataStore = dataStore;
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
        if (node.TotalResources.ComputePoints < MIN_CORES_FOR_RELAY * 1000)
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
        if (bandwidth.HasValue && bandwidth.Value < MIN_BANDWIDTH_FOR_RELAY * 8)
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
            // Create relay VM specification
            var vmSpec = RelayVmSpec.Standard;

            // Determine capacity based on node resources
            var maxCapacity = CalculateRelayCapacity(node);

            // Create relay VM
            var relayVm = await vmService.CreateVmAsync(
                userId: "system", // System-owned VM
                request: new CreateVmRequest
                (
                    Name: $"relay-{node.Region}-{node.Id[..8]}",
                    Spec: vmSpec,
                    Labels: new Dictionary<string, string>
                    {
                        { "role", "relay" },
                        { "user", "system" }
                    }
                )
            );

            // Initialize relay configuration
            node.RelayInfo = new RelayNodeInfo
            {
                IsActive = true,
                RelayVmId = relayVm.VmId,
                WireGuardEndpoint = $"{node.PublicIp}:51820",
                MaxCapacity = maxCapacity,
                CurrentLoad = 0,
                Region = node.Region ?? "default",
                Status = RelayStatus.Active,
                LastHealthCheck = DateTime.UtcNow
            };

            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "Relay VM {VmId} deployed on node {NodeId} with capacity {Capacity}",
                relayVm.VmId, node.Id, maxCapacity);

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
    /// Calculate relay capacity based on node resources
    /// </summary>
    private int CalculateRelayCapacity(Node node)
    {
        // Base capacity on available CPU cores and RAM
        var cpuCapacity = node.TotalResources.ComputePoints / 1000 / 4; // 1 CGNAT node per 4 cores
        var ramCapacity = (int)(node.HardwareInventory.Memory.TotalBytes / (4L * 1024 * 1024 * 1024)); // 1 per 4GB

        var capacity = Math.Min(cpuCapacity, ramCapacity);

        // Cap at reasonable maximum
        capacity = Math.Min(capacity, 100);

        // Minimum capacity
        capacity = Math.Max(capacity, 10);

        _logger.LogDebug(
            "Calculated relay capacity for node {NodeId}: {Capacity} " +
            "(CPU capacity: {CpuCap}, RAM capacity: {RamCap})",
            node.Id, capacity, cpuCapacity, ramCapacity);

        return capacity;
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
                PublicEndpoint = $"https://relay-{relayNode.Region}-{relayNode.Id[..8]}.decloud.io",
                TunnelStatus = TunnelStatus.Connecting,
                LastHandshake = null
            };

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

    private string AllocateTunnelIp(Node relayNode)
    {
        // Use relay node ID hash to create unique subnet
        var relayIdHash = Math.Abs(relayNode.Id.GetHashCode());
        var subnet = (relayIdHash % 200) + 1; // 10.20.1.0 - 10.20.200.0

        var currentLoad = relayNode.RelayInfo?.CurrentLoad ?? 0;
        var hostId = currentLoad + 2; // Start from .2 (.1 is relay itself)

        return $"10.20.{subnet}.{hostId}";
    }

    private async Task<string> GenerateWireGuardConfigAsync(
        Node cgnatNode,
        Node relayNode,
        string tunnelIp,
        CancellationToken ct = default)
    {
        // This would call the relay VM's WireGuard API to get the public key
        // For now, return a template

        var config = $@"[Interface]
PrivateKey = <CGNAT_NODE_PRIVATE_KEY>
Address = {tunnelIp}/24
DNS = 8.8.8.8

[Peer]
PublicKey = <RELAY_PUBLIC_KEY>
Endpoint = {relayNode.RelayInfo!.WireGuardEndpoint}
AllowedIPs = 10.20.0.0/16
PersistentKeepalive = 25";

        return config;
    }
}