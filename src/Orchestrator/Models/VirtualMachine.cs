using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using MongoDB.Bson.Serialization.Attributes;
using Orchestrator.Models.Payment;
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
    public VmCategory Category { get; set; } = VmCategory.Tenant;
    public VmRole Role { get; set; } = VmRole.General;

    /// <summary>
    /// Subdomain tier: Free (suffixed) or Premium (exact vanity name).
    /// All VMs start as Free. Premium is claimed via upgrade flow.
    /// </summary>
    public SubdomainTier SubdomainTier { get; set; } = SubdomainTier.Free;

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

    /// <summary>
    /// Source node of an in-flight migration. Set at dispatch in MigrateVmAsync
    /// when NodeId is optimistically advanced to the target; read on any
    /// migration-create failure (terminal ack, retryable ack, or timeout) to
    /// roll NodeId back to the source so the migration scan can re-evaluate.
    /// Cleared on successful migration and on rollback. Null when no migration
    /// is in flight.
    /// </summary>
    public string? MigrationSourceNodeId { get; set; }

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
    /// <summary>
    /// Last time this VM was reported in a node heartbeat.
    /// Owned exclusively by SyncVmStateFromHeartbeatAsync — stamped on every
    /// heartbeat cycle on the post-transition re-fetched object.
    /// Nullable: null until the first heartbeat is received, which prevents
    /// VMs that have never sent a heartbeat from appearing stale (DateTime.MinValue
    /// would cause LastHeartbeatAt-based checks to fire immediately).
    /// </summary>
    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>
    /// Set when this VM's placement constraints are no longer satisfied by
    /// its host node's locality — typically after the operator changes the
    /// node's country or region via re-registration.
    ///
    /// The migration scheduler treats NonCompliantSince != null as a trigger
    /// to relocate the VM to a compliant node, the same way it treats
    /// VmStatus.Error as a trigger for offline-node recovery.
    ///
    /// Cleared when the VM is successfully migrated to a compliant node, or
    /// when the node's locality is changed back to a compliant state.
    /// </summary>
    public DateTime? NonCompliantSince { get; set; }

    /// <summary>
    /// Human-readable explanation of why the VM is non-compliant.
    /// Set alongside NonCompliantSince by FlagNonCompliantVms.
    ///
    /// Examples:
    ///   "Constraint #0 failed: node.locality.jurisdictionTags contains EU — node country BR has tags [Mercosur]"
    ///   "Constraint #2 failed: node.locality.country in [DE, FR] — node is now BR"
    /// </summary>
    public string? NonComplianceReason { get; set; }

    // Networking
    public VmNetworkConfig NetworkConfig { get; set; } = new();

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
    public List<VmServiceModel> Services { get; set; } = new();

    /// <summary>
    /// True when all services (including System) report Ready.
    /// </summary>
    [BsonIgnore]
    public bool IsFullyReady => Services.Count > 0 && Services.All(s => s.Status == ServiceStatus.Ready);

    // =========================================================================
    // Lazysync & Replication State (updated each lazysync cycle)
    // =========================================================================

    /// <summary>
    /// Number of blocks in the current confirmed manifest.
    /// Updated by LazysyncManager after each successful audit cycle.
    /// Null until the first lazysync cycle completes.
    ///
    /// Used for exact block-count billing:
    ///   cost = CurrentManifestBlockCount × (CurrentManifestBlockSizeKb / 1024)
    ///          × ReplicationFactor × StoragePerMbPerHour
    ///
    /// Falls back to estimate before first cycle:
    ///   (DiskBytes × 0.05) / (BlockSizeKb × 1024)
    /// </summary>
    public int? CurrentManifestBlockCount { get; set; }

    /// <summary>
    /// Block size in KB for this VM's manifest type.
    /// Set from ManifestRecord.BlockSizeKb on first lazysync cycle.
    /// Defaults to 1024 (1 MB, VmOverlay) for the billing estimate fallback.
    /// </summary>
    public int CurrentManifestBlockSizeKb { get; set; } = BlockSizeConstants.VmOverlayKb;

    /// <summary>
    /// When the last successful lazysync cycle completed for this VM.
    /// Null for ephemeral VMs (ReplicationFactor == 0) and VMs not yet seeded.
    /// </summary>
    public DateTime? LastLazysyncAt { get; set; }

    /// <summary>
    /// Current lazysync / migration status for this VM.
    /// Drives source-offline alerting and dashboard display.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LazysyncStatus LazysyncStatus { get; set; } = LazysyncStatus.Pending;

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

    /// <summary>
    /// Bounded chronological log of significant VM lifecycle events.
    /// Capped at 100 — oldest dropped when full. Persisted to MongoDB.
    /// </summary>
    public List<VmMessage> Messages { get; set; } = new();
}

