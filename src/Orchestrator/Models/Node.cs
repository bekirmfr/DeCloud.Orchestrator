using DeCloud.Shared.Contracts;
using DeCloud.Shared.Dtos;
using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
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
    /// JWT ID (jti claim) of the most recently issued JWT for this node.
    /// Used to revoke the token on deregister/uninstall.
    /// </summary>
    public string? CurrentJti { get; set; }

    /// <summary>
    /// SSH certificate authority public key for this node, captured at
    /// registration from <c>/etc/ssh/decloud_ca.pub</c> on the node side.
    ///
    /// <para>
    /// Used by <c>CaPublicKeyResolver</c> to substitute <c>__CA_PUBLIC_KEY__</c>
    /// in cloud-init at render time, so the orchestrator (not the node)
    /// is the source of truth for this value at deploy time. The CA key
    /// itself still lives on the node — the orchestrator only stores a
    /// copy for use during VM rendering.
    /// </para>
    ///
    /// <para>
    /// Null on nodes registered before P1.9; their VMs will hit
    /// <c>CaPublicKeyResolver</c>'s null-guard exception until the node
    /// re-registers with the new agent version.
    /// </para>
    /// </summary>
    public string? SshCaPublicKey { get; set; }

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
    /// <summary>
    /// Physical capability totals. Set at evaluate time from HardwareInventory
    /// and PerformanceEvaluation. Represents what the hardware CAN do.
    /// </summary>
    public ResourceSnapshot TotalResources { get; set; } = new();

    /// <summary>
    /// Operator-configured allocation percentages. Raw configuration —
    /// resolved into concrete values in <see cref="AllocatedResources"/>
    /// at allocate time.
    /// </summary>
    public AllocationConfig? AllocationConfig { get; set; }

    /// <summary>
    /// Concrete allocated capacity = TotalResources × AllocationConfig percentages.
    /// Set at allocate time. Scheduling checks: AllocatedResources - UsedResources ≥ requested.
    /// </summary>
    public ResourceSnapshot AllocatedResources { get; set; } = new();

    public ResourceSnapshot ReservedResources { get; set; } = new();
    /// <summary>
    /// Resource usage computed from heartbeat-reported VMs.
    /// Recomputed every heartbeat cycle from the sum of all active VM specs.
    /// This is the ground truth for what the node is actually running.
    /// See docs/RESOURCE-ALLOCATION.md S8.3.
    /// </summary>
    public ResourceSnapshot UsedResources { get; set; } = new();

    // State
    public NodeStatus Status { get; set; } = NodeStatus.Offline;
    /// <summary>
    /// Operator-controlled scheduling readiness flag.
    /// When false, the scheduler skips this node for new VM placement.
    /// The node continues heartbeating and hosting existing VMs.
    ///
    /// Set to true by POST /api/nodes/{id}/login (operator signals readiness).
    /// Set to false by POST /api/nodes/{id}/logout (operator pauses scheduling).
    ///
    /// Defaults to true for backward compatibility: existing nodes that
    /// registered before this field was introduced are implicitly ready.
    /// New registrations also default to true so that the current
    /// register-and-go flow continues to work until the CLI is updated
    /// to call the login endpoint separately.
    /// </summary>
    public bool SchedulingReady { get; set; } = true;
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastHeartbeat { get; set; } = null;
    public string AgentVersion { get; set; } = string.Empty;

    // Capabilities
    public List<string> SupportedImages { get; set; } = new();

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
    /// Block store node configuration (null until BlockStore VM is deployed).
    /// Only present on nodes with ≥100 GB storage and ≥2 GB RAM.
    /// </summary>
    public BlockStoreInfo? BlockStoreInfo { get; set; }

    /// <summary>
    /// CGNAT node configuration (null if node has public IP)
    /// </summary>
    public bool IsBehindCgnat => HardwareInventory != null
                                   && HardwareInventory?.Network?.NatType != NatType.None;
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

    /// <summary>
    /// Three-axis locality record: country (jurisdiction), region (network
    /// locality), zone (operator grouping). Populated at registration by
    /// <c>NodeService.RegisterNodeAsync</c> via <c>ILocalityService</c>.
    /// <para>See <c>docs/LOCALITY.md</c> for the full standard.</para>
    /// </summary>
    public NodeLocality Locality { get; set; } = new();

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
    /// Base pricing in USDC per compute point per hour.
    /// </summary>
    /// <remarks>
    /// Deprecated — never read by the billing pipeline. Preserved only for
    /// backward-compatible deserialization of existing MongoDB documents.
    /// Use <see cref="Pricing"/> for all rate logic.
    /// </remarks>
    [Obsolete("Not used in billing. Read Pricing instead.")]
    public decimal BasePrice { get; set; } = 0.01m;

    /// <summary>
    /// Per-resource pricing set by the node operator.
    /// Rates must be >= platform floor rates (enforced by orchestrator).
    /// If null or zero rates, platform defaults are used.
    /// </summary>
    public NodePricing Pricing { get; set; } = new();
    /// <summary>
    /// SHA-256 hash of the settings as they were at last registration.
    /// Computed by SettingsHash.Compute() from the registration request fields.
    /// Compared against the heartbeat-reported hash for drift detection.
    /// </summary>
    public string? RegisteredSettingsHash { get; set; }

    /// <summary>
    /// Settings hash most recently reported by the node agent in its heartbeat.
    /// Stored so LoginNodeAsync can compare without waiting for a heartbeat.
    /// Set in ProcessHeartbeatAsync; null until first heartbeat.
    /// </summary>
    public string? LatestHeartbeatSettingsHash { get; set; }
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

    /// <summary>
    /// True if /dev/kvm exists on the node host (KVM kernel module loaded,
    /// hardware virtualization available). False on VPS hosts without nested
    /// virt — QEMU TCG fallback is used, which is too slow for user workloads.
    /// System VMs (Relay, DHT, BlockStore) can run on either.
    /// </summary>
    public bool KvmAvailable { get; set; } = true; // default true for backward compat

    // ====================================================================
    // GPU Capability Helpers (for scheduling decisions)
    // ====================================================================

    /// <summary>
    /// Number of GPUs available on this node
    /// </summary>
    public int GpuCount => Gpus.Count;

    /// <summary>
    /// True if any GPU has IOMMU enabled (required for Passthrough mode)
    /// </summary>
    public bool HasIommuCapableGpu => Gpus.Any(g => g.IsIommuEnabled);

    /// <summary>
    /// True if node has at least one GPU available for VFIO passthrough
    /// </summary>
    public bool HasPassthroughCapableGpu => Gpus.Any(g => g.IsAvailableForPassthrough);

    /// <summary>
    /// True if at least one GPU on this node is available for Proxied (shared) access
    /// via the GPU proxy daemon. Populated from the agent's per-GPU discovery results.
    /// A node may have both passthrough-capable and proxy-capable GPUs simultaneously.
    /// </summary>
    public bool HasProxiedCapableGpu => Gpus.Any(g => g.IsAvailableForProxiedSharing);
}

