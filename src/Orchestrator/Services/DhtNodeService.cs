using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Manages DHT (Distributed Hash Table) system VM deployment.
/// DHT VMs run a libp2p Kademlia node providing peer discovery,
/// key-value storage, and GossipSub event propagation.
/// </summary>
public interface IDhtNodeService
{
    /// <summary>
    /// Deploy a DHT VM on the given node. Returns the VM ID on success, null on failure.
    /// </summary>
    Task<string?> DeployDhtVmAsync(Node node, IVmService vmService, CancellationToken ct = default);

    /// <summary>
    /// Collect bootstrap peer multiaddrs from nodes with Active DHT VMs.
    /// Returns addresses like "/ip4/{ip}/tcp/4001/p2p/{peerId}".
    /// </summary>
    Task<List<string>> GetBootstrapPeersAsync(string? excludeNodeId = null);
}

public class DhtNodeService : IDhtNodeService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<DhtNodeService> _logger;

    /// <summary>
    /// libp2p listen port inside the DHT VM. The node agent forwards
    /// traffic from the host's overlay/public IP on this port to the VM.
    /// </summary>
    private const int DhtListenPort = 4001;

    /// <summary>
    /// HTTP API port exposed by the DHT VM on localhost for the node agent to query.
    /// </summary>
    private const int DhtApiPort = 5080;

    public DhtNodeService(
        DataStore dataStore,
        ILogger<DhtNodeService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    public async Task<string?> DeployDhtVmAsync(Node node, IVmService vmService, CancellationToken ct = default)
    {
        _logger.LogInformation("Deploying DHT VM on node {NodeId}", node.Id);

        try
        {
            // ========================================
            // STEP 1: Collect bootstrap peers from existing DHT nodes
            // ========================================
            var bootstrapPeers = await GetBootstrapPeersAsync(excludeNodeId: node.Id);

            _logger.LogInformation(
                "DHT VM on node {NodeId} will bootstrap from {PeerCount} peers",
                node.Id, bootstrapPeers.Count);

            // ========================================
            // STEP 2: Determine the address other nodes will use to reach this DHT VM
            // ========================================
            var advertiseIp = GetAdvertiseIp(node);

            var vmName = $"dht-{node.Region ?? "default"}-{node.Id[..8]}";

            // ========================================
            // STEP 3: Create DHT VM — NodeAgent owns the cloud-init template
            // Orchestrator passes config via Labels, NodeAgent renders locally
            // ========================================
            var vmSpec = DhtVmSpec.Standard;

            var dhtVm = await vmService.CreateVmAsync(
                userId: "system",
                request: new CreateVmRequest
                (
                    Name: vmName,
                    Spec: vmSpec,
                    VmType: VmType.Dht,
                    NodeId: node.Id,
                    Labels: new Dictionary<string, string>
                    {
                        { "role", "dht" },
                        { "dht-listen-port", DhtListenPort.ToString() },
                        { "dht-api-port", DhtApiPort.ToString() },
                        { "dht-advertise-ip", advertiseIp },
                        { "dht-bootstrap-peers", string.Join(",", bootstrapPeers) },
                        { "node-region", node.Region ?? "default" },
                        { "node-id", node.Id },
                        { "architecture", node.Architecture ?? "x86_64" }
                    }
                ),
                node.Id
            );

            // ========================================
            // STEP 4: Store DHT info on the node
            // ========================================
            node.DhtInfo = new DhtNodeInfo
            {
                DhtVmId = dhtVm.VmId,
                ListenAddress = $"{advertiseIp}:{DhtListenPort}",
                ApiPort = DhtApiPort,
                BootstrapPeerCount = bootstrapPeers.Count,
                Status = DhtStatus.Initializing,
            };

            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "DHT VM {VmId} deployed on node {NodeId} (advertise: {Addr}, bootstrap peers: {Count}, arch: {Arch})",
                dhtVm.VmId, node.Id, node.DhtInfo.ListenAddress, bootstrapPeers.Count,
                node.Architecture ?? "x86_64");

            return dhtVm.VmId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy DHT VM on node {NodeId}", node.Id);
            return null;
        }
    }

    public async Task<List<string>> GetBootstrapPeersAsync(string? excludeNodeId = null)
    {
        var peers = new List<string>();
        var nodes = await _dataStore.GetAllNodesAsync();

        foreach (var node in nodes)
        {
            if (node.Id == excludeNodeId) continue;
            if (node.Status != NodeStatus.Online) continue;

            var dhtObligation = node.SystemVmObligations
                .FirstOrDefault(o => o.Role == SystemVmRole.Dht && o.Status == SystemVmStatus.Active);

            if (dhtObligation == null) continue;
            if (node.DhtInfo == null || string.IsNullOrEmpty(node.DhtInfo.PeerId)) continue;

            var ip = GetAdvertiseIp(node);
            // libp2p multiaddr format
            peers.Add($"/ip4/{ip}/tcp/{DhtListenPort}/p2p/{node.DhtInfo.PeerId}");
        }

        return peers;
    }

    /// <summary>
    /// Determine the IP that other nodes use to reach this node's DHT VM.
    /// Public nodes: use public IP.
    /// CGNAT nodes: use WireGuard tunnel IP (reachable via overlay).
    /// </summary>
    private static string GetAdvertiseIp(Node node)
    {
        if (node.IsBehindCgnat && node.CgnatInfo != null && !string.IsNullOrEmpty(node.CgnatInfo.TunnelIp))
            return node.CgnatInfo.TunnelIp;

        return node.PublicIp;
    }
}