public class VmSpec
{
    public int VirtualCpuCores { get; set; } = 1;
    public long MemoryBytes { get; set; } = 2 * 1024L * 1024L * 1024L; // 2 GB
    //[BsonIgnore]
    //[JsonIgnore]
    //public long MemoryMb => MemoryBytes / (1024L * 1024L);
    public long DiskBytes { get; set; } = 20 * 1024L * 1024L * 1024L;
    //[BsonIgnore]
    //[JsonIgnore]
    //public long DiskGb => DiskBytes / (1024L * 1024L * 1024L);
    /// <summary>
    /// GPU access mode for this workload.
    /// None = no GPU, Passthrough = dedicated VFIO GPU, Proxied = shared GPU via proxy daemon.
    /// The scheduler uses this to filter nodes and the value is sent to the node agent in CreateVm payloads.
    /// </summary>
    public GpuMode GpuMode { get; set; } = GpuMode.None;

    /// <summary>
    /// VRAM quota for Proxied GPU access, in bytes.
    /// Resolved at scheduling time from the template MinimumSpec or user request.
    /// Carried through the CreateVm command and enforced by the node's GPU proxy daemon.
    /// Null = no quota. Not applicable for Passthrough mode.
    /// </summary>
    public long? GpuVramBytes { get; set; }

    /// <summary>
    /// Convenience property: true when GpuMode requires any GPU access.
    /// Used by scheduling hard-filters and marketplace queries.
    /// </summary>
    [BsonIgnore]
    [JsonIgnore]
    public bool RequiresGpu => GpuMode != GpuMode.None;

    public string? GpuModel { get; set; }

    /// <summary>
    /// Deployment mode: VirtualMachine (default) or Container (GPU sharing).
    /// Auto-selected by scheduler when node supports GPU containers but not VFIO passthrough.
    /// </summary>
    public DeploymentMode DeploymentMode { get; set; } = DeploymentMode.VirtualMachine;

    /// <summary>
    /// Container image for Container deployment mode (e.g., "ollama/ollama:latest").
    /// Populated from template or user request.
    /// </summary>
    public string? ContainerImage { get; set; }

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

    /// <summary>
    /// Scheduling constraints evaluated as a flat AND in FILTER 10 of
    /// <c>VmSchedulingService.ApplyHardFiltersAsync</c>. This is the sole
    /// scheduling mechanism for node selection requirements — locality,
    /// architecture, reputation, jurisdiction, country, GPU model, and
    /// any other per-node requirement is expressed here.
    ///
    /// <para>
    /// Each entry is a <c>{ target, operator, value }</c> triple validated
    /// at VM creation. Malformed entries are rejected with the failing
    /// constraint's index before any resource is allocated.
    /// </para>
    ///
    /// <para>
    /// Null or empty = no scheduling constraints beyond the fixed hard
    /// filters (tier eligibility, GPU mode, load, memory, obligations, KVM).
    /// </para>
    ///
    /// <para>See <c>docs/SCHEDULING.md</c> §7 for the full vocabulary.</para>
    /// </summary>
    public List<Constraint>? Constraints { get; set; }

    // ========================================
    // IMAGE & ACCESS
    // ========================================
    public string ImageId { get; set; } = string.Empty;       // e.g., "ubuntu-22.04"

    /// <summary>
    /// HTTPS URL the node downloaded the base image from. Populated at first
    /// deploy by the orchestrator (via BaseImageUrlResolver) and round-tripped
    /// from the node's heartbeat. Carried into the migration CreateVmPayload
    /// so the target node fetches from the same source. See BASE_IMAGE_DESIGN.md §4.
    /// </summary>
    public string BaseImageUrl { get; set; } = string.Empty;

    /// <summary>
    /// SHA256 of the cached base image bytes the source overlay was authored
    /// against (lowercase hex, 64 chars). Empty at first deploy if the
    /// resolver has no recorded hash for this image yet — the node computes
    /// it on first download and reports back via heartbeat
    /// (SyncVmStateFromHeartbeatAsync adopts it). Non-empty when carried into
    /// a migration CreateVmPayload: the target MUST verify strictly.
    /// See BASE_IMAGE_DESIGN.md §4.
    /// </summary>
    public string BaseImageHash { get; set; } = string.Empty;

    // SSH key for access
    public string? SshPublicKey { get; set; }

    /// <summary>
    /// SSH username for terminal access. "root" for standard platform VMs;
    /// inherited from VmTemplate.DefaultUsername for marketplace deployments
    /// (e.g. "debian", "ubuntu", "user").
    /// </summary>
    public string SshUsername { get; set; } = "root";

    /// <summary>
    /// Wallet-encrypted password (stored permanently)
    /// Format: base64(iv):base64(ciphertext):base64(tag)
    /// </summary>
    public string? WalletEncryptedPassword { get; set; }

