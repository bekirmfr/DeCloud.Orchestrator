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
    Task<VmTemplate> UpdateTemplateAsync(VmTemplate template);
    Task<bool> DeleteTemplateAsync(string templateId, string requesterId);
    Task<VmTemplate> PublishTemplateAsync(string templateId, string requesterId);
    Task<TemplateValidationResult> ValidateTemplateAsync(VmTemplate template);

    // ════════════════════════════════════════════════════════════════════════
    // Deployment Helpers
    // ════════════════════════════════════════════════════════════════════════

    Task<CreateVmRequest> BuildVmRequestFromTemplateAsync(
        string templateId,
        string vmName,
        VmSpec? customSpec = null,
        Dictionary<string, string>? environmentVariables = null);

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
