using MongoDB.Bson.Serialization.Attributes;

namespace Orchestrator.Models;

/// <summary>
/// VM Template for marketplace - Pre-configured VM setup.
/// Templates can be platform-curated or user-created (community).
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
    /// Wallet address for template revenue payouts.
    /// Used by the escrow contract to credit earnings from paid template deployments.
    /// </summary>
    public string? AuthorRevenueWallet { get; set; }

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
    /// Template visibility: Public (in marketplace) or Private (author-only)
    /// </summary>
    public TemplateVisibility Visibility { get; set; } = TemplateVisibility.Public;

    /// <summary>
    /// Whether this template is featured on the marketplace
    /// </summary>
    public bool IsFeatured { get; set; }

    /// <summary>
    /// Whether this template has been verified/reviewed by the DeCloud team
    /// </summary>
    public bool IsVerified { get; set; } = true;

    /// <summary>
    /// Whether this is a community (user-created) template vs platform-curated
    /// </summary>
    public bool IsCommunity { get; set; }


    // ============================================
    // TIMESTAMPS
    // ============================================

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;


    // ============================================
    // PRICING (Infrastructure + Template Fee)
    // ============================================

    /// <summary>
    /// Estimated infrastructure cost per hour based on recommended spec
    /// </summary>
    public decimal EstimatedCostPerHour { get; set; }

    /// <summary>
    /// Template pricing model: Free or PerDeploy
    /// </summary>
    public TemplatePricingModel PricingModel { get; set; } = TemplatePricingModel.Free;

    /// <summary>
    /// One-time fee charged per deployment (USDC).
    /// Only applies when PricingModel == PerDeploy.
    /// Settled via escrow reportUsage() on successful VM boot.
    /// </summary>
    public decimal TemplatePrice { get; set; }

    // ============================================
    // BANDWIDTH
    // ============================================

    /// <summary>
    /// Default bandwidth tier for this template.
    /// Templates like "Private Browser" default to Standard (50 Mbps).
    /// </summary>
    public BandwidthTier DefaultBandwidthTier { get; set; } = BandwidthTier.Unmetered;


    // ============================================
    // RATINGS (denormalized for fast marketplace reads)
    // ============================================

    /// <summary>
    /// Average rating (1.0 - 5.0), recalculated on review changes
    /// </summary>
    public double AverageRating { get; set; }

    /// <summary>
    /// Total number of reviews
    /// </summary>
    public int TotalReviews { get; set; }

    /// <summary>
    /// Distribution of ratings: index 0 = 1-star count, index 4 = 5-star count
    /// </summary>
    public int[] RatingDistribution { get; set; } = new int[5];
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
/// Template lifecycle status
/// </summary>
public enum TemplateStatus
{
    /// <summary>
    /// Template is being created/edited, not visible in marketplace.
    /// Author can test-deploy their own draft templates.
    /// </summary>
    Draft,

    /// <summary>
    /// Template is live (visible per Visibility setting)
    /// </summary>
    Published,

    /// <summary>
    /// Template is no longer maintained/available
    /// </summary>
    Archived
}

/// <summary>
/// Template visibility in the marketplace
/// </summary>
public enum TemplateVisibility
{
    /// <summary>
    /// Visible in marketplace search, deployable by anyone
    /// </summary>
    Public,

    /// <summary>
    /// Not in marketplace, only the author can see and deploy
    /// </summary>
    Private
}

/// <summary>
/// How template authors charge for their templates
/// </summary>
public enum TemplatePricingModel
{
    /// <summary>
    /// No template fee — deployer only pays infrastructure costs
    /// </summary>
    Free,

    /// <summary>
    /// One-time fee per deployment, settled via escrow on VM boot success.
    /// 85% to template author, 15% platform fee.
    /// </summary>
    PerDeploy
}
