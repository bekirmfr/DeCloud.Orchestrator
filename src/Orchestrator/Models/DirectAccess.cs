namespace Orchestrator.Models;

/// <summary>
/// Smart Port Allocation configuration for a VM.
/// Enables direct TCP/UDP access via public ports (SSH, MySQL, game servers, etc.)
/// </summary>
public class VmDirectAccess
{
    /// <summary>
    /// DNS subdomain for direct access
    /// Format: {vm-name}-{id4}.direct.stackfi.tech → Node IP
    /// </summary>
    public string? Subdomain { get; set; }

    /// <summary>
    /// Full DNS name (e.g., myvm-abc1.direct.stackfi.tech)
    /// </summary>
    public string? DnsName { get; set; }

    /// <summary>
    /// Cloudflare DNS record ID for this VM
    /// </summary>
    public string? CloudflareDnsRecordId { get; set; }

    /// <summary>
    /// Port mappings for direct access
    /// Maps public ports (40000-65535) to VM internal ports
    /// </summary>
    public List<DirectAccessPortMapping> PortMappings { get; set; } = new();

    /// <summary>
    /// Whether DNS record has been created
    /// </summary>
    public bool IsDnsConfigured { get; set; }

    /// <summary>
    /// When DNS was last updated
    /// </summary>
    public DateTime? DnsUpdatedAt { get; set; }
}

/// <summary>
/// Represents a single port mapping for Smart Port Allocation
/// </summary>
public class DirectAccessPortMapping
{
    /// <summary>
    /// Unique identifier for this mapping
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Port inside the VM (e.g., 22 for SSH, 3306 for MySQL)
    /// </summary>
    public int VmPort { get; set; }

    /// <summary>
    /// Public port on the node (allocated from pool 40000-65535)
    /// </summary>
    public int PublicPort { get; set; }

    /// <summary>
    /// Protocol: TCP, UDP, or Both
    /// </summary>
    public PortProtocol Protocol { get; set; } = PortProtocol.TCP;

    /// <summary>
    /// Optional label for this mapping (e.g., "SSH", "MySQL", "Minecraft")
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// When this mapping was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this mapping is active on the node
    /// </summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Protocol for port forwarding
/// </summary>
public enum PortProtocol
{
    TCP = 1,
    UDP = 2,
    Both = 3
}

/// <summary>
/// Common service templates for quick-add functionality
/// </summary>
public static class CommonServices
{
    public static readonly Dictionary<string, (int Port, PortProtocol Protocol, string Label)> Templates = new()
    {
        ["ssh"] = (22, PortProtocol.TCP, "SSH"),
        ["rdp"] = (3389, PortProtocol.TCP, "RDP"),
        ["mysql"] = (3306, PortProtocol.TCP, "MySQL"),
        ["postgresql"] = (5432, PortProtocol.TCP, "PostgreSQL"),
        ["mongodb"] = (27017, PortProtocol.TCP, "MongoDB"),
        ["redis"] = (6379, PortProtocol.TCP, "Redis"),
        ["http"] = (80, PortProtocol.TCP, "HTTP"),
        ["https"] = (443, PortProtocol.TCP, "HTTPS"),
        ["minecraft"] = (25565, PortProtocol.Both, "Minecraft"),
        ["valheim"] = (2456, PortProtocol.Both, "Valheim"),
        ["wireguard"] = (51820, PortProtocol.UDP, "WireGuard"),
        ["openvpn"] = (1194, PortProtocol.UDP, "OpenVPN"),
        ["shadowsocks"] = (8388, PortProtocol.Both, "Shadowsocks"),
        ["teamspeak"] = (9987, PortProtocol.UDP, "TeamSpeak Voice"),
        ["mumble"] = (64738, PortProtocol.Both, "Mumble"),
        ["ftp"] = (21, PortProtocol.TCP, "FTP"),
        ["sftp"] = (22, PortProtocol.TCP, "SFTP"),
        ["smtp"] = (25, PortProtocol.TCP, "SMTP"),
        ["imap"] = (143, PortProtocol.TCP, "IMAP"),
        ["pop3"] = (110, PortProtocol.TCP, "POP3"),
    };
}

// DTOs for API

public record AllocatePortRequest(
    int VmPort,
    PortProtocol Protocol = PortProtocol.TCP,
    string? Label = null
);

public record AllocatePortResponse(
    string MappingId,
    int VmPort,
    int PublicPort,
    PortProtocol Protocol,
    string ConnectionString,  // e.g., "ssh user@myvm-abc1.direct.stackfi.tech -p 42156"
    bool Success,
    string? Error = null
);

public record RemovePortRequest(
    int VmPort
);

public record DirectAccessInfoResponse(
    string DnsName,
    List<PortMappingInfo> PortMappings,
    bool IsDnsConfigured
);

public record PortMappingInfo(
    string Id,
    int VmPort,
    int PublicPort,
    PortProtocol Protocol,
    string? Label,
    string ConnectionExample  // e.g., "mysql -h myvm-abc1.direct.stackfi.tech -P 42157"
);

public record QuickAddServiceRequest(
    string ServiceName  // e.g., "ssh", "mysql", "minecraft"
);
