using MongoDB.Bson.Serialization.Attributes;
using Orchestrator.Services;
using Orchestrator.Models.Payment;
using System.Text.Json.Serialization;

namespace Orchestrator.Models;

/// <summary>
/// Represents a Virtual Machine in the network
/// </summary>
public class VirtualMachine
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public VmType VmType { get; set; } = VmType.General;

    // Ownership
    public string? OwnerId { get; set; } = string.Empty;        // User/tenant ID
    public string? OwnerWallet
    {
        get => OwnerId;
        set => OwnerId = value;
    }

    // Placement
    public string NodeId { get; set; }                        // Which node it's running on
    public string? TargetNodeId { get; set; }                  // For migrations
    
    // Specification
    public VmSpec Spec { get; set; } = new();
    
    // State
    public VmStatus Status { get; set; } = VmStatus.Pending;
    public VmPowerState PowerState { get; set; } = VmPowerState.Off;
    public string? StatusMessage { get; set; }
    
    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Networking
    public VmNetworkConfig NetworkConfig { get; set; } = new();

    public VmNetworkMetrics? NetworkMetrics { get; set; }
    /// <summary>
    /// Attestation statistics (persisted to MongoDB)
    /// </summary>
    public VmLivenessState? AttestationStats { get; set; }

    /// <summary>
    /// Ingress configuration for this VM (auto-subdomain and custom domains)
    /// </summary>
    public VmIngressConfig? IngressConfig { get; set; }

    /// <summary>
    /// Direct access configuration (Smart Port Allocation for TCP/UDP)
    /// </summary>
    public VmDirectAccess? DirectAccess { get; set; }

    // Access
    public VmAccessInfo? AccessInfo { get; set; }
    
    // Billing
    public VmBillingInfo BillingInfo { get; set; } = new();
    
    // Runtime metrics (updated periodically)
    public VmMetrics? LatestMetrics { get; set; }
    
    // Tags/Labels
    public Dictionary<string, string> Labels { get; set; } = new();

    // =========================================================================
    // Template Tracking (for marketplace deployments)
    // =========================================================================
    
    /// <summary>
    /// Template ID if deployed from marketplace template
    /// </summary>
    public string? TemplateId { get; set; }
    
    /// <summary>
    /// Template name (cached for display)
    /// </summary>
    public string? TemplateName { get; set; }
    
    /// <summary>
    /// Template version used for deployment
    /// </summary>
    public string? TemplateVersion { get; set; }

    // =========================================================================
    // Service Readiness Tracking (per-service status via qemu-guest-agent)
    // =========================================================================

    /// <summary>
    /// Per-service readiness statuses.
    /// Always includes an implicit "System" entry (cloud-init completion).
    /// Additional entries come from template ExposedPorts.
    /// Updated via heartbeat from node agent.
    /// </summary>
    public List<VmServiceStatus> Services { get; set; } = new();

    /// <summary>
    /// True when all services (including System) report Ready.
    /// </summary>
    [BsonIgnore]
    public bool IsFullyReady => Services.Count > 0 && Services.All(s => s.Status == ServiceReadiness.Ready);

    // =========================================================================
    // Command Tracking (for reliable acknowledgement processing)
    // =========================================================================

    /// <summary>
    /// Currently active command ID for this VM.
    /// Used for reliable command acknowledgement tracking.
    /// Cleared after command is acknowledged.
    /// </summary>
    public string? ActiveCommandId { get; set; }

    /// <summary>
    /// Type of the currently active command.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NodeCommandType? ActiveCommandType { get; set; }

    /// <summary>
    /// Timestamp when the active command was issued.
    /// Used for timeout detection.
    /// </summary>
    public DateTime? ActiveCommandIssuedAt { get; set; }
}

public class VmSpec
{
    public VmType? VmType { get; set; } = Models.VmType.General;
    public int VirtualCpuCores { get; set; } = 1;
    public long MemoryBytes { get; set; } = 2 * 1024L * 1024L * 1024L; // 2 GB
    //[BsonIgnore]
    //[JsonIgnore]
    //public long MemoryMb => MemoryBytes / (1024L * 1024L);
    public long DiskBytes { get; set; } = 20 * 1024L * 1024L * 1024L;
    //[BsonIgnore]
    //[JsonIgnore]
    //public long DiskGb => DiskBytes / (1024L * 1024L * 1024L);
    public bool RequiresGpu { get; set; }
    public string? GpuModel { get; set; }

