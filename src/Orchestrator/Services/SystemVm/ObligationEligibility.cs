using Orchestrator.Models;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Determines which system VM roles a node is obligated to run,
/// based on its hardware capabilities.
///
/// Called during registration to seed the initial obligation list, and by
/// the reconciliation loop to detect missing obligations on legacy nodes
/// or nodes whose capabilities have changed since registration.
/// </summary>
public static class ObligationEligibility
{
    // Relay eligibility thresholds (mirrors RelayNodeService constants)
    private const int MinRelayCores = 2;
    private const long MinRelayRam = 4L * 1024 * 1024 * 1024;   // 4 GB
    private const long MinRelayBandwidth = 50_000_000;            // 50 Mbps

    // BlockStore eligibility thresholds
    private const long MinBlockStoreStorage = 100L * 1024 * 1024 * 1024;  // 100 GB
    private const long MinBlockStoreRam = 4L * 1024 * 1024 * 1024;        // 4 GB

    public static List<SystemVmRole> ComputeObligations(Node node)
    {
        var roles = new List<SystemVmRole>();

        // Every node gets DHT — the discovery layer is universal
        roles.Add(SystemVmRole.Dht);

        bool hasPublicIp = node.HardwareInventory.Network.NatType == NatType.None;
        bool hasMinRelayCpu = node.TotalResources.ComputePoints >= MinRelayCores;
        bool hasMinRelayRam = node.HardwareInventory.Memory.TotalBytes >= MinRelayRam;
        bool hasMinBandwidth = (node.HardwareInventory.Network.BandwidthBitsPerSecond ?? 0) >= MinRelayBandwidth;

        if (hasPublicIp && hasMinRelayCpu && hasMinRelayRam && hasMinBandwidth)
        {
            roles.Add(SystemVmRole.Relay);
            // TODO: Ingress obligation disabled until DeployIngressVmAsync is implemented.
            // Enabling it now causes an infinite retry loop in the reconciliation service.
            // roles.Add(SystemVmRole.Ingress);
        }

        // TODO: BlockStore obligation disabled until DeployBlockStoreVmAsync is implemented.
        // Enabling it now causes an infinite retry loop in the reconciliation service.
        // var totalStorage = node.HardwareInventory.Storage.Sum(s => s.TotalBytes);
        // bool hasStorage = totalStorage >= MinBlockStoreStorage;
        // bool hasBlockStoreRam = node.HardwareInventory.Memory.TotalBytes >= MinBlockStoreRam;
        //
        // if (hasStorage && hasBlockStoreRam)
        //     roles.Add(SystemVmRole.BlockStore);

        return roles;
    }
}
