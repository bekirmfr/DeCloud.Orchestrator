using Nethereum.Merkle.Patricia;
using Orchestrator.Models.Payment;
using Orchestrator.Services;

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
    public DateTime? LastHeartbeat { get; set; } = null;
    public string AgentVersion { get; set; } = string.Empty;

    // Capabilities
    public List<string> SupportedImages { get; set; } = new();
    public bool SupportsGpu { get; set; }
    public GpuInfo? GpuInfo { get; set; }

    /// <summary>
    /// Overall GPU setup status for this node.
    /// Tracks whether automated GPU configuration (VFIO or Container Toolkit) has been performed.
    /// </summary>
    public GpuSetupStatus GpuSetupStatus { get; set; } = GpuSetupStatus.NotNeeded;
    /// <summary>
    /// Relay node configuration (null if node is not a relay)
    /// </summary>
    public RelayNodeInfo? RelayInfo { get; set; }

    /// <summary>
    /// DHT node configuration (null until DHT VM is deployed).
    /// Every node runs a DHT VM — this tracks its state and libp2p peer identity.
    /// </summary>
    public DhtNodeInfo? DhtInfo { get; set; }

    /// <summary>
    /// CGNAT node configuration (null if node has public IP)
    /// </summary>
    public bool IsBehindCgnat => HardwareInventory.Network.NatType != NatType.None;
    public CgnatNodeInfo? CgnatInfo { get; set; }

    /// <summary>
    /// System VMs this node is obligated to run (DHT, Relay, BlockStore, Ingress).
    /// Computed from node capabilities at registration. The reconciliation loop
    /// converges each obligation toward Active status, respecting the dependency graph.
    /// </summary>
    public List<SystemVmObligation> SystemVmObligations { get; set; } = new();

    // Performance metrics
    public NodeMetrics? LatestMetrics { get; set; }

    // Reputation/Trust
    public double UptimePercentage { get; set; } = 100.0;
    public int TotalVmsHosted { get; set; }
    public int SuccessfulVmCompletions { get; set; }
    
    /// <summary>
    /// Failed heartbeats tracked by day (date string -> count)
    /// Key format: "yyyy-MM-dd" (e.g., "2026-01-30")
    /// Used to calculate uptime percentage over 30-day rolling window
    /// </summary>
    public Dictionary<string, int> FailedHeartbeatsByDay { get; set; } = new();
    
    /// <summary>
    /// Last time we checked for failed heartbeats (to avoid double-counting)
    /// </summary>
    public DateTime? LastFailedHeartbeatCheckAt { get; set; }

    // Region/Location for scheduling
    public string Region { get; set; } = "default";
    public string Zone { get; set; } = "default";
    public DateTime? LastSeenAt { get; set; } = null;
    /// <summary>
    /// Last time a command was successfully pushed to this node
    /// </summary>
    public DateTime? LastCommandPushedAt { get; set; }

    /// <summary>
    /// Consecutive successful push deliveries (for reliability tracking)
    /// </summary>
    public int ConsecutivePushSuccesses { get; set; } = 0;

    /// <summary>
    /// Consecutive failed push deliveries (triggers fallback to pull-only)
    /// </summary>
    public int ConsecutivePushFailures { get; set; } = 0;

    /// <summary>
    /// If true, node can receive pushed commands
    /// Automatically disabled after multiple consecutive push failures
    /// </summary>
    public bool PushEnabled { get; set; } = true;

    // ============================================================================
    // Marketplace & Discovery
    // ============================================================================
    
    /// <summary>
    /// Operator-provided description for marketplace display
    /// Example: "High-end gaming GPUs, EU datacenter, 24/7 uptime"
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// Searchable tags for node discovery
    /// Examples: "gpu", "nvme", "high-memory", "eu-gdpr", "gaming"
    /// </summary>
    public List<string> Tags { get; set; } = new();
    
    /// <summary>
    /// Base pricing in USDC per compute point per hour (legacy, kept for backward compatibility)
    /// Prefer using Pricing for per-resource rates.
    /// </summary>
    public decimal BasePrice { get; set; } = 0.01m;

    /// <summary>
    /// Per-resource pricing set by the node operator.
    /// Rates must be >= platform floor rates (enforced by orchestrator).
    /// If null or zero rates, platform defaults are used.
    /// </summary>
    public NodePricing Pricing { get; set; } = new();
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

    /// <summary>
    /// Container runtimes available on this node (e.g., "docker", "podman")
    /// </summary>
    public List<string> ContainerRuntimes { get; set; } = new();

    /// <summary>
    /// True if any GPU supports container-based sharing (Docker + NVIDIA Container Toolkit)
    /// </summary>
    public bool SupportsGpuContainers { get; set; }
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

    /// <summary>
    /// True if GPU can be shared via Docker + NVIDIA Container Toolkit.
    /// Set when Docker daemon and nvidia runtime are detected.
    /// </summary>
    public bool IsAvailableForContainerSharing { get; set; }

    /// <summary>
    /// Tracks the automated GPU setup lifecycle on this GPU.
    /// Updated by GpuSetupService when ConfigureGpu commands are sent/acknowledged.
    /// </summary>
    public GpuSetupStatus SetupStatus { get; set; } = GpuSetupStatus.NotNeeded;

    // Current utilization
    public double GpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public int? TemperatureCelsius { get; set; }
}

/// <summary>
/// Tracks the automated GPU setup lifecycle on a node.
/// The orchestrator sends ConfigureGpu commands and tracks progress through these states.
/// </summary>
public enum GpuSetupStatus
{
    /// <summary>No GPU detected or GPU already fully configured</summary>
    NotNeeded,

    /// <summary>GPU detected but neither VFIO nor Container Toolkit is ready. Setup will be queued.</summary>
    Pending,

