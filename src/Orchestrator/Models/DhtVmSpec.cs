namespace Orchestrator.Models;

/// <summary>
/// Standard specification for DHT (Distributed Hash Table) system VMs.
/// DHT VMs run a libp2p-based Kademlia node that provides peer discovery,
/// key-value storage, and GossipSub event propagation over the WireGuard overlay.
/// Every node in the network runs a DHT VM — it is the root of the system VM dependency graph.
/// </summary>
public static class DhtVmSpec
{
    /// <summary>
    /// Standard DHT node — sufficient for networks up to thousands of peers.
    /// libp2p + Kademlia + GossipSub uses ~100-150MB RAM at steady state.
    /// </summary>
    public static VmSpec Standard => new()
    {
        VmType = VmType.Dht,
        VirtualCpuCores = 1,
        MemoryBytes = 512L * 1024 * 1024,         // 512 MB
        DiskBytes = 2L * 1024 * 1024 * 1024,      // 2 GB
        QualityTier = QualityTier.Burstable,
        ImageId = "ubuntu-24.04-dht",
        ComputePointCost = 1,
    };

    /// <summary>
    /// DHT image packages and configuration.
    /// No Docker — the Go binary is embedded as base64 in cloud-init
    /// and runs directly via systemd.
    /// </summary>
    public static class DhtImage
    {
        public const string BaseImage = "ubuntu:24.04";

        public static readonly string[] RequiredPackages =
        [
            "qemu-guest-agent",
            "curl",
            "jq",
            "net-tools",
            "openssl"
        ];

        public static readonly string[] Services =
        [
            "decloud-dht",
            "decloud-dht-callback"
        ];
    }
}
