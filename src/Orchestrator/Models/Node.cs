using Orchestrator.Background;

namespace Orchestrator.Models;

/// <summary>
/// Represents a compute node in the decentralized network
/// </summary>
public class Node
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Machine fingerprint - stable identifier for the physical hardware
    /// Used to generate deterministic node ID
    /// </summary>
    public required string MachineId { get; set; }
    public string WalletAddress { get; set; } = string.Empty;

    /// <summary>
    /// Pending payout to this node operator (in USDC)
    /// </summary>
    public decimal PendingPayout { get; set; }

    /// <summary>
    /// Total earned by this node (lifetime)
    /// </summary>
    public decimal TotalEarned { get; set; }

    /// <summary>
    /// CPU Architecture: x86_64, aarch64, etc.
    /// CRITICAL: Used for VM scheduling compatibility
    /// </summary>
    public string Architecture { get; set; } = "x86_64"; // Default for backward compatibility
    public string? ApiKeyHash { get; set; }
    public DateTime? ApiKeyCreatedAt { get; set; }
    public DateTime? ApiKeyLastUsedAt { get; set; }

    // Connection info
    public string PublicIp { get; set; } = string.Empty;
    public int AgentPort { get; set; } = 5050;

    // Resources
    public HardwareInventory HardwareInventory { get; set; } = new();
    /// <summary>
    /// Performance evaluation and tier eligibility
    /// Populated by NodePerformanceEvaluator on registration
    /// </summary>
    public NodePerformanceEvaluation? PerformanceEvaluation { get; set; }
    public ResourceSnapshot TotalResources { get; set; } = new();
    public ResourceSnapshot ReservedResources { get; set; } = new();

    // State
    public NodeStatus Status { get; set; } = NodeStatus.Offline;
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public string AgentVersion { get; set; } = string.Empty;

    // Capabilities
    public List<string> SupportedImages { get; set; } = new();
    public bool SupportsGpu { get; set; }
    public GpuInfo? GpuInfo { get; set; }
    /// <summary>
    /// Relay node configuration (null if node is not a relay)
    /// </summary>
    public RelayNodeInfo? RelayInfo { get; set; }

    /// <summary>
    /// CGNAT node configuration (null if node has public IP)
    /// </summary>
    public CgnatNodeInfo? CgnatInfo { get; set; }

    // Performance metrics
    public NodeMetrics? LatestMetrics { get; set; }

    // Reputation/Trust
    public double UptimePercentage { get; set; } = 100.0;
    public int TotalVmsHosted { get; set; }
    public int SuccessfulVmCompletions { get; set; }

    // Region/Location for scheduling
    public string Region { get; set; } = "default";
    public string Zone { get; set; } = "default";
}

/// <summary>
/// Complete hardware inventory of a node
/// </summary>
public class HardwareInventory
{
    public string NodeId { get; set; } = string.Empty;
    public CpuInfo Cpu { get; set; } = new();
    public MemoryInfo Memory { get; set; } = new();
    public List<StorageInfo> Storage { get; set; } = new();
    public bool SupportsGpu { get; set; }
    public List<GpuInfo> Gpus { get; set; } = new();
    public NetworkInfo Network { get; set; } = new();
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
}

public class CpuInfo
{
    public string Model { get; set; } = string.Empty;
    public int PhysicalCores { get; set; }
    public int LogicalCores { get; set; }
    public double FrequencyMhz { get; set; }
    public List<string> Flags { get; set; } = new(); // e.g., "vmx", "svm" for virtualization
    public bool SupportsVirtualization { get; set; }

    // Current utilization (0-100)
    public double UsagePercent { get; set; }

    // Available for VMs (considering overcommit ratio)
    public int AvailableVCpus { get; set; }
    /// <summary>
    /// CPU benchmark score (measured via sysbench or custom benchmark)
    /// Used for tier eligibility and capacity calculation
    /// Default: 1000 (baseline equivalent)
    /// </summary>
    public int BenchmarkScore { get; set; } = 1000;
}

