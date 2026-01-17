using Orchestrator.Services;

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
        ImageId = "ubuntu-24.04-relay", // Custom relay image
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
        ImageId = "ubuntu-24.04-relay", // Custom relay image
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
        ImageId = "ubuntu-24.04-relay",
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
        ImageId = "ubuntu-24.04-relay",
        ComputePointCost = 13,
        MaxConnections = 100,
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