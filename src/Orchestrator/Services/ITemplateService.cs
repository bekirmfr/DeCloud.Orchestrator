using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Service for managing VM templates in the marketplace.
/// Supports both platform-curated and user-created (community) templates.
/// </summary>
public interface ITemplateService
{
    // ════════════════════════════════════════════════════════════════════════
    // Template Queries
    // ════════════════════════════════════════════════════════════════════════

    Task<VmTemplate?> GetTemplateByIdAsync(string templateId);
    Task<VmTemplate?> GetTemplateBySlugAsync(string slug);
    Task<List<VmTemplate>> GetTemplatesAsync(TemplateQuery query);
    Task<List<VmTemplate>> GetFeaturedTemplatesAsync(int limit = 10);
    Task<List<TemplateCategory>> GetCategoriesAsync();

    /// <summary>
    /// Get all templates owned by a specific author (wallet address).
    /// Returns all statuses (Draft, Published, Archived).
    /// </summary>
    Task<List<VmTemplate>> GetTemplatesByAuthorAsync(string authorId);

    // ════════════════════════════════════════════════════════════════════════
    // Template Management (Any authenticated user)
    // ════════════════════════════════════════════════════════════════════════

    Task<VmTemplate> CreateTemplateAsync(VmTemplate template);
    Task<VmTemplate> UpdateTemplateAsync(VmTemplate template, bool isAdmin = false);

    /// <summary>
    /// Restore server-owned fields (author identity, revision link, accrued history,
    /// artifacts, revision counter) from the stored record onto an incoming edit. The single
    /// source of truth for fields the edit form never sends; call before validating an update
    /// so validation sees the stored values, not the PUT body's model defaults.
    /// </summary>
    void RestoreServerOwnedFields(VmTemplate incoming, VmTemplate existing);

    Task SaveTemplateDirectAsync(VmTemplate template);
    Task<bool> DeleteTemplateAsync(string templateId, string requesterId);
    Task<VmTemplate> PublishTemplateAsync(string templateId, string requesterId);

    /// <summary>Author: open a draft revision of a Published community template (idempotent).</summary>
    Task<VmTemplate> ReviseTemplateAsync(string templateId, string requesterId);

    /// <summary>Author: withdraw a PendingReview submission → Draft so it can be edited again.</summary>
    Task<VmTemplate> CancelReviewAsync(string templateId, string requesterId);

    /// <summary>Admin: approve a PendingReview community template → Published.</summary>
    Task<VmTemplate> ApproveTemplateAsync(string templateId, string reviewerId);

    /// <summary>Admin: reject a PendingReview community template → Rejected, with a reason.</summary>
    Task<VmTemplate> RejectTemplateAsync(string templateId, string reviewerId, string reason);

    /// <summary>Admin: list community templates awaiting review (oldest first).</summary>
    Task<List<VmTemplate>> GetPendingReviewTemplatesAsync();

    /// <summary>
    /// Compliance: archive every Published community template authored by a wallet, as
    /// part of a takedown. Templates are the amplification surface — one public template
    /// deploys many times — so withholding service from an author must also pull their
    /// live listings, not just stop their VMs. Returns the number archived.
    ///
    /// Scope: Published + IsCommunity only. Platform templates are admin-authored (not
    /// this wallet's), and Draft/PendingReview/Rejected are already unreachable to
    /// others. NOT reversed on unsuspend — an archived template returns only by the
    /// author republishing, which re-enters admin review.
    /// </summary>
    Task<int> ArchiveAuthorTemplatesAsync(string authorId, CancellationToken ct = default);

    Task<TemplateValidationResult> ValidateTemplateAsync(VmTemplate template);
    // ════════════════════════════════════════════════════════════════════════
    // Deployment Helpers
    // ════════════════════════════════════════════════════════════════════════

    Task<CreateVmRequest> BuildVmRequestFromTemplateAsync(
        string templateId,
        string vmName,
        VmSpec? customSpec = null,
        Dictionary<string, string>? environmentVariables = null,
        string? nodeArchitecture = null);

    string SubstituteCloudInitVariables(
        string cloudInitTemplate,
        Dictionary<string, string> variables);

    Dictionary<string, string> GetAvailableVariables(VirtualMachine vm, Node? node = null);

    // ════════════════════════════════════════════════════════════════════════
    // Statistics & Ratings
    // ════════════════════════════════════════════════════════════════════════

    Task IncrementDeploymentCountAsync(string templateId);
    Task UpdateTemplateStatsAsync(string templateId);
    Task UpdateCategoryCountsAsync();

    /// <summary>
    /// Recalculate and persist denormalized rating aggregates on a template
    /// </summary>
    Task UpdateTemplateRatingsAsync(string templateId);
}

/// <summary>
/// Query parameters for template search
/// </summary>
public class TemplateQuery
{
    public string? Category { get; set; }
    public bool? RequiresGpu { get; set; }
    public List<string>? Tags { get; set; }
    public string? SearchTerm { get; set; }
    public bool FeaturedOnly { get; set; }
    public string SortBy { get; set; } = "popular"; // popular, newest, name, rating
    public int? Limit { get; set; }
}

/// <summary>
/// Template validation result
/// </summary>
public class TemplateValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    public static TemplateValidationResult Success() => new() { IsValid = true };
    public static TemplateValidationResult Failure(params string[] errors) =>
        new() { IsValid = false, Errors = errors.ToList() };
}
