using Orchestrator.Background;

namespace Orchestrator.Models;

/// <summary>
/// Standard specification for relay VMs
/// </summary>
public static class RelayVmSpec
{
    /// <summary>
    /// Standard relay VM resource allocation
    /// </summary>
    public static VmSpec Standard => new()
    {
        VmType = VmType.Relay,
        VirtualCpuCores = 1,
        MemoryBytes = 1024L * 1024 * 1024, // 1GB
        DiskBytes = 10L * 1024 * 1024 * 1024, // 10GB
        QualityTier = QualityTier.Balanced,
        ImageId = "ubuntu-24.04-relay", // Custom relay image
        ComputePointCost = 2, // Minimal cost
    };

    /// <summary>
    /// High-capacity relay for busy regions
    /// </summary>
    public static VmSpec High => new()
    {
        VirtualCpuCores = 2,
        MemoryBytes = 2L * 1024 * 1024 * 1024, // 2GB
        DiskBytes = 20L * 1024 * 1024 * 1024, // 20GB
        QualityTier = QualityTier.Balanced,
        ImageId = "ubuntu-24.04-relay",
        ComputePointCost = 11,
    };

    /// <summary>
    /// Optimized for premium relay workloads.
    /// </summary>
    public static VmSpec Premium => new()
    {
        VmType = VmType.Relay,
        VirtualCpuCores = 2,
        MemoryBytes = 3L * 1024 * 1024 * 1024, // 3GB
        DiskBytes = 20L * 1024 * 1024 * 1024,
        QualityTier = QualityTier.Standard, // Upgraded tier
        ImageId = "ubuntu-24.04-relay",
        ComputePointCost = 20, // Calculated: 4 × (4.0/1.6) × 2 = 20
    };

    /// <summary>
    /// Relay image packages and configuration
    /// </summary>
    public static class RelayImage
    {
        public const string BaseImage = "ubuntu:24.04";

        public static readonly string[] RequiredPackages = new[]
        {
            "wireguard",
            "wireguard-tools",
            "nginx",
            "iptables",
            "curl",
            "net-tools"
        };

        public static readonly string[] Services = new[]
        {
            "wireguard@wg0",
            "nginx",
            "decloud-relay-monitor"
        };
    }
}