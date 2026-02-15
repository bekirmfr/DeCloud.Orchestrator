using System.Security.Cryptography;
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
    public const int DhtListenPort = 4001;

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
            // Fetch all nodes once — used by both bootstrap peer collection
            // and WireGuard label resolution, avoiding redundant DB queries.
            // ========================================
            var allNodes = await _dataStore.GetAllNodesAsync();

            // ========================================
            // STEP 1: Collect bootstrap peers from existing DHT nodes
            // ========================================
            var bootstrapPeers = CollectBootstrapPeers(allNodes, excludeNodeId: node.Id);

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

            // ========================================
            // STEP 3b: Resolve WireGuard relay info for mesh enrollment
            // ========================================
            var wgLabels = ResolveWireGuardLabels(node, allNodes);

            // Override advertise IP with WG tunnel IP for mesh connectivity
            // This way DHT VMs communicate directly via WireGuard mesh,
            // no host-level port forwarding needed.
            if (wgLabels.TryGetValue("wg-tunnel-ip", out var wgTunnelIp))
            {
                advertiseIp = wgTunnelIp.Split('/')[0]; // Strip CIDR: "10.20.1.202/24" → "10.20.1.202"
                _logger.LogInformation(
                    "DHT VM on node {NodeId} will advertise WireGuard tunnel IP {TunnelIp}",
                    node.Id, advertiseIp);
            }

            // ========================================
            // STEP 3a: Generate auth token for DHT VM → orchestrator authentication
            // ========================================
            var authToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

            var labels = new Dictionary<string, string>
            {
                { "role", "dht" },
                { "dht-listen-port", DhtListenPort.ToString() },
                { "dht-api-port", DhtApiPort.ToString() },
                { "dht-advertise-ip", advertiseIp },
                { "dht-bootstrap-peers", string.Join(",", bootstrapPeers) },
                { "dht-auth-token", authToken },
                { "node-region", node.Region ?? "default" },
                { "node-id", node.Id },
                { "architecture", node.Architecture ?? "x86_64" }
            };

            // Merge WireGuard labels if relay is available
            foreach (var (key, value) in wgLabels)
                labels[key] = value;

            var dhtVm = await vmService.CreateVmAsync(
                userId: "system",
                request: new CreateVmRequest
                (
                    Name: vmName,
                    Spec: vmSpec,
                    VmType: VmType.Dht,
                    NodeId: node.Id,
                    Labels: labels
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

            // Store auth token on the DHT obligation for /api/dht/join verification
            var dhtObligation = node.SystemVmObligations
                .FirstOrDefault(o => o.Role == SystemVmRole.Dht);
            if (dhtObligation != null)
                dhtObligation.AuthToken = authToken;

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

    /// <summary>
    /// Resolve WireGuard relay configuration for a DHT VM using a pre-fetched node list.
    /// Returns labels to pass through to cloud-init via the VM spec.
    ///
    /// Public IP nodes: relay is co-located on the same node.
    /// CGNAT nodes: relay info from CgnatInfo (registered during WireGuard enrollment).
    /// </summary>
    private Dictionary<string, string> ResolveWireGuardLabels(Node node, IEnumerable<Node> allNodes)
    {
        var labels = new Dictionary<string, string>();

        try
        {
            // For CGNAT nodes: look up relay via AssignedRelayNodeId
            if (node.IsBehindCgnat && node.CgnatInfo != null)
            {
                var relayNode = allNodes.FirstOrDefault(n =>
                    n.Id == node.CgnatInfo.AssignedRelayNodeId &&
                    n.RelayInfo != null);

                if (relayNode?.RelayInfo != null)
                {
                    var relay = relayNode.RelayInfo;
                    labels["wg-relay-endpoint"] = relay.WireGuardEndpoint;
                    labels["wg-relay-pubkey"] = relay.WireGuardPublicKey ?? "";
                    labels["wg-relay-api"] = $"http://{relayNode.PublicIp}:8080";

                    // DHT VM gets a unique tunnel IP derived from CGNAT host's last octet
                    // Host .2 → DHT VM .202, Host .3 → .203, etc. (avoids collision with other hosts/VMs)
                    var tunnelIp = node.CgnatInfo.TunnelIp;
                    if (!string.IsNullOrEmpty(tunnelIp))
                    {
                        var parts = tunnelIp.Split('.');
                        if (parts.Length == 4 && int.TryParse(parts[3], out var hostOctet))
                        {
                            var dhtOctet = 200 + hostOctet;
                            if (dhtOctet > 253)
                            {
                                _logger.LogWarning(
                                    "DHT VM on CGNAT node {NodeId}: host octet {HostOctet} produces " +
                                    "DHT octet {DhtOctet} (>253) — skipping WireGuard mesh enrollment",
                                    node.Id, hostOctet, dhtOctet);
                            }
                            else
                            {
                                labels["wg-tunnel-ip"] = $"{parts[0]}.{parts[1]}.{parts[2]}.{dhtOctet}/24";
                            }
                        }
                    }

                    _logger.LogInformation(
                        "DHT VM on CGNAT node {NodeId}: relay={Endpoint}, tunnelIp={TunnelIp}",
                        node.Id, labels["wg-relay-endpoint"],
                        labels.GetValueOrDefault("wg-tunnel-ip", "(missing)"));
                    return labels;
                }

                _logger.LogWarning(
                    "CGNAT node {NodeId} relay {RelayNodeId} not found — DHT VM will have no mesh",
                    node.Id, node.CgnatInfo.AssignedRelayNodeId);
                return labels;
            }

            // For public IP nodes: find the relay VM running on this node
            var localRelay = allNodes.FirstOrDefault(n =>
                n.RelayInfo != null &&
                n.Id == node.Id);

            if (localRelay?.RelayInfo != null)
            {
                var relay = localRelay.RelayInfo;
                labels["wg-relay-endpoint"] = relay.WireGuardEndpoint;
                labels["wg-relay-pubkey"] = relay.WireGuardPublicKey ?? "";
                labels["wg-relay-api"] = $"http://{node.PublicIp}:8080";
                // Co-located DHT VM gets .199 in relay's subnet (relay is .254)
                labels["wg-tunnel-ip"] = $"10.20.{relay.RelaySubnet}.199/24";

                _logger.LogInformation(
                    "DHT VM on public node {NodeId}: relay={Endpoint}, subnet={Subnet}",
                    node.Id, relay.WireGuardEndpoint, relay.RelaySubnet);
                return labels;
            }

            // No relay found — try to find any active relay in the same region
            var regionRelay = allNodes
                .Where(n => n.RelayInfo != null &&
                            n.Status == NodeStatus.Online &&
                            n.Region == node.Region)
                .FirstOrDefault();

            if (regionRelay?.RelayInfo != null)
            {
                var relay = regionRelay.RelayInfo;
                labels["wg-relay-endpoint"] = relay.WireGuardEndpoint;
                labels["wg-relay-pubkey"] = relay.WireGuardPublicKey ?? "";
                labels["wg-relay-api"] = $"http://{regionRelay.PublicIp}:8080";
                // Regional fallback DHT VM gets .198 (avoids .199 used by co-located)
                labels["wg-tunnel-ip"] = $"10.20.{relay.RelaySubnet}.198/24";

                _logger.LogInformation(
                    "DHT VM on node {NodeId}: using regional relay on {RelayNode}, subnet={Subnet}",
                    node.Id, regionRelay.Id, relay.RelaySubnet);
                return labels;
            }

            _logger.LogWarning(
                "No relay found for DHT VM on node {NodeId} — VM will boot without WireGuard mesh",
                node.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving WireGuard labels for DHT VM on node {NodeId}", node.Id);
        }

        return labels;
    }
}
