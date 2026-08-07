namespace Orchestrator.Models;

/// <summary>
/// Fail-closed API projection of <see cref="Node"/> for the user-facing node
/// endpoints (GET /api/nodes and GET /api/nodes/{id}).
///
/// The internal <see cref="Node"/> model embeds secrets — system-VM auth tokens
/// and ed25519/WireGuard PRIVATE KEYS (SystemVmObligations), the CGNAT relay
/// token and a WireGuard config with a PRIVATE KEY (CgnatInfo), the relay
/// WireGuard PRIVATE KEY (RelayInfo), and the node's API-key hash. Returning the
/// model directly leaked all of these to any authenticated caller. This DTO is
/// the fix: a field reaches a caller ONLY if it is explicitly listed here, so a
/// new field on Node cannot leak by default.
///
/// Owner-aware. Every authenticated caller gets the marketplace/scheduling tier
/// (identity, availability, uptime, location). The node's operator (wallet match)
/// and admins additionally get the operational tier (network, resource pools,
/// role health, earnings). Secrets are in NO tier — not even for the owner or an
/// admin; those live on the node, not in a browser.
/// </summary>
public sealed class NodeView
{
    // ---- Tier 1: every authenticated caller ----
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    // The owner key. The client compares this to the session wallet to decide
    // ownership, so it must be present for everyone.
    public string WalletAddress { get; set; } = string.Empty;
    public string? Description { get; set; }
    public NodeStatus Status { get; set; }
    public bool IsSchedulingReady { get; set; }
    public double UptimePercentage { get; set; }
    public NodeLocality? Locality { get; set; }   // region/country/zone/jurisdiction — no secrets
    public List<string> Tags { get; set; } = new();
    public ResourceSnapshot? TotalResources { get; set; }
    public ResourceSnapshot? AllocatedResources { get; set; }

    // ---- Tier 2: owner or admin only (null → omitted by WhenWritingNull) ----
    public string? PublicIp { get; set; }
    public int? AgentPort { get; set; }
    public string? AgentVersion { get; set; }
    public string? Architecture { get; set; }
    public DateTime? RegisteredAt { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public int? TotalVmsHosted { get; set; }
    public int? SuccessfulVmCompletions { get; set; }
    public bool? IsBehindCgnat { get; set; }
    public ResourceSnapshot? UsedResources { get; set; }
    public ResourceSnapshot? ReservedResources { get; set; }
    public decimal? PendingPayout { get; set; }
    public decimal? TotalEarned { get; set; }
    public RelayView? RelayInfo { get; set; }
    public DhtView? DhtInfo { get; set; }
    public BlockStoreView? BlockStoreInfo { get; set; }

    /// <param name="ownerOrAdmin">
    /// true when the caller owns this node (wallet match) or is an admin.
    /// Gates the operational tier. Never gates secrets — those are not projected
    /// at all.
    /// </param>
    public static NodeView From(Node n, bool ownerOrAdmin)
    {
        var v = new NodeView
        {
            Id = n.Id,
            Name = n.Name,
            WalletAddress = n.WalletAddress,
            Description = n.Description,
            Status = n.Status,
            IsSchedulingReady = n.IsSchedulingReady,
            UptimePercentage = n.UptimePercentage,
            Locality = n.Locality,
            Tags = n.Tags,
            TotalResources = n.TotalResources,
            AllocatedResources = n.AllocatedResources,
        };

        if (ownerOrAdmin)
        {
            v.PublicIp = n.PublicIp;
            v.AgentPort = n.AgentPort;
            v.AgentVersion = n.AgentVersion;
            v.Architecture = n.Architecture;
            v.RegisteredAt = n.RegisteredAt;
            v.LastHeartbeat = n.LastHeartbeat;
            v.TotalVmsHosted = n.TotalVmsHosted;
            v.SuccessfulVmCompletions = n.SuccessfulVmCompletions;
            v.IsBehindCgnat = n.IsBehindCgnat;
            v.UsedResources = n.UsedResources;
            v.ReservedResources = n.ReservedResources;
            v.PendingPayout = n.PendingPayout;
            v.TotalEarned = n.TotalEarned;
            v.RelayInfo = RelayView.From(n.RelayInfo);
            v.DhtInfo = DhtView.From(n.DhtInfo);
            v.BlockStoreInfo = BlockStoreView.From(n.BlockStoreInfo);
        }

        return v;
    }
}

/// <summary>Relay role, projected WITHOUT WireGuardPrivateKey.</summary>
public sealed class RelayView
{
    public string? RelayVmId { get; set; }
    public string? WireGuardEndpoint { get; set; }
    public string? WireGuardPublicKey { get; set; }
    public string? TunnelIp { get; set; }
    public int RelaySubnet { get; set; }
    public int MaxCapacity { get; set; }
    public int CurrentLoad { get; set; }
    public List<string> ConnectedNodeIds { get; set; } = new();
    public decimal RelayFeePerHour { get; set; }
    public string? Region { get; set; }
    public RelayStatus Status { get; set; }
    public DateTime LastHealthCheck { get; set; }
    // WireGuardPrivateKey is deliberately NOT projected.

