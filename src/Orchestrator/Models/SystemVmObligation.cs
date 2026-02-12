using System.Text.Json.Serialization;

namespace Orchestrator.Models;

/// <summary>
/// Represents a system VM that a node is obligated to run.
/// Stored directly on the Node model. The reconciliation loop
/// converges each node toward having all its obligations Active.
/// </summary>
public class SystemVmObligation
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SystemVmRole Role { get; set; }

    /// <summary>
    /// The VM ID once deployed (null until first deployment attempt).
    /// </summary>
    public string? VmId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SystemVmStatus Status { get; set; } = SystemVmStatus.Pending;

    public DateTime? DeployedAt { get; set; }
    public DateTime? ActiveAt { get; set; }
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
}

public enum SystemVmRole
{
    Dht,
    Relay,
    BlockStore,
    Ingress
}

public enum SystemVmStatus
{
    /// <summary>Should be deployed, hasn't been yet</summary>
    Pending,

    /// <summary>VM created, waiting for Running + healthy</summary>
    Deploying,

    /// <summary>VM running and healthy</summary>
    Active,

    /// <summary>Deployment failed, needs retry</summary>
    Failed
}
