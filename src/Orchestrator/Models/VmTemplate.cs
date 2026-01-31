using MongoDB.Bson.Serialization.Attributes;

namespace Orchestrator.Models;

/// <summary>
/// VM Template for marketplace - Pre-configured VM setup
/// </summary>
public class VmTemplate
{
    // ============================================
    // IDENTITY
    // ============================================
    
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Display name (e.g., "Stable Diffusion WebUI")
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// URL-friendly slug (e.g., "stable-diffusion-webui")
    /// </summary>
    public string Slug { get; set; } = string.Empty;
    
    /// <summary>
    /// Template version (e.g., "1.0.0")
    /// </summary>
    public string Version { get; set; } = "1.0.0";
    
    
    // ============================================
    // METADATA
    // ============================================
    
    /// <summary>
    /// Short description (280 chars max for card display)
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Full markdown description with usage instructions
    /// </summary>
    public string? LongDescription { get; set; }
    
    /// <summary>
    /// Category slug (e.g., "ai-ml", "web-apps", "dev-tools")
    /// </summary>
    public string Category { get; set; } = string.Empty;
    
    /// <summary>
    /// Tags for filtering and search
    /// </summary>
    public List<string> Tags { get; set; } = new();
    
    /// <summary>
    /// Icon/logo URL or emoji
    /// </summary>
    public string? IconUrl { get; set; }
    
    /// <summary>
    /// Screenshot URLs for template detail page
    /// </summary>
    public List<string> Screenshots { get; set; } = new();
    
    
    // ============================================
    // AUTHOR & ATTRIBUTION
    // ============================================
    
    /// <summary>
    /// Author ID (wallet address or "platform" for curated)
    /// </summary>
    public string AuthorId { get; set; } = "platform";
    
    /// <summary>
    /// Display name for author
    /// </summary>
    public string AuthorName { get; set; } = "DeCloud";
    
    /// <summary>
    /// License type (MIT, GPL-3.0, Apache-2.0, etc.)
    /// </summary>
    public string? License { get; set; }
    
    /// <summary>
    /// Source code or documentation URL
    /// </summary>
    public string? SourceUrl { get; set; }
    
    
    // ============================================
    // VM SPECIFICATIONS
    // ============================================
    
    /// <summary>
    /// Minimum viable specification
    /// </summary>
    public VmSpec MinimumSpec { get; set; } = new();
    
    /// <summary>
    /// Recommended specification for optimal performance
    /// </summary>
    public VmSpec RecommendedSpec { get; set; } = new();
    
    
    // ============================================
    // REQUIREMENTS
    // ============================================
    
    /// <summary>
    /// Whether this template requires GPU
    /// </summary>
    public bool RequiresGpu { get; set; }
    
    /// <summary>
    /// GPU requirement description (e.g., "RTX 3060 or better")
    /// </summary>
    public string? GpuRequirement { get; set; }
    
    /// <summary>
    /// Required node capabilities (e.g., ["nvme", "high-bandwidth"])
    /// </summary>
    public List<string> RequiredCapabilities { get; set; } = new();
    
    
    // ============================================
    // CLOUD-INIT CONFIGURATION
    // ============================================
    
    /// <summary>
    /// Cloud-init YAML template with variable substitution
    /// Variables: ${DECLOUD_VM_ID}, ${DECLOUD_VM_NAME}, ${DECLOUD_PASSWORD}, etc.
    /// </summary>
    public string CloudInitTemplate { get; set; } = string.Empty;
    
    /// <summary>
    /// Default environment variables for the template
    /// </summary>
    public Dictionary<string, string> DefaultEnvironmentVariables { get; set; } = new();
    
    
    // ============================================
    // ACCESS & PORTS
    // ============================================
    
    /// <summary>
    /// Ports exposed by this template
    /// </summary>
    public List<TemplatePort> ExposedPorts { get; set; } = new();
    
    /// <summary>
    /// Default access URL template (e.g., "https://{vm-id}.decloud.app:7860")
    /// </summary>
    public string? DefaultAccessUrl { get; set; }
    
    /// <summary>
    /// Default username if application has one
    /// </summary>
    public string? DefaultUsername { get; set; }
    
    /// <summary>
    /// Whether to use DeCloud's generated password system
    /// </summary>
    public bool UseGeneratedPassword { get; set; } = true;
    
    
    // ============================================
    // MARKETPLACE STATS
    // ============================================
    
    /// <summary>
    /// Total number of times this template has been deployed
    /// </summary>
    public int DeploymentCount { get; set; }
    
    /// <summary>
    /// When this template was last deployed
    /// </summary>
    public DateTime? LastDeployedAt { get; set; }
    
    
    // ============================================
    // STATUS & VISIBILITY
    // ============================================
    
    /// <summary>
    /// Template status (Draft, Published, Archived)
    /// </summary>
    public TemplateStatus Status { get; set; } = TemplateStatus.Published;
    
    /// <summary>
    /// Whether this template is featured on the marketplace
    /// </summary>
    public bool IsFeatured { get; set; }
    
    /// <summary>
    /// Whether this template has been verified by DeCloud team
    /// </summary>
    public bool IsVerified { get; set; } = true;
    
    
    // ============================================
    // TIMESTAMPS
    // ============================================
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    
    // ============================================
    // PRICING
    // ============================================
    
    /// <summary>
    /// Estimated cost per hour based on recommended spec
    /// </summary>
    public decimal EstimatedCostPerHour { get; set; }
}

/// <summary>
/// Template port configuration for exposed services
/// </summary>
public class TemplatePort
{
    /// <summary>
    /// Port number (1-65535)
    /// </summary>
    public int Port { get; set; }
    
    /// <summary>
    /// Protocol (http, https, tcp, udp)
    /// </summary>
    public string Protocol { get; set; } = "http";
    
    /// <summary>
    /// Human-readable description
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether this port should be publicly accessible via ingress
    /// </summary>
    public bool IsPublic { get; set; } = true;
}

/// <summary>
/// Template status
/// </summary>
public enum TemplateStatus
{
    /// <summary>
    /// Template is being created/edited
    /// </summary>
    Draft,
    
    /// <summary>
    /// Template is live in marketplace
    /// </summary>
    Published,
    
    /// <summary>
    /// Template is no longer maintained/available
    /// </summary>
    Archived
}
