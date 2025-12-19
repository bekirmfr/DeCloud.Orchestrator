using Orchestrator.Services;
using System.Text.Json.Serialization;

namespace Orchestrator.Models;

/// <summary>
/// Represents a Virtual Machine in the network
/// </summary>
public class VirtualMachine
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    
    // Ownership
    public string OwnerId { get; set; } = string.Empty;        // User/tenant ID
    public string OwnerWallet { get; set; } = string.Empty;    // Wallet address
    
    // Placement
    public string? NodeId { get; set; }                        // Which node it's running on
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

    /// <summary>
    /// Ingress configuration for this VM (auto-subdomain and custom domains)
    /// </summary>
    public VmIngressConfig? IngressConfig { get; set; }

    // Access
    public VmAccessInfo? AccessInfo { get; set; }
    
    // Billing
    public VmBillingInfo BillingInfo { get; set; } = new();
    
    // Runtime metrics (updated periodically)
    public VmMetrics? LatestMetrics { get; set; }
    
    // Tags/Labels
    public Dictionary<string, string> Labels { get; set; } = new();

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
    public int CpuCores { get; set; } = 1;
    public long MemoryMb { get; set; } = 1024;
    public long DiskGb { get; set; } = 20;
    public bool RequiresGpu { get; set; }
    public string? GpuModel { get; set; }

    // Scheduling preferences and requirements
    /// <summary>
    /// Preferred quality tier for scheduling (Guaranteed, Standard, Burstable)
    /// Default: Standard
    /// </summary>
    public QualityTier QualityTier { get; set; } = QualityTier.Standard;

    /// <summary>
    /// Preferred region for deployment (e.g., "us-west", "eu-central")
    /// If null, any region is acceptable
    /// </summary>
    public string? PreferredRegion { get; set; }

    /// <summary>
    /// Preferred availability zone within region (e.g., "us-west-2a")
    /// If null, any zone in preferred region is acceptable
    /// </summary>
    public string? PreferredZone { get; set; }

    /// <summary>
    /// If true, ONLY schedule on nodes in PreferredRegion (hard requirement)
    /// If false, PreferredRegion is just a preference (soft requirement)
    /// Default: false
    /// </summary>
    public bool RequirePreferredRegion { get; set; } = false;

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

    // Image
    public string ImageId { get; set; } = string.Empty;       // e.g., "ubuntu-22.04"
    public string? ImageUrl { get; set; }                     // Custom image URL
    
    // SSH key for access
    public string? SshPublicKey { get; set; }
    /// <summary>
    /// Human-readable plaintext password (only set during creation, then cleared)
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Wallet-encrypted password (stored permanently)
    /// Format: base64(iv):base64(ciphertext):base64(tag)
    /// </summary>
    public string? EncryptedPassword { get; set; }

    /// <summary>
    /// Whether the password has been encrypted and stored
    /// </summary>
    public bool PasswordSecured { get; set; }
    public bool PasswordShownToUser { get; set; } = false;

    // Cloud-init / user data
    public string? UserData { get; set; }
}

public class VmNetworkConfig
{
    public string? PrivateIp { get; set; }
    public string? PublicIp { get; set; }
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

public class VmBillingInfo
{
    public decimal HourlyRateCrypto { get; set; }          // Price per hour in crypto
    public string CryptoSymbol { get; set; } = "USDC";    // Which token
    public decimal TotalBilled { get; set; }
    public decimal TotalPaid { get; set; }
    public DateTime? LastBillingAt { get; set; }
    public TimeSpan TotalRuntime { get; set; }
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
    Starting,
    Running,
    Paused,
    Suspended,
    ShuttingDown
}

// DTOs for API
public record CreateVmRequest(
    string Name,
    VmSpec Spec,
    Dictionary<string, string>? Labels = null
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
    DateTime UpdatedAt
);

public record VmDetailResponse(
    VirtualMachine Vm,
    Node? HostNode
);
