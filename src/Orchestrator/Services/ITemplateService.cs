using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Service for managing VM templates in the marketplace
/// </summary>
public interface ITemplateService
{
    // ════════════════════════════════════════════════════════════════════════
    // Template Queries
    // ════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Get template by ID
    /// </summary>
    Task<VmTemplate?> GetTemplateByIdAsync(string templateId);
    
    /// <summary>
    /// Get template by slug (URL-friendly identifier)
    /// </summary>
    Task<VmTemplate?> GetTemplateBySlugAsync(string slug);
    
    /// <summary>
    /// Get templates with optional filtering
    /// </summary>
    Task<List<VmTemplate>> GetTemplatesAsync(TemplateQuery query);
    
    /// <summary>
    /// Get featured templates for marketplace homepage
    /// </summary>
    Task<List<VmTemplate>> GetFeaturedTemplatesAsync(int limit = 10);
    
    /// <summary>
    /// Get all template categories
    /// </summary>
    Task<List<TemplateCategory>> GetCategoriesAsync();
    
    
    // ════════════════════════════════════════════════════════════════════════
    // Template Management (Admin/Creator)
    // ════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Create a new template
    /// </summary>
    Task<VmTemplate> CreateTemplateAsync(VmTemplate template);
    
    /// <summary>
    /// Update an existing template
    /// </summary>
    Task<VmTemplate> UpdateTemplateAsync(VmTemplate template);
    
    /// <summary>
    /// Validate template before publishing
    /// </summary>
    Task<TemplateValidationResult> ValidateTemplateAsync(VmTemplate template);
    
    
    // ════════════════════════════════════════════════════════════════════════
    // Deployment Helpers
    // ════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Build VM creation request from template with optional customization
    /// </summary>
    Task<CreateVmRequest> BuildVmRequestFromTemplateAsync(
        string templateId,
        string vmName,
        VmSpec? customSpec = null,
        Dictionary<string, string>? environmentVariables = null);
    
    /// <summary>
    /// Process cloud-init template and substitute variables
    /// </summary>
    string SubstituteCloudInitVariables(
        string cloudInitTemplate,
        Dictionary<string, string> variables);
    
    /// <summary>
    /// Get all available cloud-init variables
    /// </summary>
    Dictionary<string, string> GetAvailableVariables(VirtualMachine vm, Node? node = null);
    
    
    // ════════════════════════════════════════════════════════════════════════
    // Statistics & Tracking
    // ════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Increment deployment count for a template
    /// </summary>
    Task IncrementDeploymentCountAsync(string templateId);
    
    /// <summary>
    /// Update template deployment statistics
    /// </summary>
    Task UpdateTemplateStatsAsync(string templateId);
    
    /// <summary>
    /// Update category template counts
    /// </summary>
    Task UpdateCategoryCountsAsync();
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
    public string SortBy { get; set; } = "popular"; // popular, newest, name
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
