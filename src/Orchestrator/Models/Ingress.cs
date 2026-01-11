using System.Text.Json.Serialization;

namespace Orchestrator.Models;

/// <summary>
/// Configuration for the central ingress gateway with wildcard domain support.
/// Enables automatic subdomain routing like {vm-name}.vms.decloud.io
/// </summary>
public class CentralIngressOptions
{
    /// <summary>
    /// Base domain for automatic VM subdomains (e.g., "vms.decloud.io")
    /// VMs will get URLs like: {vm-name}.vms.decloud.io
    /// </summary>
    public string BaseDomain { get; set; } = "";

    /// <summary>
    /// Whether central ingress is enabled
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Caddy Admin API URL (localhost:2019)
    /// </summary>
    public string CaddyAdminUrl { get; set; } = "http://localhost:2019";

    /// <summary>
    /// Email for Let's Encrypt wildcard certificate
    /// </summary>
    public string AcmeEmail { get; set; } = "";

    /// <summary>
    /// Use Let's Encrypt staging for testing
    /// </summary>
    public bool UseAcmeStaging { get; set; } = false;

    /// <summary>
    /// DNS provider for ACME DNS-01 challenge (required for wildcard certs)
    /// Supported: cloudflare, route53, digitalocean, etc.
    /// </summary>
    public string DnsProvider { get; set; } = "cloudflare";

    /// <summary>
    /// DNS provider API token (for DNS-01 challenge)
    /// </summary>
    public string DnsApiToken { get; set; } = "";

    /// <summary>
    /// Default port on VMs to route to (can be overridden per-VM)
    /// </summary>
    public int DefaultTargetPort { get; set; } = 80;

    /// <summary>
    /// Pattern for generating subdomains from VM info
    /// Placeholders: {name}, {id}, {id8}, {owner8}
    /// Default: "{name}" → myapp.vms.decloud.io
    /// </summary>
    public string SubdomainPattern { get; set; } = "{name}-{id4}";

    /// <summary>
    /// Whether to auto-register VMs when they start
    /// </summary>
    public bool AutoRegisterOnStart { get; set; } = true;

    /// <summary>
    /// Whether to auto-remove routes when VMs stop/delete
    /// </summary>
    public bool AutoRemoveOnStop { get; set; } = true;

    /// <summary>
    /// Timeout for proxying to nodes (seconds)
    /// </summary>
    public int ProxyTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Enable WebSocket support for all routes
    /// </summary>
    public bool EnableWebSocket { get; set; } = true;

    /// <summary>
    /// Global rate limit per subdomain (requests/minute, 0 = disabled)
    /// </summary>
    public int RateLimitPerMinute { get; set; } = 0;
}

/// <summary>
/// Represents a route in the central ingress gateway
/// </summary>
public class CentralIngressRoute
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Full subdomain (e.g., "myapp.vms.decloud.io")
    /// </summary>
    public string Subdomain { get; set; } = "";

    /// <summary>
    /// The VM this route points to
    /// </summary>
    public string VmId { get; set; } = "";

    /// <summary>
    /// VM name (for display)
    /// </summary>
    public string VmName { get; set; } = "";

    /// <summary>
    /// Owner wallet address
    /// </summary>
    public string OwnerWallet { get; set; } = "";

    /// <summary>
    /// Node hosting the VM
    /// </summary>
    public string NodeId { get; set; } = "";

    /// <summary>
    /// Node's public IP
    /// </summary>
    public string NodePublicIp { get; set; } = "";

    /// <summary>
    /// VM's private IP on the node
    /// </summary>
    public string VmPrivateIp { get; set; } = "";

    /// <summary>
    /// Target port on the VM
    /// </summary>
    public int TargetPort { get; set; } = 80;

    /// <summary>
    /// Whether the route is currently active
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CentralRouteStatus Status { get; set; } = CentralRouteStatus.Pending;

    /// <summary>
    /// Full public URL
    /// </summary>
    public string PublicUrl => $"https://{Subdomain}";

    /// <summary>
    /// When this route was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this route was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total requests served (updated periodically)
    /// </summary>
    public long TotalRequests { get; set; }
}

