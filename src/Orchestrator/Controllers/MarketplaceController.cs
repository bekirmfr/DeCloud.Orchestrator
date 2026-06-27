using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Interfaces;
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
    private readonly IEnumerable<IVariableResolver> _variableResolvers;
    private readonly ILogger<MarketplaceController> _logger;

    public MarketplaceController(
        ITemplateService templateService,
        IVmService vmService,
        IReviewService reviewService,
        IBalanceService balanceService,
        TemplateSeederService seederService,
        INodeService nodeService,
        IEnumerable<IVariableResolver> variableResolvers,
        ILogger<MarketplaceController> logger)
    {
        _templateService = templateService;
        _vmService = vmService;
        _reviewService = reviewService;
        _balanceService = balanceService;
        _seederService = seederService;
        _nodeService = nodeService;
        _variableResolvers = variableResolvers;
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
    public async Task<IActionResult> CreateCommunityTemplate(
        [FromBody] CreateTemplateRequest request)
    {
        try
        {
            var userId = GetUserId();
            if (userId == null)
                return Unauthorized(new { error = "Authentication required" });

            var isAdmin = User.IsInRole("Admin");

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

                // Admin creates platform templates; regular users create community
                Status = TemplateStatus.Draft,
                IsCommunity = !isAdmin,
                IsVerified = false,
                IsFeatured = false,

            };

            // Variables: declared by the author, validated for unique names.
            // Empty list is the default for templates that don't use the
            // declared-variable pipeline.
            if (request.Variables is { Count: > 0 })
            {
                var seenNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var variable in request.Variables)
                {
                    if (string.IsNullOrWhiteSpace(variable.Name))
                        return BadRequest(new { error = "Variable name is required." });

                    if (!seenNames.Add(variable.Name))
                        return BadRequest(new
                        {
                            error = $"Duplicate variable name '{variable.Name}'. " +
                                    "Variable names must be unique within a template."
                        });
                }
                template.Variables = request.Variables;
            }

            // Artifacts: build, verify, and stage them in the same call as the
            // template create. Removes the previous draft→artifact→publish
            // round-trip. Each artifact is validated using the same path as
            // POST /artifacts so behaviour stays in lock step.
            if (request.Artifacts is { Count: > 0 })
            {
                foreach (var artifactRequest in request.Artifacts)
                {
                    try
                    {
                        var built = await BuildAndVerifyArtifactAsync(
                            artifactRequest, template.Artifacts, userId);
                        template.Artifacts.Add(built);
                    }
                    catch (ArgumentException ex)
                    {
                        return BadRequest(new { error = ex.Message });
                    }
                }
            }

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
                new { template = created, warnings = validation.Warnings });
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
    public async Task<IActionResult> UpdateTemplate(
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
            return Ok(new { template = updated, warnings = validation.Warnings });
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
            _logger.LogError(ex, "Failed to update template: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to update template" });
        }
    }

    /// <summary>
    /// Withdraw a PendingReview submission back to Draft (author only).
    /// </summary>
    [HttpPost("templates/{templateId}/cancel-review")]
    [Authorize]
    public async Task<ActionResult<VmTemplate>> CancelReview(string templateId)
    {
        try
        {
            var userId = GetUserId();
            if (userId == null)
                return Unauthorized(new { error = "Authentication required" });

            var result = await _templateService.CancelReviewAsync(templateId, userId);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Template '{templateId}' not found" });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel review: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to cancel review" });
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

            // Archived is closed to everyone.
            if (template.Status == TemplateStatus.Archived)
                return BadRequest(new { error = "Template has been archived" });

            // Anything below Published (Draft, PendingReview, Rejected) is deployable
            // only by its author, for testing — never by others until it is approved.
            if (template.Status != TemplateStatus.Published && template.AuthorId != userId)
                return BadRequest(new { error = "Template is not available for deployment" });

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

            // Surface creation-gate failures (ToS not accepted, quota, etc.) as a
            // proper 4xx with the specific error code — consistent with VmsController.
            // Must run before stats tracking so a blocked deploy isn't counted.
            if (string.IsNullOrEmpty(vmResponse.VmId))
            {
                return BadRequest(new { error = vmResponse.Error ?? "CREATE_ERROR", message = vmResponse.Message });
            }

            // Track deployment stats (fire and forget)s
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

    /// <summary>
    /// Register an artifact reference on a template.
    ///
    /// Supports two SourceUrl schemes:
    ///   HTTPS: orchestrator fetches URL and verifies SHA256 immediately.
    ///   data:  orchestrator decodes base64 and verifies SHA256 inline.
    ///          No network call. Bytes are stored in the template document.
    ///          Binary type rejected for data: URIs.
    ///          Size limits: 5 MB per artifact, 10 MB total per template.
    /// </summary>
    [HttpPost("templates/{id}/artifacts")]
    [Authorize]
    [ProducesResponseType(typeof(TemplateArtifact), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<TemplateArtifact>> AddArtifact(
        string id,
        [FromBody] AddArtifactRequest request)
    {
        var userId = User.FindFirst("wallet")?.Value ?? User.FindFirst("sub")?.Value;
        var template = await _templateService.GetTemplateByIdAsync(id);

        if (template is null) return NotFound();
        if (template.AuthorId != userId) return Forbid();

        TemplateArtifact artifact;
        try
        {
            artifact = await BuildAndVerifyArtifactAsync(request, template.Artifacts, userId);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        template.Artifacts.Add(artifact);
        await _templateService.SaveTemplateDirectAsync(template);

        return Ok(artifact);
    }

    /// <summary>
    /// Build a verified <see cref="TemplateArtifact"/> from an
    /// <see cref="AddArtifactRequest"/>. Handles three input shapes:
    ///   1. Raw <c>Content</c> + <c>ContentType</c> — server constructs the data: URI.
    ///   2. <c>SourceUrl</c> as a data: URI — server decodes and computes SHA256
    ///      when none supplied; verifies if one is supplied.
    ///   3. <c>SourceUrl</c> as HTTPS — server fetches and verifies against
    ///      the required <c>Sha256</c>.
    ///
    /// Enforces name uniqueness, the inline 5 MB per-artifact limit, and the
    /// 10 MB per-template aggregate inline limit (accounting for
    /// <paramref name="existingArtifacts"/>).
    ///
    /// Throws <see cref="ArgumentException"/> on validation failure. The caller
    /// translates this to a 400 response.
    /// </summary>
    private async Task<TemplateArtifact> BuildAndVerifyArtifactAsync(
        AddArtifactRequest request,
        IReadOnlyCollection<TemplateArtifact> existingArtifacts,
        string? userId)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Artifact name is required.");

        if (existingArtifacts.Any(a =>
            string.Equals(a.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"An artifact named '{request.Name}' already exists on this template. " +
                "Names must be unique within a template.");
        }

        // Resolve effective SourceUrl: either supplied, or constructed from Content.
        var sourceUrl = request.SourceUrl?.Trim();
        if (string.IsNullOrEmpty(sourceUrl))
        {
            if (string.IsNullOrEmpty(request.Content))
                throw new ArgumentException(
                    "Either SourceUrl or Content is required for an artifact.");

            var mediaType = string.IsNullOrWhiteSpace(request.ContentType)
                ? "text/plain"
                : request.ContentType.Trim();

            var contentBytes = System.Text.Encoding.UTF8.GetBytes(request.Content);
            sourceUrl = $"data:{mediaType};base64,{Convert.ToBase64String(contentBytes)}";
        }

        var artifact = new TemplateArtifact
        {
            Name = request.Name,
            Description = request.Description ?? string.Empty,
            Sha256 = request.Sha256?.Trim().ToLowerInvariant() ?? string.Empty,
            SizeBytes = request.SizeBytes ?? 0,
            SourceUrl = sourceUrl,
            Architecture = request.Architecture,
            Type = request.Type,
            RegisteredAt = DateTime.UtcNow,
            RegisteredBy = userId,
        };

        if (artifact.IsInline)
        {
            // ── Inline (data: URI) validation ─────────────────────────────
            if (artifact.Type == ArtifactType.Binary)
                throw new ArgumentException(
                    "Binary artifacts must use an HTTPS SourceUrl. " +
                    "Compile the binary, host it externally (e.g., GitHub Releases), " +
                    "and register with the download URL.");

            var commaIndex = sourceUrl.IndexOf(',');
            if (commaIndex < 0 ||
                !sourceUrl[..commaIndex]
                    .Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "data: URI must be in the form: data:{mediaType};base64,{base64bytes}");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(sourceUrl[(commaIndex + 1)..].Trim());
            }
            catch (FormatException)
            {
                throw new ArgumentException("data: URI contains invalid base64.");
            }

            if (bytes.Length > TemplateArtifact.MaxInlineArtifactBytes)
                throw new ArgumentException(
                    $"Inline attachment decoded size {bytes.Length / 1024 / 1024.0:F1} MB " +
                    $"exceeds the {TemplateArtifact.MaxInlineArtifactBytes / 1024 / 1024} MB limit. " +
                    "Host larger files externally.");

            var totalCurrentInline = existingArtifacts
                .Where(a => a.IsInline)
                .Sum(a => a.SizeBytes);

            if (totalCurrentInline + bytes.Length > TemplateArtifact.MaxTotalInlineBytes)
                throw new ArgumentException(
                    $"Adding this attachment would bring the template's total inline size " +
                    $"to {(totalCurrentInline + bytes.Length) / 1024 / 1024.0:F1} MB, " +
                    $"exceeding the {TemplateArtifact.MaxTotalInlineBytes / 1024 / 1024} MB limit.");

            var actualHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

            if (string.IsNullOrEmpty(artifact.Sha256))
            {
                // Inline path: declared SHA256 is optional — adopt the computed one.
                artifact.Sha256 = actualHash;
            }
            else if (actualHash != artifact.Sha256)
            {
                throw new ArgumentException(
                    $"SHA256 mismatch: declared {artifact.Sha256[..12]}, " +
                    $"actual {actualHash[..12]}. " +
                    "Recompute SHA256 of the decoded file and update the field, " +
                    "or omit the SHA256 field to let the server compute it.");
            }

            artifact.SizeBytes = bytes.Length;
        }
        else
        {
            // ── External (HTTPS) verification ─────────────────────────────
            if (string.IsNullOrEmpty(artifact.Sha256))
                throw new ArgumentException(
                    "SHA256 is required for external HTTPS artifacts so the fetched " +
                    "bytes can be verified.");

            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != "https")
                throw new ArgumentException(
                    "SourceUrl must be an HTTPS URL or a data: URI.");

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                using var response = await client.GetAsync(
                    sourceUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync();
                using var sha = System.Security.Cryptography.SHA256.Create();
                var hash = await sha.ComputeHashAsync(stream);
                var actual = Convert.ToHexString(hash).ToLowerInvariant();

                if (actual != artifact.Sha256)
                    throw new ArgumentException(
                        $"SHA256 mismatch: declared {artifact.Sha256[..12]}, " +
                        $"actual {actual[..12]}.");

                if (response.Content.Headers.ContentLength.HasValue)
                    artifact.SizeBytes = response.Content.Headers.ContentLength.Value;
            }
            catch (HttpRequestException ex)
            {
                throw new ArgumentException(
                    $"Could not fetch artifact from {sourceUrl}: {ex.Message}");
            }
        }

        return artifact;
    }

    /// <summary>Remove an artifact reference from a template.</summary>
    [HttpDelete("templates/{id}/artifacts/{artifactId}")]
    [Authorize]
    [ProducesResponseType(204)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RemoveArtifact(string id, string artifactId)
    {
        var userId = User.FindFirst("wallet")?.Value ?? User.FindFirst("sub")?.Value;
        var template = await _templateService.GetTemplateByIdAsync(id);

        if (template is null) return NotFound();
        if (template.AuthorId != userId) return Forbid();

        var removed = template.Artifacts.RemoveAll(a => a.Id == artifactId);
        if (removed == 0) return NotFound();

        var isAdmin = User.IsInRole("Admin");
        await _templateService.SaveTemplateDirectAsync(template);
        return NoContent();

    }

    /// <summary>
    /// Replace an existing artifact in place. Same request shape as
    /// <see cref="AddArtifact"/> — accepts inline data: URI, raw
    /// <c>Content</c> + <c>ContentType</c>, or external HTTPS — and runs the
    /// same SHA256 verification and size-limit checks. The artifact's
    /// <c>Id</c>, <c>RegisteredAt</c>, and <c>RegisteredBy</c> are preserved
    /// so cloud-init references that pin the artifact by ID stay stable.
    /// </summary>
    [HttpPut("templates/{id}/artifacts/{artifactId}")]
    [Authorize]
    [ProducesResponseType(typeof(TemplateArtifact), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<TemplateArtifact>> UpdateArtifact(
        string id,
        string artifactId,
        [FromBody] AddArtifactRequest request)
    {
        var userId = User.FindFirst("wallet")?.Value ?? User.FindFirst("sub")?.Value;
        var template = await _templateService.GetTemplateByIdAsync(id);

        if (template is null) return NotFound();
        if (template.AuthorId != userId) return Forbid();

        var existing = template.Artifacts.FirstOrDefault(a => a.Id == artifactId);
        if (existing is null)
            return NotFound(new { error = $"Artifact '{artifactId}' not found." });

        // Verify name uniqueness against siblings only — renaming an artifact
        // back to its own current name must not trip the duplicate check.
        var siblings = template.Artifacts.Where(a => a.Id != artifactId).ToList();

        TemplateArtifact built;
        try
        {
            built = await BuildAndVerifyArtifactAsync(request, siblings, userId);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }

        // Preserve the original identity / registration metadata.
        built.Id = existing.Id;
        built.RegisteredAt = existing.RegisteredAt;
        built.RegisteredBy = existing.RegisteredBy;

        template.Artifacts.RemoveAll(a => a.Id == artifactId);
        template.Artifacts.Add(built);

        await _templateService.SaveTemplateDirectAsync(template);
        return Ok(built);
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

    /// <summary>
    /// List variable names that the orchestrator's resolver registry resolves
    /// at render time. The marketplace deploy form uses this as its source of
    /// truth for "platform-bound vs user-supplied" — any declared Static
    /// variable whose <c>ResolverKey ?? Name</c> appears in <c>static</c> is
    /// resolved by the platform and never surfaced in the form's Template
    /// Configuration section (UNIFIED_CLOUDINIT_PIPELINE.md §2.4).
    ///
    /// <para>
    /// Response is split by <see cref="VariableKind"/> so future callers
    /// (validators, doc generators) can address Static and Dynamic resolvers
    /// independently. The same logical name may appear in both lists when
    /// distinct Static and Dynamic resolvers are registered for it.
    /// </para>
    ///
    /// <para>
    /// Public — resolver keys are not secrets (they appear in seeded template
    /// metadata and base/role YAMLs). Read-only — derived from DI singletons,
    /// no state mutation.
    /// </para>
    /// </summary>
    [HttpGet("platform-variables")]
    [AllowAnonymous]
    public ActionResult<PlatformVariablesResponse> GetPlatformVariables()
    {
        try
        {
            var statics = _variableResolvers
                .Where(r => r.Kind == VariableKind.Static)
                .Select(r => r.ResolverKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            var dynamics = _variableResolvers
                .Where(r => r.Kind == VariableKind.Dynamic)
                .Select(r => r.ResolverKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            return Ok(new PlatformVariablesResponse(statics, dynamics));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list platform variables");
            return StatusCode(500, new { error = "Failed to retrieve platform variables" });
        }
    }

    /// <summary>
    /// Response shape for <c>GET /api/marketplace/platform-variables</c>.
    /// </summary>
    public sealed record PlatformVariablesResponse(
        IReadOnlyList<string> Static,
        IReadOnlyList<string> Dynamic);


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
    /// List community templates awaiting review, oldest first (admin only).
    /// </summary>
    [HttpGet("templates/pending")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<VmTemplate>>> GetPendingTemplates()
    {
        try
        {
            var templates = await _templateService.GetPendingReviewTemplatesAsync();
            return Ok(templates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list pending templates");
            return StatusCode(500, new { error = "Failed to list pending templates" });
        }
    }

    /// <summary>
    /// Approve a community template (PendingReview -> Published, admin only).
    /// </summary>
    [HttpPost("templates/{templateId}/approve")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<VmTemplate>> ApproveTemplate(string templateId)
    {
        try
        {
            var reviewerId = GetUserId();
            if (reviewerId == null)
                return Unauthorized(new { error = "Authentication required" });

            var approved = await _templateService.ApproveTemplateAsync(templateId, reviewerId);
            return Ok(approved);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Template '{templateId}' not found" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to approve template: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to approve template" });
        }
    }

    /// <summary>
    /// Reject a community template (PendingReview -> Rejected, admin only). Reason required.
    /// </summary>
    [HttpPost("templates/{templateId}/reject")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<VmTemplate>> RejectTemplate(
        string templateId, [FromBody] RejectTemplateRequest request)
    {
        try
        {
            var reviewerId = GetUserId();
            if (reviewerId == null)
                return Unauthorized(new { error = "Authentication required" });

            if (string.IsNullOrWhiteSpace(request?.Reason))
                return BadRequest(new { error = "A rejection reason is required" });

            var rejected = await _templateService.RejectTemplateAsync(templateId, reviewerId, request.Reason);
            return Ok(rejected);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Template '{templateId}' not found" });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reject template: {TemplateId}", templateId);
            return StatusCode(500, new { error = "Failed to reject template" });
        }
    }

    public sealed record RejectTemplateRequest(string Reason);

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

    /// <summary>
    /// Artifacts to attach during creation. Each entry follows the same shape
    /// as the standalone <see cref="AddArtifactRequest"/> — inline (data: URI
    /// or raw <c>Content</c>) or external HTTPS. Validation, SHA256
    /// computation, and size-limit enforcement are identical to
    /// <c>POST /api/marketplace/templates/{id}/artifacts</c>.
    /// </summary>
    public List<AddArtifactRequest>? Artifacts { get; set; }

    /// <summary>
    /// Variables declared by this template. Surfaces the same metadata used by
    /// the cloud-init renderer's static/dynamic resolution pipeline. Most
    /// community templates leave this empty.
    /// </summary>
    public List<TemplateVariable>? Variables { get; set; }
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

public class AddArtifactRequest
{
    public required string Name { get; init; }

    /// <summary>
    /// Optional for inline artifacts (data: URI or <see cref="Content"/>) — server computes it.
    /// Required for external HTTPS artifacts so the fetched bytes can be verified.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// Either an HTTPS URL (fetched and SHA256-verified) or a data: URI
    /// (decoded inline). Optional when <see cref="Content"/> is supplied —
    /// the server constructs the data: URI from raw content.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Optional. Server fills this in from the decoded inline bytes
    /// or from the external response's Content-Length header.
    /// </summary>
    public long? SizeBytes { get; init; }

    public required ArtifactType Type { get; init; }
    public string? Description { get; init; }
    public string? Architecture { get; init; }

    /// <summary>
    /// Raw script/config content. When set (and <see cref="SourceUrl"/> is empty),
    /// the server base64-encodes the content and builds a data: URI using
    /// <see cref="ContentType"/> as the media type. Eliminates the need for the
    /// frontend to perform base64 encoding for "paste-script" UX.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Media type for <see cref="Content"/>. Defaults to "text/plain" if omitted.
    /// </summary>
    public string? ContentType { get; init; }
}
