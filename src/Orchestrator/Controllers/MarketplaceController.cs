using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Services;
using System.Security.Claims;

namespace Orchestrator.Controllers;

/// <summary>
/// API endpoints for the VM Template Marketplace
/// </summary>
[ApiController]
[Route("api/marketplace")]
public class MarketplaceController : ControllerBase
{
    private readonly ITemplateService _templateService;
    private readonly IVmService _vmService;
    private readonly ILogger<MarketplaceController> _logger;

    public MarketplaceController(
        ITemplateService templateService,
        IVmService vmService,
        ILogger<MarketplaceController> logger)
    {
        _templateService = templateService;
        _vmService = vmService;
        _logger = logger;
    }


    // ════════════════════════════════════════════════════════════════════════
    // Template Discovery & Browsing
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get all templates with optional filtering
    /// </summary>
    /// <param name="category">Filter by category (e.g., "ai-ml", "web-apps")</param>
    /// <param name="requiresGpu">Filter by GPU requirement</param>
    /// <param name="tags">Filter by tags (comma-separated)</param>
    /// <param name="search">Search term for name/description</param>
    /// <param name="featured">Show only featured templates</param>
    /// <param name="sortBy">Sort order: popular, newest, name</param>
    /// <param name="limit">Maximum number of results</param>
    [HttpGet("templates")]
    [AllowAnonymous]
    public async Task<ActionResult<List<VmTemplate>>> GetTemplates(
        [FromQuery] string? category = null,
        [FromQuery] bool? requiresGpu = null,
        [FromQuery] string? tags = null,
        [FromQuery] string? search = null,
        [FromQuery] bool featured = false,
        [FromQuery] string sortBy = "popular",
        [FromQuery] int? limit = null)
    {
        try
        {
            var query = new TemplateQuery
            {
                Category = category,
                RequiresGpu = requiresGpu,
                Tags = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                SearchTerm = search,
                FeaturedOnly = featured,
                SortBy = sortBy,
                Limit = limit
            };

            var templates = await _templateService.GetTemplatesAsync(query);

            _logger.LogInformation(
                "Retrieved {Count} templates (category: {Category}, gpu: {Gpu}, search: {Search})",
                templates.Count, category ?? "all", requiresGpu?.ToString() ?? "any", search ?? "none");

            return Ok(templates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get templates");
            return StatusCode(500, new { error = "Failed to retrieve templates" });
        }
    }

    /// <summary>
    /// Get featured templates for homepage
    /// </summary>
    [HttpGet("templates/featured")]
    [AllowAnonymous]
    public async Task<ActionResult<List<VmTemplate>>> GetFeaturedTemplates(
        [FromQuery] int limit = 10)
    {
        try
        {
            var templates = await _templateService.GetFeaturedTemplatesAsync(limit);

            _logger.LogInformation("Retrieved {Count} featured templates", templates.Count);

            return Ok(templates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get featured templates");
            return StatusCode(500, new { error = "Failed to retrieve featured templates" });
        }
    }

    /// <summary>
    /// Get template details by ID or slug
    /// </summary>
    [HttpGet("templates/{slugOrId}")]
    [AllowAnonymous]
    public async Task<ActionResult<VmTemplate>> GetTemplate(string slugOrId)
    {
        try
        {
            // Try slug first (lowercase with hyphens), then ID
            var template = await _templateService.GetTemplateBySlugAsync(slugOrId);
            
            if (template == null)
            {
                template = await _templateService.GetTemplateByIdAsync(slugOrId);
            }

            if (template == null)
            {
                _logger.LogWarning("Template not found: {SlugOrId}", slugOrId);
                return NotFound(new { error = $"Template '{slugOrId}' not found" });
            }

            _logger.LogInformation("Retrieved template: {Name} ({Id})", template.Name, template.Id);

            return Ok(template);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get template: {SlugOrId}", slugOrId);
            return StatusCode(500, new { error = "Failed to retrieve template details" });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Categories
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get all template categories
    /// </summary>
    [HttpGet("categories")]
    [AllowAnonymous]
    public async Task<ActionResult<List<TemplateCategory>>> GetCategories()
    {
        try
        {
            var categories = await _templateService.GetCategoriesAsync();

            _logger.LogInformation("Retrieved {Count} categories", categories.Count);

            return Ok(categories);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get categories");
            return StatusCode(500, new { error = "Failed to retrieve categories" });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Template Deployment
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deploy a VM from a template
    /// </summary>
    [HttpPost("templates/{templateId}/deploy")]
    [Authorize]
    public async Task<ActionResult<CreateVmResponse>> DeployTemplate(
        string templateId,
        [FromBody] DeployTemplateRequest request)
    {
        try
        {
            // Get authenticated user
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value 
                      ?? User.FindFirst("sub")?.Value;

            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("Unauthorized template deployment attempt");
                return Unauthorized(new { error = "User authentication required" });
            }

            // Validate template exists
            var template = await _templateService.GetTemplateByIdAsync(templateId);
            if (template == null)
            {
                _logger.LogWarning("Template not found for deployment: {TemplateId}", templateId);
                return NotFound(new { error = $"Template '{templateId}' not found" });
            }

            // Validate template is published
            if (template.Status != TemplateStatus.Published)
            {
                _logger.LogWarning(
                    "Attempted to deploy non-published template: {TemplateId} (status: {Status})",
                    templateId, template.Status);
                return BadRequest(new { error = "Template is not available for deployment" });
            }

            _logger.LogInformation(
                "Deploying template {TemplateName} for user {UserId} with VM name {VmName}",
                template.Name, userId, request.VmName);

            // Build VM request from template
            var vmRequest = await _templateService.BuildVmRequestFromTemplateAsync(
                templateId,
                request.VmName,
                request.CustomSpec,
                request.EnvironmentVariables);

            // Override node selection if specified
            if (!string.IsNullOrEmpty(request.NodeId))
            {
                vmRequest = vmRequest with { NodeId = request.NodeId };
            }

            // Deploy VM
            var vmResponse = await _vmService.CreateVmAsync(userId, vmRequest, request.NodeId);

            // Increment deployment counter (fire and forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _templateService.IncrementDeploymentCountAsync(templateId);
                    await _templateService.UpdateTemplateStatsAsync(templateId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update template stats for {TemplateId}", templateId);
                }
            });

            _logger.LogInformation(
                "Successfully deployed template {TemplateName} as VM {VmId}",
                template.Name, vmResponse.VmId);

            return Ok(vmResponse);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid template deployment request");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy template: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to deploy template", details = ex.Message });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Admin/Creator Endpoints (Phase 2)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a new template (admin only - Phase 1: curated templates only)
    /// </summary>
    [HttpPost("templates")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<VmTemplate>> CreateTemplate([FromBody] VmTemplate template)
    {
        try
        {
            // Validate template
            var validation = await _templateService.ValidateTemplateAsync(template);
            if (!validation.IsValid)
            {
                _logger.LogWarning(
                    "Template validation failed: {Errors}",
                    string.Join(", ", validation.Errors));
                return BadRequest(new
                {
                    error = "Template validation failed",
                    errors = validation.Errors,
                    warnings = validation.Warnings
                });
            }

            // Create template
            var created = await _templateService.CreateTemplateAsync(template);

            _logger.LogInformation(
                "Created template: {TemplateName} ({TemplateId})",
                created.Name, created.Id);

            return CreatedAtAction(
                nameof(GetTemplate),
                new { slugOrId = created.Slug },
                created);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid template creation request");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create template");
            return StatusCode(500, new { error = "Failed to create template" });
        }
    }

    /// <summary>
    /// Update an existing template (admin only - Phase 1)
    /// </summary>
    [HttpPut("templates/{templateId}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<VmTemplate>> UpdateTemplate(
        string templateId,
        [FromBody] VmTemplate template)
    {
        try
        {
            // Ensure ID matches
            if (template.Id != templateId)
            {
                template.Id = templateId;
            }

            // Validate template
            var validation = await _templateService.ValidateTemplateAsync(template);
            if (!validation.IsValid)
            {
                _logger.LogWarning(
                    "Template validation failed: {Errors}",
                    string.Join(", ", validation.Errors));
                return BadRequest(new
                {
                    error = "Template validation failed",
                    errors = validation.Errors,
                    warnings = validation.Warnings
                });
            }

            // Update template
            var updated = await _templateService.UpdateTemplateAsync(template);

            _logger.LogInformation(
                "Updated template: {TemplateName} ({TemplateId})",
                updated.Name, updated.Id);

            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid template update request");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update template: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to update template" });
        }
    }
}

// ════════════════════════════════════════════════════════════════════════
// Request/Response Models
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request to deploy a VM from a template
/// </summary>
public class DeployTemplateRequest
{
    /// <summary>
    /// Name for the new VM
    /// </summary>
    public string VmName { get; set; } = string.Empty;

    /// <summary>
    /// Optional: Custom VM specification (overrides template defaults)
    /// If not provided, uses template's recommended spec
    /// </summary>
    public VmSpec? CustomSpec { get; set; }

    /// <summary>
    /// Optional: Environment variables to inject into cloud-init
    /// Merged with template defaults (request values override defaults)
    /// </summary>
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Optional: Target specific node for deployment
    /// If not provided, scheduler will select best available node
    /// </summary>
    public string? NodeId { get; set; }
}