public class CpuInfo
{
    public string Model { get; set; } = string.Empty;
    public int PhysicalCores { get; set; }
    public int LogicalCores { get; set; }
    public double FrequencyMhz { get; set; }
    public List<string> Flags { get; set; } = new(); // e.g., "vmx", "svm" for virtualization
    public bool SupportsVirtualization { get; set; }
    /// <summary>
    /// CPU architecture string as reported by the node agent
    /// (e.g., "x86_64", "aarch64"). Used by the scheduler to resolve
    /// architecture-specific template artifacts. Null on nodes that
    /// pre-date this field — callers default to "amd64".
    /// </summary>
    public string? Architecture { get; set; }

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
    /// True if GPU can be shared via the host-side GPU proxy daemon over virtio-vsock.
    /// Set when the GPU is present but not available for VFIO passthrough (no IOMMU).
    /// Populated from the agent's HardwareInventory reported at registration/heartbeat.
    /// </summary>
    public bool IsAvailableForProxiedSharing { get; set; }

    /// <summary>
    /// True if GPU can be shared via Docker + NVIDIA Container Toolkit.
    /// Set when Docker daemon and nvidia runtime are detected.
    /// </summary>
    public bool IsAvailableForContainerSharing { get; set; }

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
    /// <summary>
    /// Committed VRAM across active GPU VMs.
    /// In UsedResources: sum of GpuVramBytes quotas for Proxied VMs in the heartbeat.
    /// In ReservedResources: transient scheduling holds for VMs entering Provisioning.
    /// In AllocatedResources: not used (GPU VRAM allocation is not percentage-based).
    /// </summary>
    public long GpuVramBytes { get; set; }
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
    /// <summary>
    /// SSH certificate authority public key, captured by the node agent
    /// at registration time from <c>/etc/ssh/decloud_ca.pub</c>.
    ///
    /// <para>
    /// Stamped into <see cref="Node.SshCaPublicKey"/> by
    /// <c>NodeService.RegisterNodeAsync</c> and consumed by
    /// <c>CaPublicKeyResolver</c> at cloud-init render time to substitute
    /// <c>__CA_PUBLIC_KEY__</c> in tenant templates.
    /// </para>
    ///
    /// <para>
    /// Optional for backward compatibility — nodes running pre-P1.9 agents
    /// won't send this field. Their <c>Node.SshCaPublicKey</c> stays null;
    /// any tenant deploy that uses <c>__CA_PUBLIC_KEY__</c> will fail at
    /// render time with a clear message pointing at this gap.
    /// </para>
    /// </summary>
    public string? SshCaPublicKey { get; set; }

