using MongoDB.Bson.Serialization.Attributes;

namespace Orchestrator.Models;

/// <summary>
/// Universal review model for any marketplace resource (templates, nodes, etc.).
/// Stored as a separate MongoDB collection for flexible querying.
/// Reviews require eligibility proof (e.g., a deployment) to prevent fakes.
/// </summary>
public class MarketplaceReview
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // ============================================
    // RESOURCE REFERENCE
    // ============================================

    /// <summary>
    /// Type of resource being reviewed: "template", "node", etc.
    /// </summary>
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>
    /// ID of the resource being reviewed (template ID, node ID, etc.)
    /// </summary>
    public string ResourceId { get; set; } = string.Empty;

    // ============================================
    // REVIEWER
    // ============================================

    /// <summary>
    /// Wallet address of the reviewer
    /// </summary>
    public string ReviewerId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the reviewer (optional)
    /// </summary>
    public string? ReviewerName { get; set; }

    // ============================================
    // ELIGIBILITY PROOF
    // ============================================

    /// <summary>
    /// Proof that the reviewer is eligible to review this resource.
    /// For templates: must have deployed a VM from it.
    /// For nodes: must have had a VM run on it.
    /// </summary>
    public ReviewEligibilityProof EligibilityProof { get; set; } = new();

    // ============================================
    // REVIEW CONTENT
    // ============================================

    /// <summary>
    /// Rating from 1 to 5
    /// </summary>
    public int Rating { get; set; }

    /// <summary>
    /// Optional review headline
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Optional review body text
    /// </summary>
    public string? Comment { get; set; }

    // ============================================
    // MODERATION & STATUS
    // ============================================

    /// <summary>
    /// Whether the eligibility proof was validated server-side
    /// </summary>
    public bool IsVerified { get; set; }

    /// <summary>
    /// Review moderation status
    /// </summary>
    public ReviewStatus Status { get; set; } = ReviewStatus.Active;

    // ============================================
    // TIMESTAMPS
    // ============================================

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Proof that a reviewer is eligible to review a resource.
/// Each resource type defines its own proof requirements.
/// </summary>
public class ReviewEligibilityProof
{
    /// <summary>
    /// Type of proof: "deployment" (deployed a template), "vm_usage" (ran VM on node), etc.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Reference ID for the proof (VM ID, deployment ID, etc.)
    /// </summary>
    public string ReferenceId { get; set; } = string.Empty;

    /// <summary>
    /// When the proof was validated by the server
    /// </summary>
    public DateTime? VerifiedAt { get; set; }
}

/// <summary>
/// Review moderation status
/// </summary>
public enum ReviewStatus
{
    /// <summary>
    /// Review is visible and counted in aggregates
    /// </summary>
    Active,

    /// <summary>
    /// Review has been flagged for moderation
    /// </summary>
    Flagged,

    /// <summary>
    /// Review has been removed by moderation
    /// </summary>
    Removed
}
