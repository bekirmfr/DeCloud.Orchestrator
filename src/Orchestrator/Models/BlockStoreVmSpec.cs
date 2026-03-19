using Orchestrator.Models;

namespace Orchestrator.Models;

/// <summary>
/// Resource specification for Block Store system VMs.
///
/// Every eligible node contributes 5% of its total storage as a network duty.
/// This is a fixed obligation — not configurable per node.
///
/// Resource profile: lightweight (1 vCPU, 512 MB RAM) because disk I/O is
/// the primary resource. libp2p + bitswap + FlatFS runs at ~150-250 MB RAM
/// at steady state. Burstable tier is appropriate — the bitswap workload
/// is bursty (spike when peers request blocks) with long idle periods.
/// </summary>
public static class BlockStoreVmSpec
{
    /// <summary>5% of total node storage is allocated to the block store duty.</summary>
    public const double StorageDutyFraction = 0.05;

    /// <summary>Minimum node storage to be eligible for the block store obligation.</summary>
    public const long MinNodeStorageBytes = 100L * 1024 * 1024 * 1024;  // 100 GB

    /// <summary>Minimum node RAM to be eligible for the block store obligation.</summary>
    public const long MinNodeRamBytes = 2L * 1024 * 1024 * 1024;  // 2 GB

    /// <summary>libp2p bitswap port (exposed on the WireGuard mesh interface).</summary>
    public const int BitswapPort = 5001;

    /// <summary>Localhost HTTP API port (node agent → block store VM queries, internal only).</summary>
    public const int ApiPort = 5090;

    /// <summary>
    /// Create a VmSpec for a block store VM on a node with the given total storage.
    /// DiskBytes is dynamically calculated as 5% of the node's total storage.
    /// </summary>
    public static VmSpec Create(long nodeStorageTotalBytes) => new()
    {
        VmType = VmType.BlockStore,
        VirtualCpuCores = 1,
        MemoryBytes = 512L * 1024 * 1024,                               // 512 MB
        DiskBytes = (long)(nodeStorageTotalBytes * StorageDutyFraction), // 5% duty
        QualityTier = QualityTier.Burstable,
        ImageId = "debian-12-blockstore",
        ComputePointCost = 1,
    };
}