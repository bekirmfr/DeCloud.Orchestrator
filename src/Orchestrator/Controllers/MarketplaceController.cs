using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Services;
using Orchestrator.Services.Balance;
using System.Security.Claims;

namespace Orchestrator.Controllers;

/// <summary>
/// API endpoints for the Marketplace (Templates, Reviews, and Node browsing).
/// Templates can be platform-curated or user-created (community).
/// </summary>
[ApiController]
[Route("api/marketplace")]
public class MarketplaceController : ControllerBase
{
    private readonly ITemplateService _templateService;
    private readonly IVmService _vmService;
    private readonly IReviewService _reviewService;
    private readonly IBalanceService _balanceService;
    private readonly TemplateSeederService _seederService;
    private readonly INodeService _nodeService;
    private readonly ILogger<MarketplaceController> _logger;

    public MarketplaceController(
        ITemplateService templateService,
        IVmService vmService,
        IReviewService reviewService,
        IBalanceService balanceService,
        TemplateSeederService seederService,
        INodeService nodeService,
        ILogger<MarketplaceController> logger)
    {
        _templateService = templateService;
        _vmService = vmService;
        _reviewService = reviewService;
        _balanceService = balanceService;
        _seederService = seederService;
        _nodeService = nodeService;
        _logger = logger;
    }


    // ════════════════════════════════════════════════════════════════════════
    // Template Discovery & Browsing (Public)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Browse published public templates with optional filtering
    /// </summary>
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
            var template = await _templateService.GetTemplateBySlugAsync(slugOrId)
                        ?? await _templateService.GetTemplateByIdAsync(slugOrId);

            if (template == null)
                return NotFound(new { error = $"Template '{slugOrId}' not found" });

            // Private templates: only author can view
            if (template.Visibility == TemplateVisibility.Private)
            {
                var userId = GetUserId();
                if (userId == null || template.AuthorId != userId)
                    return NotFound(new { error = $"Template '{slugOrId}' not found" });
            }

