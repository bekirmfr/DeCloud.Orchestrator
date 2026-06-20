using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services.SystemVm;
using Orchestrator.Services.Tenant;

namespace Orchestrator.Services;

/// <summary>
/// Seeds marketplace categories, then delegates template seeding to the two
/// per-tree seeders. System VM templates live in <see cref="SystemVmTemplateSeeder"/>;
/// all tenant templates (general, legacy marketplace, and compose-pipeline) live
/// in <see cref="Tenant.TenantVmTemplateSeeder"/>. This service owns only
/// category seeding and the top-level failure-domain orchestration.
/// </summary>
public class TemplateSeederService
{
    private readonly SystemVmTemplateSeeder _systemVmTemplateSeeder;
    private readonly TenantVmTemplateSeeder _tenantVmTemplateSeeder;
    private readonly DataStore _dataStore;
    private readonly ILogger<TemplateSeederService> _logger;

    public TemplateSeederService(
        SystemVmTemplateSeeder systemVmTemplateSeeder,
        TenantVmTemplateSeeder tenantVmTemplateSeeder,
        DataStore dataStore,
        ILogger<TemplateSeederService> logger)
    {
        _systemVmTemplateSeeder = systemVmTemplateSeeder;
        _tenantVmTemplateSeeder = tenantVmTemplateSeeder;
        _dataStore = dataStore;
        _logger = logger;
    }

    /// <summary>
    /// Seed categories and templates if they don't exist
    /// </summary>
    public async Task SeedAsync(bool force = false)
    {
        _logger.LogInformation("Starting template seeding (force: {Force})", force);

        // Each seeder runs in its own failure domain. One seeder's failure
        // does not block another — they manage logically independent data.
        await TrySeed("categories", () => SeedCategoriesAsync(force));
        await TrySeed("system VM", () => _systemVmTemplateSeeder.SeedAsync());
        await TrySeed("tenant", () => _tenantVmTemplateSeeder.SeedAsync(force));

        _logger.LogInformation("Template seeding completed");
    }

    private async Task TrySeed(string label, Func<Task> seeder)
    {
        try
        {
            await seeder();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Seeder failed: {Label}. Other seeders will continue. " +
                "Templates from this seeder will not be available until " +
                "the underlying issue is fixed and the orchestrator is restarted.",
                label);
        }
    }


    private async Task SeedCategoriesAsync(bool force)
    {
        var existingCategories = await _dataStore.GetCategoriesAsync();

        var categories = GetSeedCategories();

        foreach (var category in categories)
        {
            var existing = existingCategories.FirstOrDefault(c => c.Slug == category.Slug);

            if (existing == null)
            {
                await _dataStore.SaveCategoryAsync(category);
                _logger.LogInformation("Created category: {Name}", category.Name);
            }
            else if (force)
            {
                category.Id = existing.Id;
                await _dataStore.SaveCategoryAsync(category);
                _logger.LogInformation("Updated category: {Name}", category.Name);
            }
        }
    }


    private List<TemplateCategory> GetSeedCategories()
    {
        return new List<TemplateCategory>
        {
            new TemplateCategory
            {
                Name = "AI & Machine Learning",
                Slug = "ai-ml",
                Description = "Pre-configured environments for AI inference, training, and ML development",
                IconEmoji = "🤖",
                DisplayOrder = 1
            },
            new TemplateCategory
            {
                Name = "Databases",
                Slug = "databases",
                Description = "Production-ready database servers with optimized configurations",
                IconEmoji = "🗄️",
                DisplayOrder = 2
            },
            new TemplateCategory
            {
                Name = "Development Tools",
                Slug = "dev-tools",
                Description = "Development environments, IDEs, and productivity tools",
                IconEmoji = "🛠️",
                DisplayOrder = 3
            },
            new TemplateCategory
            {
                Name = "Web Applications",
                Slug = "web-apps",
                Description = "Content management systems, blogs, and web platforms",
                IconEmoji = "🌐",
                DisplayOrder = 4
            },
            new TemplateCategory
            {
                Name = "Privacy & Security",
                Slug = "privacy-security",
                Description = "Privacy-focused tools, VPNs, secure browsing, and censorship-resistant applications",
                IconEmoji = "🔒",
                DisplayOrder = 5
            },
            new TemplateCategory
            {
                Name = "Gaming",
                Slug = "gaming",
                Description = "Game servers and multiplayer worlds",
                IconEmoji = "🎮",
                DisplayOrder = 6
            }
        };
    }

}