    public static RelayView? From(RelayNodeInfo? r) => r is null ? null : new RelayView
    {
        RelayVmId = r.RelayVmId,
        WireGuardEndpoint = r.WireGuardEndpoint,
        WireGuardPublicKey = r.WireGuardPublicKey,
        TunnelIp = r.TunnelIp,
        RelaySubnet = r.RelaySubnet,
        MaxCapacity = r.MaxCapacity,
        CurrentLoad = r.CurrentLoad,
        ConnectedNodeIds = r.ConnectedNodeIds,
        RelayFeePerHour = r.RelayFeePerHour,
        Region = r.Region,
        Status = r.Status,
        LastHealthCheck = r.LastHealthCheck,
    };
}

/// <summary>DHT role. No secrets on the source, but projected for fail-closed consistency.</summary>
public sealed class DhtView
{
    public string? DhtVmId { get; set; }
    public string? PeerId { get; set; }
    public string? ListenAddress { get; set; }
    public int ApiPort { get; set; }
    public int BootstrapPeerCount { get; set; }
    public int ConnectedPeers { get; set; }
    public DhtStatus Status { get; set; }
    public DateTime? LastHealthCheck { get; set; }

    public static DhtView? From(DhtNodeInfo? d) => d is null ? null : new DhtView
    {
        DhtVmId = d.DhtVmId,
        PeerId = d.PeerId,
        ListenAddress = d.ListenAddress,
        ApiPort = d.ApiPort,
        BootstrapPeerCount = d.BootstrapPeerCount,
        ConnectedPeers = d.ConnectedPeers,
        Status = d.Status,
        LastHealthCheck = d.LastHealthCheck,
    };
}

/// <summary>Block-store role. No secrets on the source, projected for consistency.</summary>
public sealed class BlockStoreView
{
    public string? BlockStoreVmId { get; set; }
    public string? PeerId { get; set; }
    public string? ListenAddress { get; set; }
    public int ApiPort { get; set; }
    public long CapacityBytes { get; set; }
    public long UsedBytes { get; set; }
    public int BlockCount { get; set; }
    public BlockStoreStatus Status { get; set; }
    public DateTime? LastHealthCheck { get; set; }

    public static BlockStoreView? From(BlockStoreInfo? b) => b is null ? null : new BlockStoreView
    {
        BlockStoreVmId = b.BlockStoreVmId,
        PeerId = b.PeerId,
        ListenAddress = b.ListenAddress,
        ApiPort = b.ApiPort,
        CapacityBytes = b.CapacityBytes,
        UsedBytes = b.UsedBytes,
        BlockCount = b.BlockCount,
        Status = b.Status,
        LastHealthCheck = b.LastHealthCheck,
    };
}