public class MemoryInfo
{
    public long TotalBytes { get; set; }
    public long AvailableBytes { get; set; }
    public long UsedBytes { get; set; }

    // Reserved for host OS (configurable)
    public long ReservedBytes { get; set; }

    // Available for VM allocation
    public long AllocatableBytes => Math.Max(0, TotalBytes - ReservedBytes - UsedBytes);

    public double UsagePercent => TotalBytes > 0 ? (double)UsedBytes / TotalBytes * 100 : 0;
}

public class StorageInfo
{
    public string DevicePath { get; set; } = string.Empty;  // e.g., /dev/sda
    public string MountPoint { get; set; } = string.Empty;  // e.g., /var/lib/decloud
    public string FileSystem { get; set; } = string.Empty;  // e.g., ext4, xfs
    public StorageType Type { get; set; }
    public long TotalBytes { get; set; }
    public long AvailableBytes { get; set; }
    public long UsedBytes { get; set; }

    // Measured IOPS (optional, from benchmark)
    public int? ReadIops { get; set; }
    public int? WriteIops { get; set; }
}

public enum StorageType
{
    Unknown,
    HDD,
    SSD,
    NVMe
}

public class GpuInfo
{
    public string Vendor { get; set; } = string.Empty;      // NVIDIA, AMD, Intel
    public string Model { get; set; } = string.Empty;       // e.g., RTX 4090
    public string PciAddress { get; set; } = string.Empty;  // e.g., 0000:01:00.0
    public long MemoryBytes { get; set; }
    public long MemoryUsedBytes { get; set; }
    public string DriverVersion { get; set; } = string.Empty;

    // VFIO passthrough readiness
    public bool IsIommuEnabled { get; set; }
    public string IommuGroup { get; set; } = string.Empty;
    public bool IsAvailableForPassthrough { get; set; }

    // Current utilization
    public double GpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public int? TemperatureCelsius { get; set; }
}

public class NetworkInfo
{
    public string PublicIp { get; set; } = string.Empty;
    public string PrivateIp { get; set; } = string.Empty;
    public string WireGuardIp { get; set; } = string.Empty;
    public int? WireGuardPort { get; set; }

    // Bandwidth (measured via iperf3 or similar)
    public long? BandwidthBitsPerSecond { get; set; }

    // NAT type detection
    public NatType NatType { get; set; }

    public List<NetworkInterface> Interfaces { get; set; } = new();
}

public enum NatType
{
    Unknown,
    None,           // Public IP, no NAT
    FullCone,       // Easy to traverse
    RestrictedCone,
    PortRestricted,
    Symmetric       // Hardest to traverse, may need relay
}

public class NetworkInterface
{
    public string Name { get; set; } = string.Empty;       // e.g., eth0
    public string MacAddress { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public long SpeedMbps { get; set; }
    public bool IsUp { get; set; }
}

/// <summary>
/// Node resource allocation with point-based CPU tracking
/// </summary>
public class ResourceSnapshot
{
    public int ComputePoints { get; set; }
    public long MemoryBytes { get; set; }
    public long StorageBytes { get; set; }
}

public class NodeMetrics
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public double StorageUsagePercent { get; set; }
    public double NetworkInMbps { get; set; }
    public double NetworkOutMbps { get; set; }
    public int ActiveVmCount { get; set; }
    public double LoadAverage { get; set; }
}

public enum NodeStatus
{
    Offline,
    Online,
    Maintenance,
    Draining,    // No new VMs, existing VMs running
    Suspended    // Suspended for violations
}

// DTOs for API communication
public class NodeRegistrationRequest
{
    /// <summary>
    /// Machine fingerprint (from /etc/machine-id or fallback)
    /// Required for node ID generation and validation
    /// </summary>
    public required string MachineId { get; set; }
    public required string Name { get; set; }