    // ========================================
    // COMPUTE POINT COST TRACKING
    // ========================================
    
    /// <summary>
    /// Preferred quality tier for scheduling (Guaranteed, Standard, Burstable)
    /// Default: Standard
    /// </summary>
    public QualityTier QualityTier { get; set; } = QualityTier.Standard;

    /// <summary>
    /// Compute point cost for this VM based on tier and vCPU count
    /// Calculated during scheduling: CpuCores � PointsPerVCpu[QualityTier]
    /// Examples on 2-core (16-point) node:
    /// - Guaranteed 1vCPU: 1 � 8 = 8 points
    /// - Standard 1vCPU: 1 � 4 = 4 points
    /// - Balanced 1vCPU: 1 � 2 = 2 points
    /// - Burstable 1vCPU: 1 � 1 = 1 point
    /// </summary>
    public int ComputePointCost { get; set; }

    // ========================================
    // SCHEDULING PREFERENCES
    // ========================================

    /// <summary>
    /// Preferred region for deployment (e.g., "us-west", "eu-central")
    /// If null, any region is acceptable
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Preferred availability zone within region (e.g., "us-west-2a")
    /// If null, any zone in preferred region is acceptable
    /// </summary>
    public string? Zone { get; set; }

    /// <summary>
    /// Minimum node reputation score (0.0 to 1.0) required for scheduling
    /// Higher values = more selective about node quality
    /// Default: 0.3 (accept most nodes)
    /// </summary>
    public double MinNodeReputationScore { get; set; } = 0.3;

    /// <summary>
    /// Maximum acceptable CPU overcommit ratio
    /// Example: 2.0 means accept up to 2:1 CPU overcommit
    /// If null, use tier default
    /// </summary>
    public double? MaxCpuOvercommitRatio { get; set; }

    /// <summary>
    /// Tags for advanced scheduling (anti-affinity, affinity, etc.)
    /// Example: { "app": "web", "env": "production" }
    /// </summary>
    public Dictionary<string, string> SchedulingTags { get; set; } = new();

    // ========================================
    // IMAGE & ACCESS
    // ========================================
    public string ImageId { get; set; } = string.Empty;       // e.g., "ubuntu-22.04"
    
    // SSH key for access
    public string? SshPublicKey { get; set; }

    /// <summary>
    /// Wallet-encrypted password (stored permanently)
    /// Format: base64(iv):base64(ciphertext):base64(tag)
    /// </summary>
    public string? WalletEncryptedPassword { get; set; }

    // Cloud-init / user data
    public string? UserData { get; set; }

    public int MaxConnections { get; set; } = -1; // -1 = unlimited

    // ========================================
    // BANDWIDTH TIER
    // ========================================

    /// <summary>
    /// Bandwidth tier for network rate limiting.
    /// Enforced via libvirt QoS on the VM's virtio network interface.
    /// Default: Unmetered (no artificial cap)
    /// </summary>
    public BandwidthTier BandwidthTier { get; set; } = BandwidthTier.Unmetered;
}

public class VmNetworkConfig
{
    public bool IsIpAssigned { get; set; }
    public string? PrivateIp { get; set; }
    public string? PublicIp { get; set; }

    public string? MacAddress { get; set; }
    public string? Hostname { get; set; }
    public List<PortMapping> PortMappings { get; set; } = new();
    public string? OverlayNetworkId { get; set; }

    /// <summary>
    /// Public IP of the node server hosting this VM (SSH jump host)
    /// </summary>
    public string? SshJumpHost { get; set; }

    /// <summary>
    /// Port for SSH jump host connection (2222 to bypass ISP blocks on port 22)
    /// Note: This is for SSH to the node, not the NodeAgent API port
    /// </summary>
    public int? SshJumpPort { get; set; } = 2222;

    /// <summary>
    /// Node Agent API endpoint (for web terminal WebSocket connections)
    /// Format: ws://{NodeAgentHost}:{NodeAgentPort}/api/vms/{vmId}/terminal
    /// </summary>
    public string? NodeAgentHost { get; set; }

    /// <summary>
    /// Node Agent API port (typically 5100)
    /// </summary>
    public int? NodeAgentPort { get; set; } = 5100;
}

public class PortMapping
{
    public int HostPort { get; set; }
    public int GuestPort { get; set; }
    public string Protocol { get; set; } = "tcp";   // tcp/udp
}