    /// <summary>ConfigureGpu command sent to node agent, awaiting acknowledgment</summary>
    InProgress,

    /// <summary>GPU is configured and ready for workloads (passthrough, container sharing, or both)</summary>
    Completed,

    /// <summary>Setup failed — may need manual intervention. Error details in node event log.</summary>
    Failed,

    /// <summary>IOMMU was enabled in grub/modules but a host reboot is required to activate it</summary>
    RebootRequired
}

/// <summary>
/// Specifies which GPU sharing mode the node agent should configure.
/// Sent as part of the ConfigureGpu command payload.
/// </summary>
public enum GpuSetupMode
{
    /// <summary>
    /// Let the node agent pick the best mode based on hardware capabilities.
    /// Prefers ContainerToolkit (immediate, no reboot) and optionally enables VFIO if IOMMU is available.
    /// </summary>
    Auto,

    /// <summary>Configure IOMMU + VFIO kernel modules for full GPU passthrough to VMs</summary>
    VfioPassthrough,

    /// <summary>Install NVIDIA drivers + NVIDIA Container Toolkit for Docker --gpus support</summary>
    ContainerToolkit
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

    /// <summary>
    /// Operator-defined pricing per resource (optional).
    /// If null, platform defaults are used.
    /// </summary>
    public NodePricing? Pricing { get; set; }
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
    TimeSpan HeartbeatInterval,
    /// <summary>
    /// libp2p multiaddrs of active DHT peers for bootstrap.
    /// Format: "/ip4/{ip}/tcp/4001/p2p/{peerId}"
    /// Empty list if no DHT peers are active yet (this node will be first).
    /// </summary>
    List<string> DhtBootstrapPeers
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

    /// <summary>
    /// Per-service readiness statuses reported by node agent's VmReadinessMonitor.
    /// Updated via qemu-guest-agent checks on the node.
    /// </summary>
    public List<HeartbeatServiceInfo>? Services { get; set; }
}

/// <summary>
/// Per-service readiness status reported in heartbeat from node agent.
/// Lightweight DTO — the orchestrator maps this to VmServiceStatus on the VM.
/// </summary>
public class HeartbeatServiceInfo
{
    public string Name { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string? Protocol { get; set; }
    public string Status { get; set; } = "Pending";  // Pending, Checking, Ready, TimedOut, Failed
    /// <summary>
    /// Human-readable explanation from node agent.
    /// E.g., "cloud-init error: apt-get install failed" or "cloud-init did not finish within 300s"
    /// </summary>
    public string? StatusMessage { get; set; }
    public DateTime? ReadyAt { get; set; }
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
    CollectDiagnostics,
    AllocatePort,      // Smart Port Allocation: Allocate public port for VM
    RemovePort,        // Smart Port Allocation: Remove port mapping
    ConfigureGpu       // GPU Setup: Configure VFIO passthrough or NVIDIA Container Toolkit
}

public record CommandAcknowledgment(
    string CommandId,
    bool Success,
    string? ErrorMessage,
    DateTime CompletedAt,
    string? Data = null  // JSON-encoded result data (e.g., allocated port info)
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
    /// VM ID of the relay VM running on this node
    /// </summary>
    public string RelayVmId { get; set; } = string.Empty;

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
    /// WireGuard tunnel IP of the relay VM (e.g., 10.20.1.254)
    /// </summary>
    public string TunnelIp { get; set; } = string.Empty;

    /// <summary>
    /// Unique subnet number for this relay (1-254)
    /// Used for WireGuard network isolation: 10.20.{RelaySubnet}.0/24
    /// </summary>
    public int RelaySubnet { get; set; } = 0;

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
    /// CGNAT node IDs currently connected to this relay
    /// Uses HashSet to prevent duplicate entries
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
    public string AssignedRelayNodeId { get; set; } = string.Empty;

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
    Initializing,
    Active,
    Degraded,
    Offline,
    MaintenanceMode
}

public enum TunnelStatus
{
    Connecting,
    Connected,
    Disconnected,
    Error
}

/// <summary>
/// DHT node state tracked on the host node.
/// Populated when the DHT system VM is deployed.
/// </summary>
public class DhtNodeInfo
{
    /// <summary>
    /// VM ID of the DHT VM running on this node
    /// </summary>
    public string DhtVmId { get; set; } = string.Empty;

    /// <summary>
    /// libp2p peer ID (e.g., "QmXyz..." or "12D3Koo...").
    /// Set once the DHT VM reports its identity via qemu-guest-agent.
    /// </summary>
    public string? PeerId { get; set; }

    /// <summary>
    /// Address the DHT VM listens on (e.g., "10.20.1.2:4001" or "203.0.113.5:4001")
    /// </summary>
    public string ListenAddress { get; set; } = string.Empty;

    /// <summary>
    /// Localhost HTTP API port for node agent → DHT VM queries
    /// </summary>
    public int ApiPort { get; set; } = 5080;

    /// <summary>
    /// Number of bootstrap peers provided at deployment time
    /// </summary>
    public int BootstrapPeerCount { get; set; }

    /// <summary>
    /// Number of currently connected peers in the Kademlia routing table
    /// </summary>
    public int ConnectedPeers { get; set; }

    public DhtStatus Status { get; set; } = DhtStatus.Initializing;
    public DateTime? LastHealthCheck { get; set; }
}

public enum DhtStatus
{
    Initializing,
    Active,
    Degraded,
    Offline
}

/// <summary>
/// Acknowledgment data returned by NodeAgent when allocating a port
/// </summary>
public class AllocatePortAckData
{
    public int VmPort { get; set; }
    public int PublicPort { get; set; }
    public int Protocol { get; set; }
}