    // Staking info
    public string StakingTxHash { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string? Zone { get; set; }

    /// <summary>
    /// ISO 3166-1 alpha-2 country code declared by the operator.
    /// Optional for backward compatibility with pre-2.3 agents that don't
    /// send this field. When absent the orchestrator records "ZZ" (unknown).
    /// When present it is validated against <c>countries.json</c> and the
    /// matching supranational tags are derived server-side.
    /// </summary>
    public string? Country { get; set; }

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

    /// <summary>
    /// Operator-configured resource allocation limits, resolved to absolute
    /// values by the node agent. Null fields or null object = platform default (90%).
    /// See docs/RESOURCE-ALLOCATION.md §5.2.
    /// </summary>
    public AllocatedResources? AllocatedResources { get; set; }
}

/// <summary>
/// Response from POST /api/nodes/me/evaluate.
/// Uses AgentSchedulingConfig (the lightweight projection) — not the full
/// SchedulingConfig MongoDB document — so the wire type is identical on both sides.
/// </summary>
public record EvaluateNodeResponse(
    NodePerformanceEvaluation PerformanceEvaluation,
    AgentSchedulingConfig SchedulingConfig,
    List<string> DhtBootstrapPeers,
    Dictionary<string, ObligationStatePayload> ObligationStates,
    Dictionary<string, SystemVmTemplatePayload>? SystemTemplates = null,
    List<ObligationDescriptorDto>? Obligations = null
);

public class NodeDeregisterRequest
{
    public string Reason { get; set; } = "manual_uninstall";
    public bool Force { get; set; } = false;
}

public class NodeDeregisterResponse
{
    public bool Deregistered { get; set; }
    public int TenantVmsDestroyed { get; set; }
    public string? Message { get; set; }
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

public enum RelayStatus
{
    Initializing,
    Active,
    Degraded,
    Offline,
    MaintenanceMode
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
/// Block store node state tracked on the host node.
/// Populated when the BlockStore system VM is deployed and calls /api/blockstore/join.
/// </summary>
public class BlockStoreInfo
{
    /// <summary>VM ID of the block store VM running on this node.</summary>
    public string BlockStoreVmId { get; set; } = string.Empty;

    /// <summary>libp2p peer ID (e.g., "12D3Koo..." or "QmXyz..."). Set on /join.</summary>
    public string? PeerId { get; set; }

    /// <summary>Address the block store VM listens on (e.g., "10.20.1.202:5001").</summary>
    public string ListenAddress { get; set; } = string.Empty;

    /// <summary>Localhost HTTP API port for node agent → block store VM queries.</summary>
    public int ApiPort { get; set; } = 5090;

    /// <summary>Allocated storage (5% of node total) in bytes.</summary>
    public long CapacityBytes { get; set; }

    /// <summary>Currently used storage in bytes (reported via /announce).</summary>
    public long UsedBytes { get; set; }

    /// <summary>Number of blocks currently stored (determined by Kademlia XOR proximity).</summary>
    public int BlockCount { get; set; }

    public BlockStoreStatus Status { get; set; } = BlockStoreStatus.Initializing;
    public DateTime? LastHealthCheck { get; set; }
}

public enum BlockStoreStatus
{
    Initializing,
    Active,
    Degraded,
    Full,
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