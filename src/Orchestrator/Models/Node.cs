using System.Text.Json.Serialization;

namespace Orchestrator.Models;

/// <summary>
/// Represents a compute node in the decentralized network
/// </summary>
public class Node
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
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
/// Detailed VM information sent in heartbeats for state recovery
/// </summary>
public class NodeVmInfo
{
    public string VmId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? TenantId { get; set; }
    public string State { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public double? CpuUsagePercent { get; set; }
    public DateTime? StartedAt { get; set; }
    public int? VCpus { get; set; }
    public long? MemoryBytes { get; set; }
    public long? DiskBytes { get; set; }
}

public class NodeResources
{
    public int CpuCores { get; set; }
    public long MemoryMb { get; set; }
    public long StorageGb { get; set; }
    public long BandwidthMbps { get; set; }
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
public record NodeRegistrationRequest(
    string Name,
    string WalletAddress,
    string PublicIp,
    int AgentPort,
    NodeResources Resources,
    string AgentVersion,
    List<string> SupportedImages,
    bool SupportsGpu,
    GpuInfo? GpuInfo,
    string Region,
    string Zone
);

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