public class VmAccessInfo
{
    public string? SshHost { get; set; }
    public int SshPort { get; set; } = 22;
    public string? VncHost { get; set; }
    public int VncPort { get; set; } = 5900;
    public string? VncPassword { get; set; }
    public string? ConsoleWebSocketUrl { get; set; }
}

public class VmMetrics
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public long DiskReadBytes { get; set; }
    public long DiskWriteBytes { get; set; }
    public long NetworkRxBytes { get; set; }
    public long NetworkTxBytes { get; set; }
}

public enum VmStatus
{
    Pending,        // 0 - Waiting to be scheduled
    Scheduling,     // 1 - Finding a node
    Provisioning,   // 2 - Being created on node
    Running,        // 3 - Active and running
    Stopping,       // 4 - Being stopped
    Stopped,        // 5 - Stopped but resources reserved
    Deleting,       // 6 - Deletion in progress, waiting for node confirmation
    Migrating,      // 7 - Being moved to another node
    Error,          // 8 - Something went wrong
    Deleted         // 9 - Deletion confirmed, resources freed
}

public enum VmPowerState
{
    Off,
    Running,
    Paused
}

public enum VmType
{
    General,
    Compute,
    Memory,
    Storage,
    Gpu,
    Relay,
    Dht,
    Inference
}

// DTOs for API
public record CreateVmRequest(
    string Name,
    VmSpec Spec,
    VmType VmType = VmType.General,
    string? NodeId = null,
    Dictionary<string, string>? Labels = null,
    string? TemplateId = null,
    Dictionary<string, string>? EnvironmentVariables = null,
    // Phase 2: Custom cloud-init support (optional, overrides template)
    string? CustomCloudInit = null
);

public record CreateVmResponse(
    string VmId,
    VmStatus Status,
    string Message,
    string? Error = null,
    string? Password = null
);

public record VmActionRequest(
    VmAction Action
);

public enum VmAction
{
    Start,
    Stop,
    Restart,
    Pause,
    Resume,
    ForceStop
}

public record VmListResponse(
    List<VmSummary> Vms,
    int TotalCount,
    int Page,
    int PageSize
);

public record VmSummary(
    string Id,
    string Name,
    VmStatus Status,
    VmPowerState PowerState,
    string? NodeId,
    string? NodePublicIp,       // Node's public IP for SSH access
    int? NodeAgentPort,         // Node's agent port (null = default 5100)
    VmSpec Spec,
    VmNetworkConfig NetworkConfig,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? TemplateId = null,
    List<VmServiceStatus>? Services = null
);

public record VmDetailResponse(
    VirtualMachine Vm,
    Node? HostNode
);

/// <summary>
/// Network latency metrics for adaptive attestation timeouts
/// Embedded in VirtualMachine model (similar to BillingInfo, AccessInfo, etc.)
/// </summary>
public class VmNetworkMetrics
{
    /// <summary>
    /// Baseline RTT measured during VM creation (ms)
    /// Median of 5 initial ping measurements
    /// </summary>
    public double BaselineRttMs { get; set; }

    /// <summary>
    /// Current RTT (exponential moving average, ms)
    /// Updated after each successful attestation
    /// </summary>
    public double CurrentRttMs { get; set; }

    /// <summary>
    /// Recent RTT measurements (last 10)
    /// Stored as arrays for MongoDB compatibility
    /// </summary>
    public List<RttMeasurement> RecentMeasurements { get; set; } = new();

    /// <summary>
    /// When the baseline was last calibrated
    /// </summary>
    public DateTime LastCalibrationAt { get; set; }

    /// <summary>
    /// Statistical metrics for monitoring
    /// </summary>
    public double MinRttMs { get; set; }
    public double MaxRttMs { get; set; }
    public double StdDevRttMs { get; set; }

    /// <summary>
    /// Average processing time inside VM (excluding network RTT)
    /// Should be ~20-40ms for legitimate attestation
    /// </summary>
    public double AvgProcessingTimeMs { get; set; }

    /// <summary>
    /// Last time metrics were updated
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Number of measurements taken
    /// </summary>
    public int TotalMeasurements { get; set; }

    /// <summary>
    /// Calculate adaptive timeout for this VM
    /// </summary>
    public double CalculateAdaptiveTimeout(
        double maxProcessingTimeMs = 50,
        double safetyMarginMs = 20,
        double absoluteMaxMs = 500)
    {
        var timeout = CurrentRttMs + maxProcessingTimeMs + safetyMarginMs;
        return Math.Min(timeout, absoluteMaxMs);
    }

