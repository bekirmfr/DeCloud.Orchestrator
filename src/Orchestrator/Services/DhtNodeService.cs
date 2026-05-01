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
    public const int DhtListenPort = 4001;

    /// <summary>
    /// HTTP API port exposed externally by the DHT VM via nginx proxy.
    /// The DHT binary binds to 127.0.0.1:5080 internally; nginx forwards port 80.
    /// Used by the orchestrator for health checks.
    /// </summary>
    public const int DhtApiPort = 80;

    /// <summary>
    /// Internal HTTP API port the DHT binary binds to (localhost only).
    /// Used by cloud-init scripts inside the VM (bootstrap-poll, dashboard, health-check).
    /// Must stay 5080 — changing this breaks the VM's internal service wiring.
    /// </summary>
    private const int DhtInternalApiPort = 5080;

    public DhtNodeService(
        DataStore dataStore,
        ILogger<DhtNodeService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    /// <summary>
    /// Interface method: fetches nodes from DB then delegates to the pure collector.
    /// Used by DhtController.DhtJoin where no pre-fetched node list is available.
    /// </summary>
    public async Task<List<string>> GetBootstrapPeersAsync(string? excludeNodeId = null)
    {
        var nodes = await _dataStore.GetAllNodesAsync();
        return CollectBootstrapPeers(nodes, excludeNodeId);
    }

    /// <summary>
    /// Pure function: collect bootstrap peers from a pre-fetched node list.
    /// Avoids redundant GetAllNodesAsync() calls during deployment.
    /// </summary>
    private static List<string> CollectBootstrapPeers(IEnumerable<Node> nodes, string? excludeNodeId = null)
    {
        var peers = new List<string>();

        foreach (var node in nodes)
        {
            if (node.Id == excludeNodeId) continue;
            if (node.Status != NodeStatus.Online) continue;

            var dhtObligation = node.SystemVmObligations
                .FirstOrDefault(o => o.Role == SystemVmRole.Dht && o.Status == SystemVmStatus.Active);

            if (dhtObligation == null) continue;
            if (node.DhtInfo == null || string.IsNullOrEmpty(node.DhtInfo.PeerId)) continue;

            // Use ListenAddress which contains the WG tunnel IP the DHT node advertises
            var ip = node.DhtInfo.ListenAddress?.Split(':')[0] ?? GetAdvertiseIp(node);
            // libp2p multiaddr format
            peers.Add($"/ip4/{ip}/tcp/{DhtListenPort}/p2p/{node.DhtInfo.PeerId}");
        }

        return peers;
    }

    /// <summary>
    /// Determine the IP that other nodes use to reach this node's DHT VM.
    /// Public nodes: use public IP.
    /// CGNAT nodes: use WireGuard tunnel IP (reachable via overlay).
    /// Public so reconciliation and heartbeat processing can detect stale addresses.
    /// </summary>
    public static string GetAdvertiseIp(Node node)
    {
        if (node.IsBehindCgnat && node.CgnatInfo != null && !string.IsNullOrEmpty(node.CgnatInfo.TunnelIp))
            return node.CgnatInfo.TunnelIp;

        return node.PublicIp;
    }
}
