using System.Text.RegularExpressions;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Service for managing VM templates in the marketplace.
/// Supports platform-curated and user-created (community) templates.
/// </summary>
public class TemplateService : ITemplateService
{
    private readonly DataStore _dataStore;
    private readonly ICentralIngressService _ingressService;
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
        ILogger<TemplateService> logger)
    {
        _dataStore = dataStore;
        _ingressService = ingressService;
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

            // Community templates start unverified and unfeatured
            if (template.IsCommunity)
            {
                template.IsVerified = false;
                template.IsFeatured = false;
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

    public async Task<VmTemplate> UpdateTemplateAsync(VmTemplate template)
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
                // Preserve immutable fields
                template.AuthorId = existing.AuthorId;
                template.IsCommunity = existing.IsCommunity;
                template.IsVerified = existing.IsVerified;
                template.IsFeatured = existing.IsFeatured;
                template.DeploymentCount = existing.DeploymentCount;
                template.LastDeployedAt = existing.LastDeployedAt;
                template.CreatedAt = existing.CreatedAt;
                template.AverageRating = existing.AverageRating;
                template.TotalReviews = existing.TotalReviews;
                template.RatingDistribution = existing.RatingDistribution;
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

        if (template.Status == TemplateStatus.Published)
            throw new InvalidOperationException("Template is already published");

        // Full validation before publishing
        var validation = await ValidateTemplateAsync(template);
        if (!validation.IsValid)
            throw new ArgumentException($"Template validation failed: {string.Join(", ", validation.Errors)}");

        // Paid templates must have a revenue wallet
        if (template.PricingModel == TemplatePricingModel.PerDeploy && string.IsNullOrEmpty(template.AuthorRevenueWallet))
            throw new ArgumentException("Paid templates require an author revenue wallet address");

        template.Status = TemplateStatus.Published;
        template.UpdatedAt = DateTime.UtcNow;

        var published = await _dataStore.SaveTemplateAsync(template);

        _logger.LogInformation("Published template: {TemplateName} ({TemplateId}) by {AuthorId}",
            published.Name, published.Id, published.AuthorId);

        await UpdateCategoryCountsAsync();

        return published;
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
            if (existing != null && existing.Id != template.Id)
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

            if (template.MinimumSpec.MemoryBytes < 512 * 1024 * 1024) // 512 MB
                errors.Add("Minimum memory must be at least 512 MB");

            if (template.MinimumSpec.DiskBytes < 10L * 1024 * 1024 * 1024) // 10 GB
                errors.Add("Minimum disk must be at least 10 GB");
        }

        if (template.RecommendedSpec == null)
        {
            warnings.Add("Recommended specification not provided");
        }
        else
        {
            if (template.RecommendedSpec.VirtualCpuCores < 1 || template.RecommendedSpec.VirtualCpuCores > 32)
                errors.Add("Recommended CPU cores must be between 1 and 32");

            if (template.RecommendedSpec.MemoryBytes < 512 * 1024 * 1024)
                errors.Add("Recommended memory must be at least 512 MB");

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
        Dictionary<string, string>? environmentVariables = null)
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

        _logger.LogInformation(
            "Building VM request from template {TemplateName} for VM {VmName}",
            template.Name, vmName);

        return new CreateVmRequest(
            Name: vmName,
            Spec: spec,
            VmType: VmType.General,
            NodeId: null,
            Labels: null,
            TemplateId: templateId,
            EnvironmentVariables: mergedEnvVars
        );
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
            variables["DECLOUD_NODE_REGION"] = node.Region ?? "unknown";
            variables["DECLOUD_NODE_ZONE"] = node.Zone ?? "unknown";
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