            return Ok(template);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get template: {SlugOrId}", slugOrId);
            return StatusCode(500, new { error = "Failed to retrieve template details" });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // My Templates (Authenticated user's own templates)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get all templates owned by the authenticated user (all statuses)
    /// </summary>
    [HttpGet("templates/my")]
    [Authorize]
    public async Task<ActionResult<List<VmTemplate>>> GetMyTemplates()
    {
        try
        {
            var userId = GetUserId();
            if (userId == null)
                return Unauthorized(new { error = "Authentication required" });

            var templates = await _templateService.GetTemplatesByAuthorAsync(userId);
            return Ok(templates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user templates");
            return StatusCode(500, new { error = "Failed to retrieve your templates" });
        }
    }

    /// <summary>
    /// Create a new community template (any authenticated user)
    /// </summary>
    [HttpPost("templates/create")]
    [Authorize]
    public async Task<ActionResult<VmTemplate>> CreateCommunityTemplate(
        [FromBody] CreateTemplateRequest request)
    {
        try
        {
            var userId = GetUserId();
            if (userId == null)
                return Unauthorized(new { error = "Authentication required" });

            var template = new VmTemplate
            {
                Name = request.Name,
                Slug = request.Slug,
                Version = request.Version,
                Description = request.Description,
                LongDescription = request.LongDescription,
                Category = request.Category,
                Tags = request.Tags ?? new List<string>(),
                IconUrl = request.IconUrl,
                AuthorId = userId,
                AuthorName = request.AuthorName ?? TruncateWallet(userId),
                AuthorRevenueWallet = request.AuthorRevenueWallet ?? userId,
                License = request.License,
                SourceUrl = request.SourceUrl,
                MinimumSpec = request.MinimumSpec ?? new VmSpec(),
                RecommendedSpec = request.RecommendedSpec ?? new VmSpec(),
                RequiresGpu = request.RequiresGpu,
                DefaultGpuMode = request.RequiresGpu
                    ? (request.DefaultGpuMode != GpuMode.None ? request.DefaultGpuMode : GpuMode.Passthrough)
                    : GpuMode.None,
                GpuRequirement = request.GpuRequirement,
                ContainerImage = request.ContainerImage,
                CloudInitTemplate = request.CloudInitTemplate,
                DefaultEnvironmentVariables = request.DefaultEnvironmentVariables ?? new(),
                ExposedPorts = request.ExposedPorts ?? new(),
                DefaultAccessUrl = request.DefaultAccessUrl,
                DefaultUsername = request.DefaultUsername,
                UseGeneratedPassword = request.UseGeneratedPassword,
                Visibility = request.Visibility,
                PricingModel = request.PricingModel,
                TemplatePrice = request.TemplatePrice,
                DefaultBandwidthTier = request.DefaultBandwidthTier,
                EstimatedCostPerHour = request.EstimatedCostPerHour,

                // Community template defaults
                Status = TemplateStatus.Draft,
                IsCommunity = true,
                IsVerified = false,
                IsFeatured = false,
            };

            var validation = await _templateService.ValidateTemplateAsync(template);
            if (!validation.IsValid)
            {
                return BadRequest(new
                {
                    error = "Template validation failed",
                    errors = validation.Errors,
                    warnings = validation.Warnings
                });
            }

            var created = await _templateService.CreateTemplateAsync(template);

            return CreatedAtAction(
                nameof(GetTemplate),
                new { slugOrId = created.Slug },
                created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create community template");
            return StatusCode(500, new { error = "Failed to create template" });
        }
    }

    /// <summary>
    /// Update an existing template (owner only)
    /// </summary>
    [HttpPut("templates/{templateId}")]
    [Authorize]
    public async Task<ActionResult<VmTemplate>> UpdateTemplate(
        string templateId,
        [FromBody] VmTemplate template)
    {
        try
        {
            var userId = GetUserId();
            if (userId == null)
                return Unauthorized(new { error = "Authentication required" });

            var existing = await _templateService.GetTemplateByIdAsync(templateId);
            if (existing == null)
                return NotFound(new { error = $"Template '{templateId}' not found" });

            // Ownership check
            if (existing.AuthorId != userId)
                return Forbid();

            template.Id = templateId;

            var validation = await _templateService.ValidateTemplateAsync(template);
            if (!validation.IsValid)
            {
                return BadRequest(new
                {
                    error = "Template validation failed",
                    errors = validation.Errors,
                    warnings = validation.Warnings
                });
            }

            var updated = await _templateService.UpdateTemplateAsync(template);
            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update template: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to update template" });
        }
    }

    /// <summary>
    /// Delete a template (owner only)
    /// </summary>
    [HttpDelete("templates/{templateId}")]
    [Authorize]
    public async Task<ActionResult> DeleteTemplate(string templateId)
    {
        try
        {
            var userId = GetUserId();
            if (userId == null)
                return Unauthorized(new { error = "Authentication required" });

            var deleted = await _templateService.DeleteTemplateAsync(templateId, userId);
            if (!deleted)
                return NotFound(new { error = $"Template '{templateId}' not found" });

            return Ok(new { message = "Template deleted" });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete template: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to delete template" });
        }
    }

    /// <summary>
    /// Publish a draft template (Draft -> Published, owner only)
    /// </summary>
    [HttpPatch("templates/{templateId}/publish")]
    [Authorize]
    public async Task<ActionResult<VmTemplate>> PublishTemplate(string templateId)
    {
        try
        {
            var userId = GetUserId();
            if (userId == null)
                return Unauthorized(new { error = "Authentication required" });

            var published = await _templateService.PublishTemplateAsync(templateId, userId);
            return Ok(published);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish template: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to publish template" });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Template Deployment
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deploy a VM from a template.
    /// For paid templates, the user's escrow balance must cover the template fee.
    /// Template fee is settled on-chain via reportUsage() when the VM boots successfully.
    /// </summary>
    [HttpPost("templates/{templateId}/deploy")]
    [Authorize]
    public async Task<ActionResult<CreateVmResponse>> DeployTemplate(
        string templateId,
        [FromBody] DeployTemplateRequest request)
    {
        try
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
                return Unauthorized(new { error = "User authentication required" });

            var template = await _templateService.GetTemplateByIdAsync(templateId);
            if (template == null)
                return NotFound(new { error = $"Template '{templateId}' not found" });

            // Visibility check: private templates only deployable by author
            if (template.Visibility == TemplateVisibility.Private && template.AuthorId != userId)
                return NotFound(new { error = $"Template '{templateId}' not found" });

            // Draft templates only deployable by author (for testing)
            if (template.Status == TemplateStatus.Draft && template.AuthorId != userId)
                return BadRequest(new { error = "Template is not available for deployment" });

            if (template.Status == TemplateStatus.Archived)
                return BadRequest(new { error = "Template has been archived" });

            // Check balance for paid templates
            if (template.PricingModel == TemplatePricingModel.PerDeploy && template.TemplatePrice > 0)
            {
                var hasBalance = await _balanceService.HasSufficientBalanceAsync(userId, template.TemplatePrice);
                if (!hasBalance)
                {
                    return BadRequest(new
                    {
                        error = "Insufficient balance for template fee",
                        templatePrice = template.TemplatePrice,
                        message = $"This template costs {template.TemplatePrice} USDC per deployment. Please deposit more funds."
                    });
                }
            }

            // Build VM request from template
            var vmRequest = await _templateService.BuildVmRequestFromTemplateAsync(
                templateId,
                request.VmName,
                request.CustomSpec,
                request.EnvironmentVariables);

            if (!string.IsNullOrEmpty(request.NodeId))
            {
                vmRequest = vmRequest with { NodeId = request.NodeId };
            }

            // Deploy VM
            var vmResponse = await _vmService.CreateVmAsync(userId, vmRequest, request.NodeId);

            // Track deployment stats (fire and forget)
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
                "Deployed template {TemplateName} as VM {VmId} for user {UserId} (price: {Price} USDC)",
                template.Name, vmResponse.VmId, userId,
                template.PricingModel == TemplatePricingModel.PerDeploy ? template.TemplatePrice : 0);

            return Ok(vmResponse);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy template: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to deploy template", details = ex.Message });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Reviews
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get reviews for a resource (template, node, etc.)
    /// </summary>
    [HttpGet("reviews/{resourceType}/{resourceId}")]
    [AllowAnonymous]
    public async Task<ActionResult<List<MarketplaceReview>>> GetReviews(
        string resourceType,
        string resourceId,
        [FromQuery] int limit = 50,
        [FromQuery] int skip = 0)
    {
        try
        {
            var reviews = await _reviewService.GetReviewsAsync(resourceType, resourceId, limit, skip);
            return Ok(reviews);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get reviews for {ResourceType}/{ResourceId}",
                resourceType, resourceId);
            return StatusCode(500, new { error = "Failed to retrieve reviews" });
        }
    }

    /// <summary>
    /// Submit a review for a resource (requires proof of usage)
    /// </summary>
    [HttpPost("reviews")]
    [Authorize]
    public async Task<ActionResult<MarketplaceReview>> SubmitReview(
        [FromBody] SubmitReviewRequest request)
    {
        try
        {
            var userId = GetUserId();
            if (userId == null)
                return Unauthorized(new { error = "Authentication required" });

            var review = new MarketplaceReview
            {
                ResourceType = request.ResourceType,
                ResourceId = request.ResourceId,
                ReviewerId = userId,
                ReviewerName = request.ReviewerName,
                Rating = request.Rating,
                Title = request.Title,
                Comment = request.Comment,
                EligibilityProof = new ReviewEligibilityProof
                {
                    Type = request.ProofType,
                    ReferenceId = request.ProofReferenceId
                }
            };

            var saved = await _reviewService.SubmitReviewAsync(review);
            return Ok(saved);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(403, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit review");
            return StatusCode(500, new { error = "Failed to submit review" });
        }
    }

    /// <summary>
    /// Get the current user's review for a specific resource
    /// </summary>
    [HttpGet("reviews/{resourceType}/{resourceId}/my")]
    [Authorize]
    public async Task<ActionResult<MarketplaceReview?>> GetMyReview(
        string resourceType, string resourceId)
    {
        try
        {
            var userId = GetUserId();
            if (userId == null)
                return Unauthorized(new { error = "Authentication required" });

            var review = await _reviewService.GetUserReviewAsync(resourceType, resourceId, userId);
            if (review == null)
                return Ok((MarketplaceReview?)null);

            return Ok(review);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user review");
            return StatusCode(500, new { error = "Failed to retrieve review" });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Node Discovery & Browsing
    // ════════════════════════════════════════════════════════════════════════

    [HttpGet("nodes")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<NodeAdvertisement>>>> SearchNodes(
        [FromQuery] string? tags = null,
        [FromQuery] string? region = null,
        [FromQuery] bool? requiresGpu = null,
        [FromQuery] bool onlineOnly = false,
        [FromQuery] string sortBy = "uptime",
        [FromQuery] bool sortDescending = true,
        [FromQuery] double? minUptime = null,
        [FromQuery] decimal? maxPrice = null)
    {
        try
        {
            var criteria = new NodeSearchCriteria
            {
                Tags = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                Region = region,
                RequiresGpu = requiresGpu,
                OnlineOnly = onlineOnly,
                SortBy = sortBy,
                SortDescending = sortDescending,
                MinUptimePercent = minUptime,
                MaxPricePerPoint = maxPrice
            };

            var nodes = await _nodeService.SearchNodesAsync(criteria);
            return Ok(ApiResponse<List<NodeAdvertisement>>.Ok(nodes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search nodes");
            return StatusCode(500, new { error = "Failed to search nodes" });
        }
    }

    [HttpGet("nodes/featured")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<List<NodeAdvertisement>>>> GetFeaturedNodes()
    {
        try
        {
            var nodes = await _nodeService.GetFeaturedNodesAsync();
            return Ok(ApiResponse<List<NodeAdvertisement>>.Ok(nodes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get featured nodes");
            return StatusCode(500, new { error = "Failed to retrieve featured nodes" });
        }
    }

    [HttpGet("nodes/{nodeId}")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<NodeAdvertisement>>> GetNodeDetails(string nodeId)
    {
        try
        {
            var node = await _nodeService.GetNodeAdvertisementAsync(nodeId);
            if (node == null)
                return NotFound(new { error = $"Node '{nodeId}' not found" });

            return Ok(ApiResponse<NodeAdvertisement>.Ok(node));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get node details: {NodeId}", nodeId);
            return StatusCode(500, new { error = "Failed to retrieve node details" });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Categories
    // ════════════════════════════════════════════════════════════════════════

    [HttpGet("categories")]
    [AllowAnonymous]
    public async Task<ActionResult<List<TemplateCategory>>> GetCategories()
    {
        try
        {
            var categories = await _templateService.GetCategoriesAsync();
            return Ok(categories);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get categories");
            return StatusCode(500, new { error = "Failed to retrieve categories" });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Admin Endpoints
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a platform-curated template (admin only)
    /// </summary>
    [HttpPost("templates")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<VmTemplate>> CreatePlatformTemplate([FromBody] VmTemplate template)
    {
        try
        {
            var validation = await _templateService.ValidateTemplateAsync(template);
            if (!validation.IsValid)
            {
                return BadRequest(new
                {
                    error = "Template validation failed",
                    errors = validation.Errors,
                    warnings = validation.Warnings
                });
            }

            var created = await _templateService.CreateTemplateAsync(template);

            return CreatedAtAction(
                nameof(GetTemplate),
                new { slugOrId = created.Slug },
                created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create platform template");
            return StatusCode(500, new { error = "Failed to create template" });
        }
    }

    /// <summary>
    /// Seed initial templates and categories (admin only)
    /// </summary>
    [HttpPost("seed")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SeedTemplates([FromQuery] bool force = false)
    {
        try
        {
            await _seederService.SeedAsync(force);

            var templates = await _templateService.GetTemplatesAsync(new TemplateQuery());
            var categories = await _templateService.GetCategoriesAsync();

            return Ok(new
            {
                message = "Templates seeded successfully",
                templateCount = templates.Count,
                categoryCount = categories.Count,
                templates = templates.Select(t => new { t.Id, t.Name, t.Slug, t.Category }).ToList(),
                categories = categories.Select(c => new { c.Id, c.Name, c.Slug }).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed templates");
            return StatusCode(500, new { error = "Failed to seed templates", details = ex.Message });
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════════

    private string? GetUserId()
    {
        return User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
    }

    private static string TruncateWallet(string wallet)
    {
        if (wallet.Length <= 10) return wallet;
        return $"{wallet[..6]}...{wallet[^4..]}";
    }
}

// ════════════════════════════════════════════════════════════════════════
// Request Models
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request to deploy a VM from a template
/// </summary>
public class DeployTemplateRequest
{
    public string VmName { get; set; } = string.Empty;
    public VmSpec? CustomSpec { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    public string? NodeId { get; set; }
}

/// <summary>
/// Request to create a community template
/// </summary>
public class CreateTemplateRequest
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? LongDescription { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public List<string>? Tags { get; set; }
    public string? IconUrl { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorRevenueWallet { get; set; }
    public string? License { get; set; }
    public string? SourceUrl { get; set; }
    public VmSpec? MinimumSpec { get; set; }
    public VmSpec? RecommendedSpec { get; set; }
    public bool RequiresGpu { get; set; }
    /// <summary>
    /// Default GPU mode when RequiresGpu is true.
    /// 0 = None, 1 = Passthrough (dedicated GPU, IOMMU required), 2 = Proxied (shared GPU).
    /// If RequiresGpu is true and this is not set, defaults to Passthrough.
    /// </summary>
    public GpuMode DefaultGpuMode { get; set; } = GpuMode.None;
    public string? GpuRequirement { get; set; }
    /// <summary>
    /// Docker container image for container-based GPU deployment (e.g., "ollama/ollama:latest").
    /// </summary>
    public string? ContainerImage { get; set; }
    public string CloudInitTemplate { get; set; } = string.Empty;
    public Dictionary<string, string>? DefaultEnvironmentVariables { get; set; }
    public List<TemplatePort>? ExposedPorts { get; set; }
    public string? DefaultAccessUrl { get; set; }
    public string? DefaultUsername { get; set; }
    public bool UseGeneratedPassword { get; set; } = true;
    public TemplateVisibility Visibility { get; set; } = TemplateVisibility.Public;
    public TemplatePricingModel PricingModel { get; set; } = TemplatePricingModel.Free;
    public decimal TemplatePrice { get; set; }
    public BandwidthTier DefaultBandwidthTier { get; set; } = BandwidthTier.Unmetered;
    public decimal EstimatedCostPerHour { get; set; }
}

/// <summary>
/// Request to submit a review
/// </summary>
public class SubmitReviewRequest
{
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string? ReviewerName { get; set; }
    public int Rating { get; set; }
    public string? Title { get; set; }
    public string? Comment { get; set; }
    /// <summary>
    /// Proof type: "deployment" for templates, "vm_usage" for nodes
    /// </summary>
    public string ProofType { get; set; } = string.Empty;
    /// <summary>
    /// Reference ID for the proof (VM ID)
    /// </summary>
    public string ProofReferenceId { get; set; } = string.Empty;
}