    // Cloud-init / user data
    public string? CloudInitUserData { get; set; }

    public int MaxConnections { get; set; } = -1; // -1 = unlimited

    // ========================================
    // BANDWIDTH TIER
    // ========================================

    /// <summary>
    /// Bandwidth tier for network rate limiting.
    /// Enforced via libvirt QoS on the VM's virtio network interface.
    /// Default: Unmetered (no artificial cap)
    /// </summary>
    public BandwidthTier BandwidthTier { get; set; } = BandwidthTier.Basic;

    // =========================================================================
    // REPLICATION
    // =========================================================================

    /// <summary>
    /// Number of block store providers that must hold the VM's overlay chunks
    /// before a lazysync version is considered confirmed.
    ///
    /// Supported values:
    ///   0 — Ephemeral. Lazysync skipped entirely. VM data is lost on node failure.
    ///       Use for stateless workloads, batch jobs, CI runners.
    ///   1 — Single replica. Survives if ≥1 block store provider holds the blocks.
    ///   3 — Standard (default). Survives loss of 2 provider nodes simultaneously.
    ///   5 — High availability. Survives loss of 4 provider nodes simultaneously.
    ///       Use for databases, ML checkpoints, critical stateful services.
    ///
    /// Immutable after VM creation. Default: 3 for tenant VMs, 0 for system VMs.
    /// Affects scheduling: replicationFactor > 0 requires an Active BlockStore
    /// on the target node.
    /// </summary>
    public int ReplicationFactor { get; set; } = 0;
}

public class VmMessage
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public VmMessageLevel Level { get; set; } = VmMessageLevel.Info;
    public string Source { get; set; } = string.Empty; // "scheduler", "healthmonitor", etc.
    public string Text { get; set; } = string.Empty;
}

public enum VmMessageLevel { Info, Warning, Error }

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

public enum VmPowerState
{
    Off,
    Running,
    Paused
}

/// <summary>
/// Subdomain tier determines how VM subdomains are generated.
/// Free: base name + unique suffix (e.g., "my-app-a1b2")
/// Premium: exact name as-is, globally unique (e.g., "my-app") — future upgrade flow
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubdomainTier
{
    /// <summary>
    /// Default tier. Name gets a unique hex suffix appended.
    /// Uniqueness checked per-owner. Assigned at VM creation.
    /// </summary>
    Free,

    /// <summary>
    /// Premium vanity subdomain. Name used as-is (no suffix).
    /// Uniqueness enforced globally across all users.
    /// Claimed via separate upgrade flow on VM dashboard.
    /// </summary>
    Premium
}

/// <summary>
/// Replication and migration state for a VM's lazysync lifecycle.
/// </summary>
public enum LazysyncStatus
{
    /// <summary>
    /// Initial state. Lazysync has not yet run for this VM.
    /// Ephemeral VMs (ReplicationFactor == 0) stay here permanently.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Initial overlay seeding in progress (first lazysync cycle).
    /// confirmedVersion == 0 — no confirmed replica yet.
    /// </summary>
    Seeding = 1,

    /// <summary>
    /// All chunks in the latest manifest have ≥N providers confirmed in the DHT.
    /// VM is fully protected and can be migrated without data loss.
    /// </summary>
    Protected = 2,

    /// <summary>
    /// Replication is in progress or lagging — confirmedVersion < currentVersion.
    /// VM can still be migrated from confirmedVersion with minor data loss.
    /// </summary>
    Replicating = 3,

    /// <summary>
    /// Source node is offline. VM is being rescheduled to another node.
    /// Target node is fetching overlay blocks via bitswap.
    /// </summary>
    Migrating = 4,

    /// <summary>
    /// Source node is offline. confirmedVersion > 0 but chunks are under-replicated.
    /// Migration possible but with data loss since last confirmed version.
    /// </summary>
    Recovering = 5,

    /// <summary>
    /// Source node is offline. confirmedVersion == 0 (seeding never completed).
    /// VM disk state cannot be recovered — must be redeployed from scratch.
    /// </summary>
    Unrecoverable = 6,

    /// <summary>
    /// ReplicationFactor == 0 and source node is offline.
    /// Expected state — user opted into ephemeral semantics at creation time.
    /// No alert generated.
    /// </summary>
    Lost = 7
}

// DTOs for API
public record CreateVmRequest(
    string Name,
    VmSpec Spec,
    VmCategory Category = VmCategory.Tenant,
    VmRole Role = VmRole.General,
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
    List<VmSummaryDto> Vms,
    int TotalCount,
    int Page,
    int PageSize
);

public record VmSummaryDto(
    string Id,
    string Name,
    SubdomainTier SubdomainTier,
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
    List<VmServiceModel>? Services = null
);

public record VmDetailResponse(
    VirtualMachine Vm,
    Node? HostNode
);