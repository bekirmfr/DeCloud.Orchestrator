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

    /// <summary>
    /// HMAC secret used to authenticate system VM callbacks to the orchestrator.
    /// Generated at deployment time, injected into the VM via cloud-init labels,
    /// and verified on incoming API calls (e.g., POST /api/dht/join).
    /// </summary>
    public string? AuthToken { get; set; }

    /// <summary>
    /// Combined binary/template hash stamped when this VM was deployed.
    /// Null on obligations deployed before this feature — stale detection
    /// is skipped until the next redeploy stamps it.
    /// </summary>
    public string? DeployedBinaryVersion { get; set; }

    /// <summary>
    /// Combined binary/template hash last reported by the node agent via heartbeat.
    /// Updated every heartbeat. When it diverges from DeployedBinaryVersion,
    /// VerifyActiveAsync triggers a graceful VM redeploy.
    /// Null until the node sends its first heartbeat with binary version info.
    /// </summary>
    public string? CurrentBinaryVersion { get; set; }
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
