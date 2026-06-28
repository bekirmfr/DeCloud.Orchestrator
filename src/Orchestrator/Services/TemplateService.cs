using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Orchestrator.Interfaces;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Text.RegularExpressions;

namespace Orchestrator.Services;

/// <summary>
/// Service for managing VM templates in the marketplace.
/// Supports platform-curated and user-created (community) templates.
/// </summary>
public class TemplateService : ITemplateService
{
    private readonly DataStore _dataStore;
    private readonly ICentralIngressService _ingressService;
    private readonly IWalletBlocklistService _blocklist;
    private readonly ILogger<TemplateService> _logger;

    // Cloud-init variable pattern: ${VARIABLE_NAME}
    private static readonly Regex VariablePattern = new(@"\$\{([A-Z_]+)\}", RegexOptions.Compiled);

    // Reserved author names that community users cannot claim
    private static readonly HashSet<string> ReservedAuthorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DeCloud", "DeCloud Official", "Platform", "Admin", "System"
    };

    public TemplateService(
        DataStore dataStore,
        ICentralIngressService ingressService,
        IWalletBlocklistService blocklist,
        ILogger<TemplateService> logger)
    {
        _dataStore = dataStore;
        _ingressService = ingressService;
        _blocklist = blocklist;
        _logger = logger;
    }


    // ════════════════════════════════════════════════════════════════════════
    // Template Queries
    // ════════════════════════════════════════════════════════════════════════

    public async Task<VmTemplate?> GetTemplateByIdAsync(string templateId)
    {
        try
        {
            return await _dataStore.GetTemplateByIdAsync(templateId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get template by ID: {TemplateId}", templateId);
            return null;
        }
    }

    public async Task<VmTemplate?> GetTemplateBySlugAsync(string slug)
    {
        try
        {
            return await _dataStore.GetTemplateBySlugAsync(slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get template by slug: {Slug}", slug);
            return null;
        }
    }

    public async Task<List<VmTemplate>> GetTemplatesAsync(TemplateQuery query)
    {
        try
        {
            var templates = await _dataStore.GetTemplatesAsync(
                category: query.Category,
                requiresGpu: query.RequiresGpu,
                tags: query.Tags,
                featuredOnly: query.FeaturedOnly,
                sortBy: query.SortBy);

            // Apply search term filter if provided
            if (!string.IsNullOrEmpty(query.SearchTerm))
            {
                var searchLower = query.SearchTerm.ToLower();
                templates = templates.Where(t =>
                    t.Name.ToLower().Contains(searchLower) ||
                    t.Description.ToLower().Contains(searchLower) ||
                    t.Tags.Any(tag => tag.ToLower().Contains(searchLower))
                ).ToList();
            }

            // Apply limit if specified
            if (query.Limit.HasValue && query.Limit.Value > 0)
            {
                templates = templates.Take(query.Limit.Value).ToList();
            }

            return templates;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get templates with query");
            return new List<VmTemplate>();
        }
    }

    public async Task<List<VmTemplate>> GetFeaturedTemplatesAsync(int limit = 10)
    {
        try
        {
            return await GetTemplatesAsync(new TemplateQuery
            {
                FeaturedOnly = true,
                SortBy = "popular",
                Limit = limit
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get featured templates");
            return new List<VmTemplate>();
        }
    }

    public async Task<List<TemplateCategory>> GetCategoriesAsync()
    {
        try
        {
            return await _dataStore.GetCategoriesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get categories");
            return new List<TemplateCategory>();
        }
    }

    public async Task<List<VmTemplate>> GetTemplatesByAuthorAsync(string authorId)
    {
        try
        {
            return await _dataStore.GetTemplatesByAuthorAsync(authorId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get templates for author: {AuthorId}", authorId);
            return new List<VmTemplate>();
        }
    }


    // ════════════════════════════════════════════════════════════════════════
    // Template Management
    // ════════════════════════════════════════════════════════════════════════

    public async Task<VmTemplate> CreateTemplateAsync(VmTemplate template)
    {
        try
        {
            // Validate before creating
            var validation = await ValidateTemplateAsync(template);
            if (!validation.IsValid)
            {
                throw new ArgumentException($"Template validation failed: {string.Join(", ", validation.Errors)}");
            }

            // Ensure timestamps
            template.CreatedAt = DateTime.UtcNow;
            template.UpdatedAt = DateTime.UtcNow;

            // Community templates start unverified and unfeatured, and can never be
            // created directly in Published — the only path to Published is
            // publish → review → approve.
            if (template.IsCommunity)
            {
                template.IsVerified = false;
                template.IsFeatured = false;
                if (template.Status == TemplateStatus.Published)
                    template.Status = TemplateStatus.Draft;
            }

            // Initialize rating fields
            template.AverageRating = 0;
            template.TotalReviews = 0;
            template.RatingDistribution = new int[5];

            // Save to database
            var saved = await _dataStore.SaveTemplateAsync(template);

            _logger.LogInformation(
                "Created template: {TemplateName} ({TemplateId}) by {AuthorId} in category {Category}",
                saved.Name, saved.Id, saved.AuthorId, saved.Category);

            // Update category counts if published + public
            if (saved.Status == TemplateStatus.Published && saved.Visibility == TemplateVisibility.Public)
            {
                await UpdateCategoryCountsAsync();
            }

            return saved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create template: {TemplateName}", template.Name);
            throw;
        }
    }

    public async Task<VmTemplate> UpdateTemplateAsync(VmTemplate template, bool isAdmin = false)
    {
        try
        {
            // Validate before updating
            var validation = await ValidateTemplateAsync(template);
            if (!validation.IsValid)
            {
                throw new ArgumentException($"Template validation failed: {string.Join(", ", validation.Errors)}");
            }

            // Preserve fields that users cannot change
            var existing = await GetTemplateByIdAsync(template.Id);
            if (existing != null)
            {
                // A template under review is locked: a non-admin cannot change its content
                // until they cancel the review (→ Draft). Admins (the reviewer) are exempt.
                if (!isAdmin && existing.Status == TemplateStatus.PendingReview)
                    throw new InvalidOperationException(
                        "Template is under review and cannot be edited. Cancel the review first.");

                // Preserve immutable fields
                template.AuthorId = existing.AuthorId;
                template.DeploymentCount = existing.DeploymentCount;
                template.LastDeployedAt = existing.LastDeployedAt;
                template.CreatedAt = existing.CreatedAt;
                template.AverageRating = existing.AverageRating;
                template.TotalReviews = existing.TotalReviews;
                template.RatingDistribution = existing.RatingDistribution;
                template.Artifacts = existing.Artifacts;

                // Admin can change classification flags; regular users cannot
                if (!isAdmin)
                {
                    template.IsCommunity = existing.IsCommunity;
                    template.IsVerified = existing.IsVerified;
                    template.IsFeatured = existing.IsFeatured;

                    // Status transitions to Published / PendingReview are owned by the
                    // review pipeline (publish → review → approve), never by an
                    // arbitrary update. Clamp any such attempt to the existing status.
                    if (template.Status is TemplateStatus.Published or TemplateStatus.PendingReview
                        && template.Status != existing.Status)
                    {
                        template.Status = existing.Status;
                    }

                    // A LIVE community template's deployable payload changes only through a
                    // reviewed revision (POST {id}/revise), so the live version stays in the
                    // marketplace. Cosmetic edits (name/description/tags/icon/pricing) still
                    // save in place. Refuse an in-place payload change and point to versioning.
                    if (existing.IsCommunity
                        && existing.Status == TemplateStatus.Published
                        && template.Status == TemplateStatus.Published
                        && DeployableSignature(existing) != DeployableSignature(template))
                    {
                        throw new InvalidOperationException(
                            "A published template's deployable content is changed by creating a new version. " +
                            "Use \"New version\" to start a revision for review.");
                    }
                }
            }

            template.UpdatedAt = DateTime.UtcNow;

            var updated = await _dataStore.SaveTemplateAsync(template);

            _logger.LogInformation(
                "Updated template: {TemplateName} ({TemplateId})",
                updated.Name, updated.Id);

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update template: {TemplateId}", template.Id);
            throw;
        }
    }

    /// <summary>
    /// Canonical signature of the fields that determine what a deployment runs and
    /// exposes — the surface a content review vouches for. Cosmetic fields (name,
    /// description, tags, icon, pricing) are excluded so editing them does not bounce a
    /// live template back into review. Artifacts are omitted because UpdateTemplateAsync
    /// already preserves them (they cannot change on update). Used to detect a
    /// post-approval payload edit.
    /// </summary>
    private static string DeployableSignature(VmTemplate t)
    {
        var payload = new
        {
            t.CloudInitTemplate,
            t.ContainerImage,
            t.ExposedPorts,
            t.Variables,
            // Order-stable so a dictionary re-ordering alone isn't seen as a change.
            Env = t.DefaultEnvironmentVariables == null
                ? null
                : t.DefaultEnvironmentVariables
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new { kv.Key, kv.Value })
                    .ToList()
        };
        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    public async Task SaveTemplateDirectAsync(VmTemplate template)
    {
        template.UpdatedAt = DateTime.UtcNow;
        await _dataStore.SaveTemplateAsync(template);
        _logger.LogInformation("Saved template: {Name} ({Id})", template.Name, template.Id);
    }

    public async Task<bool> DeleteTemplateAsync(string templateId, string requesterId)
    {
        try
        {
            var template = await GetTemplateByIdAsync(templateId);
            if (template == null)
            {
                _logger.LogWarning("Template not found for deletion: {TemplateId}", templateId);
                return false;
            }

            // Only the author can delete their template
            if (template.AuthorId != requesterId)
            {
                _logger.LogWarning(
                    "Unauthorized deletion attempt on template {TemplateId} by {RequesterId} (owner: {AuthorId})",
                    templateId, requesterId, template.AuthorId);
                throw new UnauthorizedAccessException("Only the template author can delete this template");
            }

            var deleted = await _dataStore.DeleteTemplateAsync(templateId);

            if (deleted)
            {
                // A revision is meaningless without its parent — cascade-delete any open
                // revision pointing at this template (author-scoped; a revision shares the
                // parent's author).
                var revisions = (await GetTemplatesByAuthorAsync(requesterId))
                    .Where(t => t.ParentTemplateId == templateId)
                    .ToList();
                foreach (var rev in revisions)
                    await _dataStore.DeleteTemplateAsync(rev.Id);

                _logger.LogInformation("Deleted template: {TemplateName} ({TemplateId}) by {AuthorId}",
                    template.Name, templateId, requesterId);
                await UpdateCategoryCountsAsync();
            }

            return deleted;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete template: {TemplateId}", templateId);
            return false;
        }
    }

    public async Task<VmTemplate> PublishTemplateAsync(string templateId, string requesterId)
    {
        var template = await GetTemplateByIdAsync(templateId);
        if (template == null)
            throw new ArgumentException($"Template not found: {templateId}");

        if (template.AuthorId != requesterId)
            throw new UnauthorizedAccessException("Only the template author can publish this template");

        // ── Compliance: enforcement gate ─────────────────────────────────────────
        // A suspended or denylisted author cannot publish. Templates are the highest-
        // leverage amplification surface (one public template deploys many times), so
        // the same predicate that guards VM creation guards publication. Checked at
        // action time, server-side; the reason is deliberately not surfaced.
        if (await _blocklist.IsWalletBlockedAsync(template.AuthorId))
            throw new UnauthorizedAccessException("This account is not permitted to publish templates.");

        if (template.Status == TemplateStatus.Published)
            throw new InvalidOperationException("Template is already published");

        if (template.Status == TemplateStatus.PendingReview)
            throw new InvalidOperationException("Template is already submitted for review");

        // Verify each artifact's SHA256 by fetching the source URL.
        // This is done once at publish time; subsequent downloads by node agents
        // trust the stored hash. Rejects the publish if any artifact's bytes
        // do not match the declared hash.
        if (template.Artifacts.Count > 0)
        {
            await VerifyArtifactsAsync(template, ct: CancellationToken.None);
        }

        // Full validation before publishing
        var validation = await ValidateTemplateAsync(template);
        if (!validation.IsValid)
            throw new ArgumentException($"Template validation failed: {string.Join(", ", validation.Errors)}");

        // Paid templates must have a revenue wallet
        if (template.PricingModel == TemplatePricingModel.PerDeploy && string.IsNullOrEmpty(template.AuthorRevenueWallet))
            throw new ArgumentException("Paid templates require an author revenue wallet address");

        // Community templates go to admin review, not live. Platform templates
        // (admin-authored, IsCommunity == false) publish directly. The only path a
        // community template reaches Published is via admin approval (slice 2).
        template.Status = template.IsCommunity
            ? TemplateStatus.PendingReview
            : TemplateStatus.Published;
        template.UpdatedAt = DateTime.UtcNow;

        var published = await _dataStore.SaveTemplateAsync(template);

        if (published.Status == TemplateStatus.PendingReview)
            _logger.LogInformation(
                "Template {TemplateName} ({TemplateId}) by {AuthorId} submitted for review",
                published.Name, published.Id, published.AuthorId);
        else
            _logger.LogInformation(
                "Published template: {TemplateName} ({TemplateId}) by {AuthorId}",
                published.Name, published.Id, published.AuthorId);

        // Category counts reflect only Published + Public templates, so a PendingReview
        // template isn't counted — this is a no-op for it, but harmless.
        await UpdateCategoryCountsAsync();

        return published;
    }

    public async Task<VmTemplate> ReviseTemplateAsync(string templateId, string requesterId)
    {
        var parent = await GetTemplateByIdAsync(templateId)
            ?? throw new KeyNotFoundException($"Template '{templateId}' not found");

        if (!string.Equals(parent.AuthorId, requesterId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only the author can revise this template.");

        // Versioning is a community-template concern; platform templates are admin-edited in place.
        if (!parent.IsCommunity)
            throw new InvalidOperationException("Only community templates use versioned revisions.");

        if (parent.Status != TemplateStatus.Published)
            throw new InvalidOperationException(
                "Only a published template is revised. Edit a draft or rejected template directly.");

        // At most one open revision per parent — idempotent: return the existing one so a
        // second "New version" reopens it rather than forking the review queue.
        var mine = await GetTemplatesByAuthorAsync(requesterId);
        var existing = mine.FirstOrDefault(t => t.ParentTemplateId == parent.Id);
        if (existing != null) return existing;

        // Deep clone via JSON round-trip so nested lists/specs are copied, not shared.
        var json = System.Text.Json.JsonSerializer.Serialize(parent);
        var revision = System.Text.Json.JsonSerializer.Deserialize<VmTemplate>(json)!;

        revision.Id = Guid.NewGuid().ToString();
        revision.ParentTemplateId = parent.Id;
        revision.Status = TemplateStatus.Draft;
        revision.IsVerified = false;
        revision.IsFeatured = false;
        revision.ReviewedBy = null;
        revision.ReviewedAt = null;
        revision.RejectionReason = null;
        revision.CreatedAt = DateTime.UtcNow;
        revision.UpdatedAt = DateTime.UtcNow;
        // Accrued history belongs to the live parent, not the revision.
        revision.AverageRating = 0;
        revision.TotalReviews = 0;
        revision.RatingDistribution = new int[5];
        revision.DeploymentCount = 0;
        revision.LastDeployedAt = null;

        var saved = await _dataStore.SaveTemplateAsync(revision);
        _logger.LogInformation(
            "Author {Author} opened revision {RevisionId} of template {ParentId} ({Name})",
            requesterId, saved.Id, parent.Id, parent.Name);
        return saved;
    }

    public async Task<VmTemplate> ApproveTemplateAsync(string templateId, string reviewerId)
    {
        var template = await GetTemplateByIdAsync(templateId)
            ?? throw new KeyNotFoundException($"Template '{templateId}' not found");

        if (template.Status != TemplateStatus.PendingReview)
            throw new InvalidOperationException(
                $"Template is not pending review (status: {template.Status}).");

        // A revision promotes onto its live parent in place instead of self-publishing, so
        // the parent keeps its id, slug, reviews, and deploy history — and the live version
        // is never pulled from the marketplace during review.
        if (template.ParentTemplateId != null)
        {
            var parent = await GetTemplateByIdAsync(template.ParentTemplateId);
            if (parent != null)
            {
                var revisionId = template.Id;

                // Carry the parent's identity, accrued history, and admin curation onto the
                // revision, then save it over the parent document. Copying identity forward
                // (not payload backward) can't miss an author-edited field. Artifacts ride
                // with the revision — a new version may legitimately change them.
                template.Id = parent.Id;
                template.AuthorId = parent.AuthorId;
                template.CreatedAt = parent.CreatedAt;
                template.AverageRating = parent.AverageRating;
                template.TotalReviews = parent.TotalReviews;
                template.RatingDistribution = parent.RatingDistribution;
                template.DeploymentCount = parent.DeploymentCount;
                template.LastDeployedAt = parent.LastDeployedAt;
                template.IsFeatured = parent.IsFeatured;
                template.IsCommunity = parent.IsCommunity;
                template.ParentTemplateId = null;
                template.Status = TemplateStatus.Published;
                template.IsVerified = true;
                template.ReviewedBy = reviewerId;
                template.ReviewedAt = DateTime.UtcNow;
                template.RejectionReason = null;
                template.UpdatedAt = DateTime.UtcNow;

                var promoted = await _dataStore.SaveTemplateAsync(template);
                await _dataStore.DeleteTemplateAsync(revisionId);
                await UpdateCategoryCountsAsync();

                _logger.LogInformation(
                    "Promoted revision {RevisionId} onto template {TemplateId} ({Name}) by {Reviewer}",
                    revisionId, promoted.Id, promoted.Name, reviewerId);
                return promoted;
            }

            // Parent deleted mid-review → publish the revision standalone.
            template.ParentTemplateId = null;
        }

        template.Status = TemplateStatus.Published;
        template.IsVerified = true;          // marketplace "reviewed" badge
        template.ReviewedBy = reviewerId;
        template.ReviewedAt = DateTime.UtcNow;
        template.RejectionReason = null;
        template.UpdatedAt = DateTime.UtcNow;

        var saved = await _dataStore.SaveTemplateAsync(template);
        await UpdateCategoryCountsAsync();

        _logger.LogInformation("Approved template {TemplateId} ({Name}) by {Reviewer}",
            saved.Id, saved.Name, reviewerId);
        return saved;
    }

    public async Task<VmTemplate> RejectTemplateAsync(string templateId, string reviewerId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A rejection reason is required.");

        var template = await GetTemplateByIdAsync(templateId)
            ?? throw new KeyNotFoundException($"Template '{templateId}' not found");

        if (template.Status != TemplateStatus.PendingReview)
            throw new InvalidOperationException(
                $"Template is not pending review (status: {template.Status}).");

        template.Status = TemplateStatus.Rejected;
        template.IsVerified = false;
        template.ReviewedBy = reviewerId;
        template.ReviewedAt = DateTime.UtcNow;
        template.RejectionReason = reason;
        template.UpdatedAt = DateTime.UtcNow;

        var saved = await _dataStore.SaveTemplateAsync(template);

        _logger.LogInformation("Rejected template {TemplateId} ({Name}) by {Reviewer}: {Reason}",
            saved.Id, saved.Name, reviewerId, reason);
        return saved;
    }

    public async Task<List<VmTemplate>> GetPendingReviewTemplatesAsync()
        => await _dataStore.GetTemplatesByStatusAsync(TemplateStatus.PendingReview);

    public async Task<VmTemplate> CancelReviewAsync(string templateId, string requesterId)
    {
        var template = await GetTemplateByIdAsync(templateId)
            ?? throw new KeyNotFoundException($"Template '{templateId}' not found");

        // Author-only. Withdrawing is the mirror of submitting, so it stays with the owner.
        if (!string.Equals(template.AuthorId, requesterId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only the author can cancel a review.");

        if (template.Status != TemplateStatus.PendingReview)
            throw new InvalidOperationException(
                $"Template is not pending review (status: {template.Status}).");

        template.Status = TemplateStatus.Draft;
        template.UpdatedAt = DateTime.UtcNow;

        var saved = await _dataStore.SaveTemplateAsync(template);
        _logger.LogInformation(
            "Author {Author} cancelled review for template {TemplateId} ({Name})",
            requesterId, saved.Id, saved.Name);
        return saved;
    }

    public async Task<TemplateValidationResult> ValidateTemplateAsync(VmTemplate template)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Required fields
        if (string.IsNullOrWhiteSpace(template.Name))
            errors.Add("Template name is required");

        if (string.IsNullOrWhiteSpace(template.Slug))
            errors.Add("Template slug is required");

        if (string.IsNullOrWhiteSpace(template.Category))
            errors.Add("Template category is required");

        if (string.IsNullOrWhiteSpace(template.Description))
            errors.Add("Template description is required");

        if (string.IsNullOrWhiteSpace(template.CloudInitTemplate))
            errors.Add("Cloud-init template is required");

        // Validate slug format (lowercase, alphanumeric, hyphens only)
        if (!string.IsNullOrWhiteSpace(template.Slug) &&
            !Regex.IsMatch(template.Slug, @"^[a-z0-9-]+$"))
        {
            errors.Add("Slug must be lowercase alphanumeric with hyphens only");
        }

        // Check for duplicate slug
        if (!string.IsNullOrWhiteSpace(template.Slug))
        {
            var existing = await _dataStore.GetTemplateBySlugAsync(template.Slug);
            if (existing != null && existing.Id != template.Id && existing.Id != template.ParentTemplateId)
            {
                errors.Add($"Slug '{template.Slug}' is already in use");
            }
        }

        // Validate author name not reserved (for community templates)
        if (template.IsCommunity && !string.IsNullOrWhiteSpace(template.AuthorName) &&
            ReservedAuthorNames.Contains(template.AuthorName))
        {
            errors.Add($"Author name '{template.AuthorName}' is reserved");
        }

        // Validate pricing
        if (template.PricingModel == TemplatePricingModel.PerDeploy)
        {
            if (template.TemplatePrice <= 0)
                errors.Add("Paid templates must have a price greater than 0");

            if (template.TemplatePrice > 1000)
                errors.Add("Template price cannot exceed 1000 USDC");
        }

        // Validate specifications
        if (template.MinimumSpec == null)
        {
            errors.Add("Minimum specification is required");
        }
        else
        {
            if (template.MinimumSpec.VirtualCpuCores < 1 || template.MinimumSpec.VirtualCpuCores > 32)
                errors.Add("Minimum CPU cores must be between 1 and 32");

            if (template.MinimumSpec.MemoryBytes < 256L * 1024 * 1024) // 256 MB
                errors.Add("Minimum memory must be at least 256 MB");

            if (template.MinimumSpec.DiskBytes < 1L * 1024 * 1024 * 1024) // 1 GB
                errors.Add("Minimum disk must be at least 1 GB");
        }

        if (template.RecommendedSpec == null)
        {
            warnings.Add("Recommended specification not provided");
        }
        else
        {
            if (template.RecommendedSpec.VirtualCpuCores < 1 || template.RecommendedSpec.VirtualCpuCores > 32)
                errors.Add("Recommended CPU cores must be between 1 and 32");

            if (template.RecommendedSpec.MemoryBytes < 256L * 1024 * 1024)
                errors.Add("Recommended memory must be at least 256 MB");

            // Recommended should be >= minimum
            if (template.MinimumSpec != null)
            {
                if (template.RecommendedSpec.VirtualCpuCores < template.MinimumSpec.VirtualCpuCores)
                    errors.Add("Recommended CPU cores must be >= minimum");

                if (template.RecommendedSpec.MemoryBytes < template.MinimumSpec.MemoryBytes)
                    errors.Add("Recommended memory must be >= minimum");

                if (template.RecommendedSpec.DiskBytes < template.MinimumSpec.DiskBytes)
                    errors.Add("Recommended disk must be >= minimum");
            }
        }

        // Validate cloud-init YAML
        if (!string.IsNullOrWhiteSpace(template.CloudInitTemplate))
        {
            if (!template.CloudInitTemplate.TrimStart().StartsWith("#cloud-config"))
            {
                warnings.Add("Cloud-init template should start with '#cloud-config'");
            }

            // Check for suspicious commands
            var suspiciousPatterns = new[]
            {
                @"rm\s+-rf\s+/",                   // Dangerous: Delete root filesystem
                @"dd\s+if=/dev/zero",              // Dangerous: Wipe disk
                @":\(\)\s*\{\s*:\|\:&\s*\};\s*:",  // Dangerous: Fork bomb
            };

            foreach (var pattern in suspiciousPatterns)
            {
                if (Regex.IsMatch(template.CloudInitTemplate, pattern, RegexOptions.IgnoreCase))
                {
                    errors.Add($"Cloud-init contains potentially dangerous command matching pattern: {pattern}");
                }
            }

            // Check for curl/wget pipe to shell, but allow trusted domains
            var trustedDomains = new[]
            {
                "deb.nodesource.com",              // Node.js official
                "get.docker.com",                   // Docker official
                "code-server.dev",                  // code-server official
                "github.com/nvm-sh/nvm",           // nvm official
                "sh.rustup.rs",                     // Rust official
                "nixos.org",                        // Nix official
                "raw.githubusercontent.com",        // GitHub raw content
                "dl.k8s.io",                        // Kubernetes official
                "get.helm.sh",                      // Helm official
                "ollama.com",                       // Ollama official
                "nvidia.github.io"                  // NVIDIA Container Toolkit
            };

            // Find all curl|bash and wget|sh patterns
            var pipeToShellMatches = Regex.Matches(
                template.CloudInitTemplate,
                @"(curl|wget)\s+.*?\|\s*(bash|sh)",
                RegexOptions.IgnoreCase);

            foreach (Match match in pipeToShellMatches)
            {
                var command = match.Value;
                var isTrusted = trustedDomains.Any(domain =>
                    command.Contains(domain, StringComparison.OrdinalIgnoreCase));

                if (!isTrusted)
                {
                    // Warn for community templates, block for platform
                    if (template.IsCommunity)
                    {
                        warnings.Add($"Cloud-init contains download-and-execute from unverified source: {command}");
                    }
                    else
                    {
                        errors.Add($"Cloud-init contains untrusted download-and-execute command: {command}. " +
                                  $"Only commands from trusted domains are allowed.");
                    }
                }
            }
        }

        // Validate ports
        foreach (var port in template.ExposedPorts)
        {
            if (port.Port < 1 || port.Port > 65535)
                errors.Add($"Invalid port number: {port.Port}");

            if (string.IsNullOrWhiteSpace(port.Description))
                warnings.Add($"Port {port.Port} has no description");
        }

        // Validate cost estimate
        if (template.EstimatedCostPerHour < 0)
            errors.Add("Estimated cost cannot be negative");

        if (template.EstimatedCostPerHour == 0)
            warnings.Add("Estimated cost per hour is not set");

        // Validate variables: names must be non-empty and unique within the
        // template. Same check applied to both create and update paths.
        if (template.Variables.Count > 0)
        {
            var seenVarNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in template.Variables)
            {
                if (string.IsNullOrWhiteSpace(v.Name))
                {
                    errors.Add("Variable name is required");
                    continue;
                }

                if (!seenVarNames.Add(v.Name))
                    errors.Add(
                        $"Duplicate variable name '{v.Name}'. " +
                        "Variable names must be unique within a template.");
            }
        }

        return new TemplateValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings
        };
    }


    // ════════════════════════════════════════════════════════════════════════
    // Deployment Helpers
    // ════════════════════════════════════════════════════════════════════════

    public async Task<CreateVmRequest> BuildVmRequestFromTemplateAsync(
        string templateId,
        string vmName,
        VmSpec? customSpec = null,
        Dictionary<string, string>? environmentVariables = null,
        string? targetArchitecture = null)
    {
        var template = await GetTemplateByIdAsync(templateId);
        if (template == null)
        {
            throw new ArgumentException($"Template not found: {templateId}");
        }

        // Use custom spec or template's recommended spec
        var spec = customSpec ?? template.RecommendedSpec ?? template.MinimumSpec;

        // Apply template defaults when using template spec (not custom)
        if (customSpec == null)
        {
            spec.BandwidthTier = template.DefaultBandwidthTier;

            // Apply template's default GPU mode to the spec if not already set
            if (spec.GpuMode == GpuMode.None && template.DefaultGpuMode != GpuMode.None)
            {
                spec.GpuMode = template.DefaultGpuMode;
            }
        }

        // Merge environment variables (template defaults + user overrides)
        var mergedEnvVars = new Dictionary<string, string>(template.DefaultEnvironmentVariables);
        if (environmentVariables != null)
        {
            foreach (var kvp in environmentVariables)
            {
                mergedEnvVars[kvp.Key] = kvp.Value;
            }
        }

        // Inject artifact URL variables if the template has artifacts.
        // Architecture is resolved from the template spec (amd64 is the default
        // for x86-64 nodes; arm64 is set explicitly for ARM deployments).
        if (template.Artifacts.Count > 0 && targetArchitecture is not null)
        {
            var artifactVars = ResolveArtifactVariables(template, targetArchitecture);
            foreach (var (key, value) in artifactVars)
                mergedEnvVars.TryAdd(key, value);
        }

        _logger.LogInformation(
            "Building VM request from template {TemplateName} for VM {VmName}",
            template.Name, vmName);

        return new CreateVmRequest(
            Name: vmName,
            Spec: spec,
            Category: VmCategory.Tenant,
            Role: VmRole.General,
            NodeId: null,
            Labels: null,
            TemplateId: templateId,
            EnvironmentVariables: mergedEnvVars
        );
    }

    /// <summary>
    /// Verify all artifacts on a template at publish time.
    ///
    /// For HTTPS artifacts: fetches the URL and verifies SHA256.
    /// For data: artifacts: decodes the base64 payload and verifies SHA256.
    ///
    /// Enforces:
    ///   - Binary artifacts must use HTTPS (never data: URIs).
    ///   - Per-artifact inline size limit (TemplateArtifact.MaxInlineArtifactBytes).
    ///   - Total inline size limit per template (TemplateArtifact.MaxTotalInlineBytes).
    ///   - Artifact names must be unique within the template.
    /// </summary>
    private async Task VerifyArtifactsAsync(VmTemplate template, CancellationToken ct)
    {
        // ── Name uniqueness ─────────────────────────────────────────────────
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in template.Artifacts)
        {
            if (!names.Add(artifact.Name))
                throw new ArgumentException(
                    $"Duplicate artifact name '{artifact.Name}' in template '{template.Slug}'. " +
                    "Artifact names must be unique within a template.");
        }

        // ── Per-artifact verification ────────────────────────────────────────
        long totalInlineBytes = 0;

        foreach (var artifact in template.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Sha256))
                throw new ArgumentException(
                    $"Artifact '{artifact.Name}' is missing Sha256.");

            if (artifact.IsInline)
            {
                await VerifyInlineArtifactAsync(artifact, ref totalInlineBytes);
            }
            else
            {
                await VerifyExternalArtifactAsync(artifact, ct);
            }
        }

        // ── Total inline budget ──────────────────────────────────────────────
        if (totalInlineBytes > TemplateArtifact.MaxTotalInlineBytes)
            throw new ArgumentException(
                $"Total inline attachment size {totalInlineBytes / 1024 / 1024.0:F1} MB " +
                $"exceeds the per-template limit of " +
                $"{TemplateArtifact.MaxTotalInlineBytes / 1024 / 1024} MB. " +
                "Move larger assets to an external HTTPS URL.");
    }

    private static Task VerifyInlineArtifactAsync(
        TemplateArtifact artifact,
        ref long totalInlineBytes)
    {
        // Compiled binaries must always use external HTTPS hosting.
        if (artifact.Type == ArtifactType.Binary)
            throw new ArgumentException(
                $"Artifact '{artifact.Name}' is type Binary and cannot use a data: URI. " +
                "Compiled binaries must be hosted externally (GitHub Releases, S3, CDN) " +
                "and registered with an HTTPS SourceUrl.");

        // Parse and decode the data: URI.
        var commaIndex = artifact.SourceUrl.IndexOf(',');
        if (commaIndex < 0)
            throw new ArgumentException(
                $"Artifact '{artifact.Name}' has a malformed data: URI — no comma separator.");

        var header = artifact.SourceUrl[..commaIndex];
        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Artifact '{artifact.Name}' data: URI must use base64 encoding " +
                "(e.g., data:text/x-sh;base64,...).");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(artifact.SourceUrl[(commaIndex + 1)..].Trim());
        }
        catch (FormatException)
        {
            throw new ArgumentException(
                $"Artifact '{artifact.Name}' data: URI contains invalid base64.");
        }

        // Per-artifact size limit.
        if (bytes.Length > TemplateArtifact.MaxInlineArtifactBytes)
            throw new ArgumentException(
                $"Artifact '{artifact.Name}' decoded size {bytes.Length / 1024 / 1024.0:F1} MB " +
                $"exceeds the per-artifact inline limit of " +
                $"{TemplateArtifact.MaxInlineArtifactBytes / 1024 / 1024} MB. " +
                "Host larger files externally and register with an HTTPS SourceUrl.");

        // Verify SHA256 of decoded bytes.
        var actualHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        if (!string.Equals(actualHash, artifact.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException(
                $"Artifact '{artifact.Name}' SHA256 mismatch. " +
                $"Declared: {artifact.Sha256[..12]}, " +
                $"Actual (decoded bytes): {actualHash[..12]}. " +
                "Recompute SHA256 of the decoded file content and update the Sha256 field.");

        // Update SizeBytes to the decoded size (author may have provided 0 or base64 size).
        artifact.SizeBytes = bytes.Length;

        totalInlineBytes += bytes.Length;
        return Task.CompletedTask;
    }

    private async Task VerifyExternalArtifactAsync(
        TemplateArtifact artifact,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artifact.SourceUrl))
            throw new ArgumentException(
                $"Artifact '{artifact.Name}' is missing SourceUrl.");

        if (!Uri.TryCreate(artifact.SourceUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https")
            throw new ArgumentException(
                $"Artifact '{artifact.Name}' SourceUrl must be HTTPS: {artifact.SourceUrl}");

        _logger.LogInformation(
            "Verifying external artifact '{Name}' at {Url}",
            artifact.Name, artifact.SourceUrl);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(
            artifact.SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actual, artifact.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
            throw new ArgumentException(
                $"Artifact '{artifact.Name}' SHA256 mismatch. " +
                $"Declared: {artifact.Sha256[..12]}, Actual: {actual[..12]}. " +
                "Update the Sha256 field to match the current bytes at SourceUrl.");

        // Stamp the verified size.
        if (response.Content.Headers.ContentLength.HasValue)
            artifact.SizeBytes = response.Content.Headers.ContentLength.Value;

        _logger.LogInformation(
            "✓ External artifact '{Name}' verified ({Sha256})",
            artifact.Name, artifact.Sha256[..12]);
    }

    public string SubstituteCloudInitVariables(
        string cloudInitTemplate,
        Dictionary<string, string> variables)
    {
        if (string.IsNullOrWhiteSpace(cloudInitTemplate))
            return cloudInitTemplate;

        var result = cloudInitTemplate;

        result = VariablePattern.Replace(result, match =>
        {
            var variableName = match.Groups[1].Value;
            if (variables.TryGetValue(variableName, out var value))
            {
                return value;
            }

            _logger.LogWarning("Variable {Variable} not found in substitution dictionary", variableName);
            return match.Value;
        });

        return result;
    }

    public Dictionary<string, string> GetAvailableVariables(VirtualMachine vm, Node? node = null)
    {
        var baseDomain = _ingressService.BaseDomain ?? "vms.decloud.app";
        var defaultSubdomain = vm.IngressConfig?.DefaultSubdomain ?? $"{vm.Id}.{baseDomain}";

        var variables = new Dictionary<string, string>
        {
            ["DECLOUD_VM_ID"] = vm.Id,
            ["DECLOUD_VM_NAME"] = vm.Name,
            ["DECLOUD_DOMAIN"] = defaultSubdomain,
            ["DECLOUD_VM_CREATED_AT"] = vm.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            ["DECLOUD_OWNER_ID"] = vm.OwnerId ?? "unknown"
        };

        variables["DECLOUD_CPU_CORES"] = vm.Spec.VirtualCpuCores.ToString();
        variables["DECLOUD_MEMORY_MB"] = (vm.Spec.MemoryBytes / (1024 * 1024)).ToString();
        variables["DECLOUD_DISK_GB"] = (vm.Spec.DiskBytes / (1024 * 1024 * 1024)).ToString();

        if (!string.IsNullOrEmpty(vm.Spec.WalletEncryptedPassword))
        {
            variables["DECLOUD_ENCRYPTED_PASSWORD"] = vm.Spec.WalletEncryptedPassword;
        }

        if (node != null)
        {
            variables["DECLOUD_NODE_ID"] = node.Id;
            variables["DECLOUD_NODE_REGION"] = node.Locality.Region ?? "unknown";
            variables["DECLOUD_NODE_ZONE"] = node.Locality.Zone ?? "unknown";
        }

        if (!string.IsNullOrEmpty(vm.NetworkConfig?.PrivateIp))
        {
            variables["DECLOUD_PRIVATE_IP"] = vm.NetworkConfig.PrivateIp;
        }

        if (!string.IsNullOrEmpty(vm.NetworkConfig?.PublicIp))
        {
            variables["DECLOUD_PUBLIC_IP"] = vm.NetworkConfig.PublicIp;
        }
        else if (node != null && !string.IsNullOrEmpty(node.PublicIp))
        {
            variables["DECLOUD_PUBLIC_IP"] = node.PublicIp;
        }

        return variables;
    }

    /// <summary>
    /// Build the ${ARTIFACT_URL:name} and ${ARTIFACT_SHA256:name} variable
    /// substitutions for artifacts in a template, filtered to the target
    /// architecture. Called from BuildVmRequestFromTemplateAsync when the
    /// template has artifacts.
    ///
    /// All artifact URLs resolve to the node agent's local cache endpoint
    /// (http://192.168.122.1:5100/api/artifacts/{sha256}) rather than the
    /// upstream SourceUrl. The VM never sees the upstream URL — it always
    /// fetches from the local cache, which the node agent has pre-verified.
    /// </summary>
    private static Dictionary<string, string> ResolveArtifactVariables(
        VmTemplate template,
        string targetArchitecture) // "amd64" or "arm64"
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var artifact in template.Artifacts)
        {
            // Skip arch-specific artifacts that don't match the target.
            // null Architecture means universal (scripts, configs, web assets).
            if (artifact.Architecture is not null &&
                !string.Equals(artifact.Architecture, targetArchitecture,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            // Always resolve to local cache — VMs never see upstream URLs.
            variables[$"ARTIFACT_URL:{artifact.Name}"] =
                $"http://192.168.122.1:5100/api/artifacts/{artifact.Sha256}";

            variables[$"ARTIFACT_SHA256:{artifact.Name}"] = artifact.Sha256;
        }

        return variables;
    }


    // ════════════════════════════════════════════════════════════════════════
    // Statistics & Ratings
    // ════════════════════════════════════════════════════════════════════════

    public async Task IncrementDeploymentCountAsync(string templateId)
    {
        try
        {
            var success = await _dataStore.IncrementTemplateDeploymentCountAsync(templateId);
            if (!success)
            {
                _logger.LogWarning("Failed to increment deployment count for template: {TemplateId}", templateId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error incrementing deployment count for template: {TemplateId}", templateId);
        }
    }

    public async Task UpdateTemplateStatsAsync(string templateId)
    {
        try
        {
            var template = await GetTemplateByIdAsync(templateId);
            if (template == null)
            {
                _logger.LogWarning("Cannot update stats for non-existent template: {TemplateId}", templateId);
                return;
            }

            template.LastDeployedAt = DateTime.UtcNow;
            template.UpdatedAt = DateTime.UtcNow;

            await _dataStore.SaveTemplateAsync(template);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating template stats: {TemplateId}", templateId);
        }
    }

    public async Task UpdateCategoryCountsAsync()
    {
        try
        {
            await _dataStore.UpdateCategoryCountsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating category counts");
        }
    }

    public async Task UpdateTemplateRatingsAsync(string templateId)
    {
        try
        {
            var template = await GetTemplateByIdAsync(templateId);
            if (template == null) return;

            var (averageRating, totalReviews, distribution) =
                await _dataStore.GetRatingAggregateAsync("template", templateId);

            template.AverageRating = averageRating;
            template.TotalReviews = totalReviews;
            template.RatingDistribution = distribution;
            template.UpdatedAt = DateTime.UtcNow;

            await _dataStore.SaveTemplateAsync(template);

            _logger.LogInformation(
                "Updated ratings for template {TemplateId}: {Average}/5 ({Total} reviews)",
                templateId, averageRating, totalReviews);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating template ratings: {TemplateId}", templateId);
        }
    }
}