    /// <summary>
    /// Check if recalibration is needed
    /// </summary>
    public bool NeedsRecalibration(
        TimeSpan maxAge = default,
        double rttChangeThreshold = 0.3,
        double maxStdDevRatio = 0.5)
    {
        if (maxAge == default)
            maxAge = TimeSpan.FromHours(24);

        // Time-based recalibration
        if (DateTime.UtcNow - LastCalibrationAt > maxAge)
            return true;

        // Significant RTT change
        var rttChange = Math.Abs(CurrentRttMs - BaselineRttMs) / Math.Max(1, BaselineRttMs);
        if (rttChange > rttChangeThreshold)
            return true;

        // High variance in measurements
        if (StdDevRttMs > CurrentRttMs * maxStdDevRatio)
            return true;

        return false;
    }

    /// <summary>
    /// Initialize default metrics (called at VM creation)
    /// </summary>
    public static VmNetworkMetrics CreateDefault(double baselineRttMs = 50.0)
    {
        return new VmNetworkMetrics
        {
            BaselineRttMs = baselineRttMs,
            CurrentRttMs = baselineRttMs,
            MinRttMs = baselineRttMs,
            MaxRttMs = baselineRttMs,
            StdDevRttMs = 0,
            AvgProcessingTimeMs = 30, // Default estimate
            LastCalibrationAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            TotalMeasurements = 0,
            RecentMeasurements = new List<RttMeasurement>()
        };
    }
}

/// <summary>
/// Single RTT measurement
/// </summary>
public class RttMeasurement
{
    public DateTime Timestamp { get; set; }
    public double RttMs { get; set; }
    public double ProcessingTimeMs { get; set; }
    public bool WasSuccessful { get; set; }

    /// <summary>
    /// Total response time (RTT + processing)
    /// </summary>
    public double TotalTimeMs => RttMs + ProcessingTimeMs;
}

// =========================================================================
// Service Readiness Tracking
// =========================================================================

/// <summary>
/// Tracks the readiness status of a single service inside a VM.
/// The "System" service (cloud-init completion) is always present.
/// Additional services come from template ExposedPorts.
/// Checked via qemu-guest-agent from the node agent (hypervisor channel).
/// </summary>
public class VmServiceStatus
{
    /// <summary>
    /// Service display name (e.g., "System", "PostgreSQL", "VS Code Server")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Port number for this service. Null for "System" (cloud-init).
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Protocol (tcp, http, udp, etc.). Null for "System".
    /// </summary>
    public string? Protocol { get; set; }

    /// <summary>
    /// How this service is checked for readiness
    /// </summary>
    public CheckType CheckType { get; set; } = CheckType.CloudInitDone;

    /// <summary>
    /// For HttpGet: URL path to check
    /// </summary>
    public string? HttpPath { get; set; }

    /// <summary>
    /// For ExecCommand: command to run inside VM
    /// </summary>
    public string? ExecCommand { get; set; }

    /// <summary>
    /// Current readiness status
    /// </summary>
    public ServiceReadiness Status { get; set; } = ServiceReadiness.Pending;

    /// <summary>
    /// When the service was first detected as ready
    /// </summary>
    public DateTime? ReadyAt { get; set; }

    /// <summary>
    /// When the last check was performed
    /// </summary>
    public DateTime? LastCheckAt { get; set; }

    /// <summary>
    /// Max seconds to wait before marking TimedOut
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// How a service readiness check is performed via qemu-guest-agent
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CheckType
{
    /// <summary>
    /// Implicit "System" check: cloud-init status == done.
    /// Always the first service checked; all others gate on this.
    /// </summary>
    CloudInitDone,

    /// <summary>
    /// TCP connect: nc -zv -w2 localhost {port}
    /// </summary>
    TcpPort,

    /// <summary>
    /// HTTP GET: curl -sf -o /dev/null localhost:{port}{path}
    /// </summary>
    HttpGet,

    /// <summary>
    /// Arbitrary command: execute inside VM, exit code 0 = ready
    /// </summary>
    ExecCommand
}

/// <summary>
/// Readiness state of a single service inside a VM
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceReadiness
{
    /// <summary>Waiting for System (cloud-init) to complete first</summary>
    Pending,

    /// <summary>Actively being probed</summary>
    Checking,

    /// <summary>Check passed — service is accepting traffic</summary>
    Ready,

    /// <summary>Timeout expired without the check passing</summary>
    TimedOut,

    /// <summary>Cloud-init reported error (System service only)</summary>
    Failed
}