public enum CentralRouteStatus
{
    Pending,
    Active,
    Paused,
    Error,
    Deleted
}

/// <summary>
/// VM's ingress configuration (stored on VM record)
/// </summary>
public class VmIngressConfig
{
    /// <summary>
    /// Auto-assigned subdomain (e.g., "myapp.vms.decloud.io")
    /// </summary>
    public string? DefaultSubdomain { get; set; }

    /// <summary>
    /// Target port for the default subdomain
    /// </summary>
    public int DefaultPort { get; set; } = 80;

    /// <summary>
    /// Whether the default subdomain is enabled
    /// </summary>
    public bool DefaultSubdomainEnabled { get; set; } = true;

    /// <summary>
    /// Custom domains configured for this VM (via node-level Caddy)
    /// </summary>
    public List<string> CustomDomains { get; set; } = new();
}

#region DTOs

public record SetVmIngressPortRequest(
    int Port
);

public record VmIngressResponse(
    string VmId,
    string VmName,
    string? DefaultUrl,
    int DefaultPort,
    bool DefaultEnabled,
    List<string> CustomDomains,
    string? NodePublicIp
);

public record CentralIngressStatusResponse(
    bool Enabled,
    string? BaseDomain,
    int TotalRoutes,
    int ActiveRoutes,
    bool CaddyHealthy,
    string? WildcardCertStatus
);

#endregion

/// <summary>
/// Ingress rule for routing external traffic to a VM
/// </summary>
public class IngressRule
{
    public string Id { get; set; } = string.Empty;
    public string VmId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string OwnerWallet { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public int TargetPort { get; set; } = 80;
    public bool EnableTls { get; set; } = true;
    public bool ForceHttps { get; set; } = true;
    public bool EnableWebSocket { get; set; } = true;
    public string? PathPrefix { get; set; }
    public bool StripPathPrefix { get; set; }
    public int RateLimitPerMinute { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IngressStatus Status { get; set; } = IngressStatus.Pending;

    public string? StatusMessage { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TlsCertStatus TlsStatus { get; set; } = TlsCertStatus.Pending;

    public DateTime? TlsExpiresAt { get; set; }
    public string? PublicUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public long TotalRequests { get; set; }
}

public enum IngressStatus
{
    Pending,
    Configuring,
    Active,
    Paused,
    Error,
    Deleting,
    Deleted
}

public enum TlsCertStatus
{
    Disabled,
    Pending,
    Provisioning,
    Valid,
    ExpiringSoon,
    Expired,
    Failed
}

#region DTOs

public record CreateIngressRequest(
    string VmId,
    string Domain,
    int TargetPort = 80,
    bool EnableTls = true,
    bool ForceHttps = true,
    bool EnableWebSocket = true,
    string? PathPrefix = null,
    bool StripPathPrefix = false,
    int RateLimitPerMinute = 0
);

public record UpdateIngressRequest(
    int? TargetPort = null,
    bool? EnableTls = null,
    bool? ForceHttps = null,
    bool? EnableWebSocket = null,
    string? PathPrefix = null,
    bool? StripPathPrefix = null,
    int? RateLimitPerMinute = null,
    List<string>? AllowedIps = null,
    Dictionary<string, string>? CustomHeaders = null
);

public record IngressResponse(
    string Id,
    string VmId,
    string NodeId,
    string Domain,
    int TargetPort,
    bool EnableTls,
    IngressStatus Status,
    string? StatusMessage,
    TlsCertStatus TlsStatus,
    DateTime? TlsExpiresAt,
    string? PublicUrl,
    string? NodePublicIp,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long TotalRequests
);

public record IngressOperationResult(
    bool Success,
    string? IngressId = null,
    string? Error = null,
    IngressResponse? Ingress = null
);

public record IngressListResponse(
    List<IngressResponse> Items,
    int TotalCount
);

#endregion