    /// <summary>
    /// Wallet address for ownership and billing
    /// Required - cannot be null address (0x000...000)
    /// </summary>
    public required string WalletAddress { get; set; }

    public required string PublicIp { get; set; }
    public required int AgentPort { get; set; }
    public required HardwareInventory HardwareInventory { get; set; }
    public required string AgentVersion { get; set; }
    public List<string> SupportedImages { get; set; } = new();
    // Staking info
    public string StakingTxHash { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string? Zone { get; set; }

    /// <summary>
    /// Wallet signature proving ownership
    /// </summary>
    public required string Signature { get; set; }

    /// <summary>
    /// Message that was signed (includes node ID, wallet, timestamp)
    /// </summary>
    public required string Message { get; set; }
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
}

public record NodeRegistrationResponse(
    string NodeId,
    NodePerformanceEvaluation performanceEvaluation,
    string ApiKey,
    SchedulingConfig SchedulingConfig,
    /// <summary>
    /// Orchestrator's WireGuard public key for relay configuration
    /// Null if WireGuard is not enabled on orchestrator
    /// </summary>
    string OrchestratorWireGuardPublicKey,
    TimeSpan HeartbeatInterval
);

/// <summary>
/// Node heartbeat with enhanced VM information
/// </summary>
public record NodeHeartbeat(
    string NodeId,
    NodeMetrics Metrics,
    ResourceSnapshot AvailableResources,
    int SchedulingConfigVersion,
    List<HeartbeatVmInfo>? ActiveVms = null,  // detailed VM information
    CgnatNodeInfo? CgnatInfo = null
);

/// <summary>
/// VM information included in heartbeat
/// </summary>
public class HeartbeatVmInfo
{
    public string VmId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string State { get; set; } = string.Empty;  // "Running", "Stopped", etc.
    public string OwnerId { get; set; } = string.Empty;  // Owner ID
    public bool IsIpAssigned { get; set; } = false;
    public string? IpAddress { get; set; }
    /// <summary>
    /// MAC address assigned to VM's network interface
    /// </summary>
    public string? MacAddress { get; set; }
    public int? SshPort { get; set; } = 2222;
    // Complete recovery fields
    /// <summary>
    /// VNC port for console access (e.g., "5900")
    /// </summary>
    public int? VncPort { get; set; }

    // Resource specifications
    public int VirtualCpuCores { get; set; }
    public int QualityTier { get; set; }
    public int ComputePointCost { get; set; }
    public long? MemoryBytes { get; set; }
    public long? DiskBytes { get; set; }
    public string? ImageId { get; set; }  // for recovery
    public DateTime? StartedAt { get; set; }
}

public record NodeHeartbeatResponse(
    bool Acknowledged,
    List<NodeCommand>? PendingCommands,
    AgentSchedulingConfig? SchedulingConfig,
    /// <summary>
    /// Relay configuration if node is behind CGNAT
    /// </summary>
    CgnatNodeInfo? CgnatInfo
);

/// <summary>
/// Command sent from orchestrator to node agent
/// </summary>
public record NodeCommand(
    string CommandId,
    NodeCommandType Type,
    string Payload,
    bool RequiresAck = true,
    string? TargetResourceId = null
)
{
    /// <summary>
    /// When this command was queued
    /// </summary>
    public DateTime QueuedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Optional expiration time for automatic timeout
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Check if this command has expired
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;

    /// <summary>
    /// Get age of this command
    /// </summary>
    public TimeSpan Age => DateTime.UtcNow - QueuedAt;
}

/// <summary>
/// Pending command details for orchestrator tracking
/// </summary>
public record PendingCommand(
    string CommandId,
    NodeCommandType Type,
    string? TargetResourceId,
    DateTime QueuedAt,
    double AgeSeconds,
    bool IsExpired,
    DateTime? ExpiresAt
);

/// <summary>
/// DTO for API responses containing pending command information
/// </summary>
public record PendingCommandDto(
    string CommandId,
    string Type,
    string? TargetResourceId,
    DateTime QueuedAt,
    double AgeSeconds,
    bool IsExpired,
    DateTime? ExpiresAt
);

public enum NodeCommandType
{
    CreateVm,
    StopVm,
    StartVm,
    DeleteVm,
    MigrateVm,
    UpdateAgent,
    CollectDiagnostics
}

public record CommandAcknowledgment(
    string CommandId,
    bool Success,
    string? ErrorMessage,
    DateTime CompletedAt
);


/// <summary>
/// Registration record for tracking active commands
/// </summary>
public record CommandRegistration(
    string CommandId,
    string VmId,
    string NodeId,
    NodeCommandType CommandType,
    DateTime IssuedAt
);

/// <summary>
/// Configuration for nodes acting as relays
/// </summary>
public class RelayNodeInfo
{
    /// <summary>
    /// Is this node currently operating as a relay?
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// VM ID of the relay VM running on this node
    /// </summary>
    public string? RelayVmId { get; set; }

