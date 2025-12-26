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

    // Connection info
    public string Endpoint { get; set; } = string.Empty;  // How orchestrator reaches the node
    public string PublicIp { get; set; } = string.Empty;
    public int AgentPort { get; set; } = 5050;

    // Resources
    public NodeResources TotalResources { get; set; } = new();
    public NodeResources AvailableResources { get; set; } = new();
    public NodeResources ReservedResources { get; set; } = new();

    // State
    public NodeStatus Status { get; set; } = NodeStatus.Offline;
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public string AgentVersion { get; set; } = string.Empty;

    // Capabilities
    public List<string> SupportedImages { get; set; } = new();
    public bool SupportsGpu { get; set; }
    public GpuInfo? GpuInfo { get; set; }

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
/// Node resource allocation with point-based CPU tracking
/// </summary>
public class NodeResources
{
    // ========================================
    // LEGACY FIELDS (Backward Compatible)
    // ========================================
    public int CpuCores { get; set; }
    public long MemoryMb { get; set; }
    public long StorageGb { get; set; }
    public long BandwidthMbps { get; set; }

    // ========================================
    // NEW: POINT-BASED CPU ALLOCATION
    // ========================================

    /// <summary>
    /// Total compute points available (CpuCores × 8)
    /// Each physical core = 8 compute points
    /// Example: 2-core node = 16 total points
    /// </summary>
    public int TotalComputePoints { get; set; }

    /// <summary>
    /// Currently reserved compute points (sum of all VM point costs)
    /// </summary>
    public int ReservedComputePoints { get; set; }

    /// <summary>
    /// Available compute points for new VMs
    /// </summary>
    public int AvailableComputePoints => TotalComputePoints - ReservedComputePoints;

    /// <summary>
    /// Initialize compute points from physical CPU cores
    /// Call this during node registration
    /// </summary>
    public void InitializeComputePoints()
    {
        TotalComputePoints = CpuCores * 8;
        // ReservedComputePoints starts at 0 for new nodes
    }
}

public class GpuInfo
{
    public string Model { get; set; } = string.Empty;
    public int VramMb { get; set; }
    public int Count { get; set; } = 1;
    public string Driver { get; set; } = string.Empty;
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
    /// Deterministic node ID (generated from MachineId + WalletAddress)
    /// Optional: if not provided, orchestrator will calculate it
    /// If provided, must match calculated value for validation
    /// </summary>
    public string? NodeId { get; set; }

    /// <summary>
    /// Machine fingerprint (from /etc/machine-id or fallback)
    /// Required for node ID generation and validation
    /// </summary>
    public required string MachineId { get; set; }

    /// <summary>
    /// Wallet address for ownership and billing
    /// Required - cannot be null address (0x000...000)
    /// </summary>
    public required string WalletAddress { get; set; }

    public required string Name { get; set; }
    public required string PublicIp { get; set; }
    public required int AgentPort { get; set; }
    public required NodeResources Resources { get; set; }
    public required string AgentVersion { get; set; }
    public List<string> SupportedImages { get; set; } = new();
    public bool SupportsGpu { get; set; }
    public GpuInfo? GpuInfo { get; set; }
    public string? Region { get; set; }
    public string? Zone { get; set; }
}

public record NodeRegistrationResponse(
    string NodeId,
    string AuthToken,
    TimeSpan HeartbeatInterval
);

/// <summary>
/// Node heartbeat with enhanced VM information
/// </summary>
public record NodeHeartbeat(
    string NodeId,
    NodeMetrics Metrics,
    NodeResources AvailableResources,
    List<HeartbeatVmInfo>? ActiveVms = null  // detailed VM information
);

/// <summary>
/// VM information included in heartbeat
/// </summary>
public class HeartbeatVmInfo
{
    public string VmId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? TenantId { get; set; }  // Owner ID
    public string State { get; set; } = string.Empty;  // "Running", "Stopped", etc.
    public string? IpAddress { get; set; }
    public double? CpuUsagePercent { get; set; }
    public DateTime? StartedAt { get; set; }

    // Optional extended info
    // Resource specifications
    public int? VCpus { get; set; }
    public long? MemoryBytes { get; set; }
    public long? DiskBytes { get; set; }

    // Complete recovery fields
    /// <summary>
    /// VNC port for console access (e.g., "5900")
    /// </summary>
    public string? VncPort { get; set; }

    /// <summary>
    /// MAC address assigned to VM's network interface
    /// </summary>
    public string? MacAddress { get; set; }

    /// <summary>
    /// Wallet-encrypted password for VM access
    /// Format: base64(iv):base64(ciphertext):base64(tag)
    /// </summary>
    public string? EncryptedPassword { get; set; }
}

public record NodeHeartbeatResponse(
    bool Acknowledged,
    List<NodeCommand>? PendingCommands
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