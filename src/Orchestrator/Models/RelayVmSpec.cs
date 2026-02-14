namespace Orchestrator.Models;

/// <summary>
/// Standard specification for relay VMs
/// </summary>
public static class RelayVmSpec
{
    /// <summary>
    /// Basic relay VM resource allocation
    /// </summary>
    public static VmSpec Basic => new()
    {
        VmType = VmType.Relay,
        VirtualCpuCores = 1,
        MemoryBytes = 1024L * 1024 * 1024,
        DiskBytes = 5L * 1024 * 1024 * 1024,
        QualityTier = QualityTier.Burstable,
        ImageId = "debian-12-relay", // Custom relay image
        ComputePointCost = 1, // Minimal cost
        MaxConnections = 20,
    };

    /// <summary>
    /// Standard relay VM resource allocation
    /// </summary>
    public static VmSpec Standard => new()
    {
        VmType = VmType.Relay,
        VirtualCpuCores = 1,
        MemoryBytes = 2L * 1024 * 1024 * 1024,
        DiskBytes = 6L * 1024 * 1024 * 1024,
        QualityTier = QualityTier.Balanced,
        ImageId = "debian-12-relay", // Custom relay image
        ComputePointCost = 2, // Minimal cost
        MaxConnections = 40,
    };

    /// <summary>
    /// High-capacity relay for busy regions
    /// </summary>
    public static VmSpec High => new()
    {
        VirtualCpuCores = 2,
        MemoryBytes = 3L * 1024 * 1024 * 1024,
        DiskBytes = 7L * 1024 * 1024 * 1024,
        QualityTier = QualityTier.Balanced,
        ImageId = "debian-12-relay",
        ComputePointCost = 4,
        MaxConnections = 80,
    };

    /// <summary>
    /// Optimized for premium relay workloads.
    /// </summary>
    public static VmSpec Premium => new()
    {
        VmType = VmType.Relay,
        VirtualCpuCores = 2,
        MemoryBytes = 4L * 1024 * 1024 * 1024,
        DiskBytes = 8L * 1024 * 1024 * 1024,
        QualityTier = QualityTier.Standard, // Upgraded tier
        ImageId = "debian-12-relay",
        ComputePointCost = 13,
        MaxConnections = 100,
    };

}