    /// <summary>
    /// WireGuard public endpoint (IP:Port)
    /// </summary>
    public string WireGuardEndpoint { get; set; } = string.Empty;
    /// <summary>
    /// WireGuard public key for this relay
    /// Generated during relay VM deployment and used by CGNAT nodes
    /// </summary>
    public string? WireGuardPublicKey { get; set; }
    /// <summary>
    /// Gets or sets the WireGuard private key used for secure VPN authentication.
    /// </summary>
    /// <remarks>The private key must be a valid WireGuard key in base64 format. This value should be kept
    /// confidential to maintain the security of the VPN connection.</remarks>
    public string? WireGuardPrivateKey { get; set; }

    /// <summary>
    /// Maximum number of CGNAT nodes this relay can serve
    /// Based on available bandwidth and CPU
    /// </summary>
    public int MaxCapacity { get; set; } = 50;

    /// <summary>
    /// Current number of CGNAT nodes using this relay
    /// </summary>
    public int CurrentLoad { get; set; } = 0;

    /// <summary>
    /// IDs of CGNAT nodes currently using this relay
    /// </summary>
    public List<string> ConnectedNodeIds { get; set; } = new();

    /// <summary>
    /// Relay service fee (USDC per hour per connected node)
    /// </summary>
    public decimal RelayFeePerHour { get; set; } = 0.001m;

    /// <summary>
    /// Geographic region for this relay
    /// </summary>
    public string Region { get; set; } = "default";

    /// <summary>
    /// Relay health status
    /// </summary>
    public RelayStatus Status { get; set; } = RelayStatus.Active;

    /// <summary>
    /// Last health check timestamp
    /// </summary>
    public DateTime LastHealthCheck { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration for nodes behind CGNAT
/// </summary>
public class CgnatNodeInfo
{
    /// <summary>
    /// ID of the relay node serving this CGNAT node
    /// </summary>
    public string? AssignedRelayNodeId { get; set; }

    /// <summary>
    /// WireGuard tunnel IP assigned to this node
    /// </summary>
    public string TunnelIp { get; set; } = string.Empty;

    /// <summary>
    /// WireGuard configuration for connecting to relay
    /// </summary>
    public string? WireGuardConfig { get; set; }

    /// <summary>
    /// Public endpoint URL for accessing VMs on this node
    /// Format: https://relay-{region}-{id}.decloud.io
    /// </summary>
    public string PublicEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Connection status to relay
    /// </summary>
    public TunnelStatus TunnelStatus { get; set; } = TunnelStatus.Disconnected;

    /// <summary>
    /// Last successful handshake with relay
    /// </summary>
    public DateTime? LastHandshake { get; set; }
}

public enum RelayStatus
{
    Active,
    Degraded,
    Offline,
    MaintenanceMode
}

public enum TunnelStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}