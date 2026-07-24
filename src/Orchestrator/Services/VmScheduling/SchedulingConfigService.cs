using DeCloud.Shared.Enums;
using MongoDB.Driver;
using Orchestrator.Models;

namespace Orchestrator.Services.VmScheduling;

/// <summary>
/// Service interface for managing scheduling configuration
/// </summary>
public interface ISchedulingConfigService
{
    /// <summary>
    /// Get current scheduling configuration (cached)
    /// </summary>
    Task<SchedulingConfig> GetConfigAsync(CancellationToken ct = default);

    /// <summary>
    /// Update scheduling configuration
    /// </summary>
    Task<SchedulingConfig> UpdateConfigAsync(
        SchedulingConfig config,
        string updatedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Force reload configuration from database
    /// </summary>
    Task ReloadConfigAsync(CancellationToken ct = default);

    /// <summary>
    /// Get configuration history (optional - for audit trail)
    /// </summary>
    Task<List<SchedulingConfig>> GetConfigHistoryAsync(int limit = 10, CancellationToken ct = default);
}

/// <summary>
/// Manages scheduling configuration with database backing and caching
/// Ensures high performance through cached reads with controlled updates
/// </summary>
public class SchedulingConfigService : ISchedulingConfigService
{
    private readonly IMongoCollection<SchedulingConfig>? _configs;
    private readonly ILogger<SchedulingConfigService> _logger;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly bool _useMongoDB;

    // Cache fields
    private SchedulingConfig? _cachedConfig;
    private DateTime _lastLoaded = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Schema version of the stored scheduling config. Bump when a change must be
    /// applied to EXISTING documents, and add the corresponding step to
    /// <see cref="MigrateConfigAsync"/>. Editing CreateDefaultConfig alone only
    /// affects installs that have never persisted a config.
    ///
    /// v2 — tier PriceMultipliers aligned to what HourlyRateCalculator actually
    ///      bills. See MigrateConfigAsync for the reasoning.
    /// </summary>
    private const int CurrentConfigVersion = 2;

    /// <summary>
    /// The multipliers HourlyRateCalculator hardcoded before it read them from
    /// here. Kept as the canonical set so v1 documents converge on the values
    /// users have actually been charged.
    /// </summary>
    private static readonly Dictionary<QualityTier, decimal> BilledPriceMultipliers = new()
    {
        [QualityTier.Guaranteed] = 2.5m,
        [QualityTier.Standard] = 1.0m,
        [QualityTier.Balanced] = 0.6m,
        [QualityTier.Burstable] = 0.4m,
    };

    public SchedulingConfigService(
        IMongoDatabase? database,
        ILogger<SchedulingConfigService> logger)
    {
        _logger = logger;
        _useMongoDB = database != null;

        if (_useMongoDB)
        {
            _configs = database!.GetCollection<SchedulingConfig>("scheduling_configs");

            // Create indexes
            CreateIndexesAsync().GetAwaiter().GetResult();
        }
        else
        {
            _logger.LogWarning("SchedulingConfigService running without MongoDB - using in-memory config only");
        }
    }

    private async Task CreateIndexesAsync()
    {
        if (!_useMongoDB || _configs == null) return;

        try
        {
            var indexKeys = Builders<SchedulingConfig>.IndexKeys.Descending(c => c.Version);
            var indexModel = new CreateIndexModel<SchedulingConfig>(indexKeys, new CreateIndexOptions
            {
                Name = "idx_version"
            });
            await _configs.Indexes.CreateOneAsync(indexModel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create indexes for scheduling_configs");
        }
    }

    /// <summary>
    /// Get current scheduling configuration with caching
    /// </summary>
    public async Task<SchedulingConfig> GetConfigAsync(CancellationToken ct = default)
    {
        // Check cache first (fast path)
        if (_cachedConfig != null && DateTime.UtcNow - _lastLoaded < CacheDuration)
        {
            return _cachedConfig;
        }

        // Cache miss or expired - reload
        await _cacheLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock (another thread may have loaded it)
            if (_cachedConfig != null && DateTime.UtcNow - _lastLoaded < CacheDuration)
            {
                return _cachedConfig;
            }

            _logger.LogInformation("Loading scheduling configuration from database...");

            SchedulingConfig config;

            if (_useMongoDB && _configs != null)
            {
                // Load from database
                config = await _configs
                    .Find(c => c.Id == "default")
                    .FirstOrDefaultAsync(ct);

                if (config == null)
                {
                    _logger.LogWarning("No scheduling configuration found, creating default...");
                    config = CreateDefaultConfig();
                    await _configs.InsertOneAsync(config, cancellationToken: ct);
                    _logger.LogInformation("Created default scheduling configuration v{Version}", config.Version);
                }
                else if (config.Version < CurrentConfigVersion)
                {
                    config = await MigrateConfigAsync(config, ct);
                }
            }
            else
            {
                // In-memory mode - always use default config
                if (_cachedConfig == null)
                {
                    _logger.LogInformation("Creating in-memory default configuration...");
                    config = CreateDefaultConfig();
                }
                else
                {
                    config = _cachedConfig;
                }
            }

            // Validate configuration
            ValidateConfig(config);

            // Update cache
            _cachedConfig = config;
            _lastLoaded = DateTime.UtcNow;

            _logger.LogInformation(
                "Loaded scheduling config v{Version} (Baseline: {Baseline}, Tiers: {TierCount})",
                config.Version, config.BaselineBenchmark, config.Tiers.Count);

            return config;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scheduling configuration");
            throw;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Update scheduling configuration with validation
    /// </summary>
    public async Task<SchedulingConfig> UpdateConfigAsync(
        SchedulingConfig config,
        string updatedBy,
        CancellationToken ct = default)
    {
        // Validate before saving
        ValidateConfig(config);

        if (!_useMongoDB || _configs == null)
        {
            _logger.LogWarning("Cannot persist configuration update - MongoDB not configured");

            // Update in-memory cache only
            await _cacheLock.WaitAsync(ct);
            try
            {
                config.Id = "default";
                config.UpdatedAt = DateTime.UtcNow;
                config.UpdatedBy = updatedBy;
                config.Version = (_cachedConfig?.Version ?? 0) + 1;

                _cachedConfig = config;
                _lastLoaded = DateTime.UtcNow;

                return config;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        await _cacheLock.WaitAsync(ct);
        try
        {
            // Load current version to preserve history
            var currentConfig = await _configs!
                .Find(c => c.Id == "default")
                .FirstOrDefaultAsync(ct);

            if (currentConfig != null)
            {
                // Archive current version (optional - for history)
                var archived = CloneForHistory(currentConfig);
                archived.Id = $"history_{currentConfig.Version}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                await _configs.InsertOneAsync(archived, cancellationToken: ct);

                // Increment version
                config.Version = currentConfig.Version + 1;
            }
            else
            {
                config.Version = 1;
            }

            // Update metadata
            config.Id = "default";
            config.UpdatedAt = DateTime.UtcNow;
            config.UpdatedBy = updatedBy;

            // Save to database
            await _configs.ReplaceOneAsync(
                c => c.Id == "default",
                config,
                new ReplaceOptions { IsUpsert = true },
                ct);

            // Invalidate cache immediately
            _cachedConfig = config;
            _lastLoaded = DateTime.UtcNow;

            _logger.LogWarning(
                "Scheduling configuration updated to v{Version} by {User}. " +
                "Baseline: {Baseline}, Tiers: {TierCount}",
                config.Version, updatedBy, config.BaselineBenchmark, config.Tiers.Count);

            // Note: Node re-evaluation should be triggered externally by background service

            return config;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Force reload configuration from database (clears cache)
    /// </summary>
    public async Task ReloadConfigAsync(CancellationToken ct = default)
    {
        await _cacheLock.WaitAsync(ct);
        try
        {
            _cachedConfig = null;
            _lastLoaded = DateTime.MinValue;
            _logger.LogInformation("Configuration cache cleared, will reload on next request");
        }
        finally
        {
            _cacheLock.Release();
        }

        // Trigger immediate reload
        await GetConfigAsync(ct);
    }

    /// <summary>
    /// Get configuration history for audit trail
    /// </summary>
    public async Task<List<SchedulingConfig>> GetConfigHistoryAsync(
        int limit = 10,
        CancellationToken ct = default)
    {
        if (!_useMongoDB || _configs == null)
        {
            _logger.LogWarning("Cannot retrieve configuration history - MongoDB not configured");
            return new List<SchedulingConfig>();
        }

        return await _configs
            .Find(c => c.Id.StartsWith("history_"))
            .SortByDescending(c => c.UpdatedAt)
            .Limit(limit)
            .ToListAsync(ct);
    }

    // ========================================
    // VALIDATION
    // ========================================

    private void ValidateConfig(SchedulingConfig config)
    {
        if (config.BaselineBenchmark <= 0)
            throw new ArgumentException("BaselineBenchmark must be positive");

        if (config.MaxPerformanceMultiplier <= 0)
            throw new ArgumentException("MaxPerformanceMultiplier must be positive");

        if (config.Tiers == null || config.Tiers.Count == 0)
            throw new ArgumentException("At least one tier must be configured");

        // Validate Burstable tier exists (required as baseline)
        if (!config.Tiers.ContainsKey(QualityTier.Burstable))
            throw new ArgumentException("Burstable tier must be configured (required as baseline)");

        // Validate scoring weights
        if (!config.Weights.IsValid(out var weightsError))
            throw new ArgumentException($"Invalid scoring weights: {weightsError}");

        // Validate each tier
        foreach (var (tier, tierConfig) in config.Tiers)
        {
            if (tierConfig.MinimumBenchmark <= 0)
                throw new ArgumentException($"Tier {tier} has invalid minimum benchmark");

            if (tierConfig.CpuOvercommitRatio <= 0)
                throw new ArgumentException($"Tier {tier} has invalid CPU overcommit ratio");

            if (tierConfig.StorageOvercommitRatio <= 0)
                throw new ArgumentException($"Tier {tier} has invalid storage overcommit ratio");

            if (tierConfig.PriceMultiplier < 0)
                throw new ArgumentException($"Tier {tier} has invalid price multiplier");
        }

        // Validate limits
        if (config.Limits.MaxUtilizationPercent <= 0 || config.Limits.MaxUtilizationPercent > 100)
            throw new ArgumentException("MaxUtilizationPercent must be between 0 and 100");

        if (config.Limits.MinFreeMemoryMb < 0)
            throw new ArgumentException("MinFreeMemoryMb cannot be negative");

        if (config.Limits.MaxLoadAverage <= 0)
            throw new ArgumentException("MaxLoadAverage must be positive");
    }

    // ========================================
    // DEFAULT CONFIGURATION
    // ========================================

    private SchedulingConfig CreateDefaultConfig()
    {
        return new SchedulingConfig
        {
            Id = "default",
            Version = CurrentConfigVersion,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UpdatedBy = "system",
            BaselineBenchmark = 1000,
            MaxPerformanceMultiplier = 20.0,
            Tiers = new Dictionary<QualityTier, TierConfiguration>
            {
                [QualityTier.Burstable] = new TierConfiguration
                {
                    MinimumBenchmark = 1000,
                    CpuOvercommitRatio = 4.0,
                    StorageOvercommitRatio = 2.5,
                    PriceMultiplier = 0.4m,
                    Description = "Best-effort performance, entry-level hardware, 4:1 CPU overcommit",
                    TargetUseCase = "Development, testing, light workloads"
                },
                [QualityTier.Balanced] = new TierConfiguration
                {
                    MinimumBenchmark = 1500,
                    CpuOvercommitRatio = 2.7,
                    StorageOvercommitRatio = 2.0,
                    PriceMultiplier = 0.6m,
                    Description = "Balanced performance for production workloads, 2.7:1 CPU overcommit",
                    TargetUseCase = "Web servers, databases, AI inference"
                },
                [QualityTier.Standard] = new TierConfiguration
                {
                    MinimumBenchmark = 2500,
                    CpuOvercommitRatio = 1.6,
                    StorageOvercommitRatio = 1.5,
                    PriceMultiplier = 1.0m,
                    Description = "High performance for demanding applications, 1.6:1 CPU overcommit",
                    TargetUseCase = "High-traffic apps, real-time processing, model training"
                },
                [QualityTier.Guaranteed] = new TierConfiguration
                {
                    MinimumBenchmark = 4000,
                    CpuOvercommitRatio = 1.0,
                    StorageOvercommitRatio = 1.0,
                    PriceMultiplier = 2.5m,
                    Description = "Dedicated high-performance resources, guaranteed 1:1 CPU performance",
                    TargetUseCase = "Mission-critical apps, large models, financial trading"
                }
            },
            Limits = new SchedulingLimits
            {
                MaxUtilizationPercent = 100.0,
                MinFreeMemoryMb = 256,
                MaxLoadAverage = 8.0,
                PreferLocalRegion = true
            },
            Weights = new ScoringWeightsConfig
            {
                Capacity = 0.40,
                Load = 0.25,
                Reputation = 0.20,
                Locality = 0.15
            }
        };
    }

    /// <summary>
    /// Bring a stored config up to <see cref="CurrentConfigVersion"/>, persisting
    /// the result. Runs on load, so a running deployment converges without an
    /// admin action.
    /// </summary>
    private async Task<SchedulingConfig> MigrateConfigAsync(SchedulingConfig config, CancellationToken ct)
    {
        var from = config.Version;

        // ── v1 → v2 ──────────────────────────────────────────────────────────
        // Tier PriceMultipliers here (1.8 / 1.0 / 0.7 / 0.5) disagreed with the
        // multipliers HourlyRateCalculator actually billed (2.5 / 1.0 / 0.6 / 0.4).
        // Two sources of truth for the same quantity: this document fed
        // TierCapability.PriceMultiplier — what the platform ADVERTISES through the
        // node capability model — while billing used its own hardcoded switch.
        //
        // Aligned to the BILLED values, deliberately: those are what users have
        // been charged and what the deploy UI displays, so no bill changes. The
        // advertisement corrects itself instead. Adopting the stored values would
        // have repriced the Guaranteed tier by -28% retroactively — a product
        // decision, not a bug fix.
        if (config.Version < 2)
        {
            foreach (var (tier, multiplier) in BilledPriceMultipliers)
            {
                if (config.Tiers.TryGetValue(tier, out var tierConfig))
                    tierConfig.PriceMultiplier = multiplier;
            }
        }

        config.Version = CurrentConfigVersion;
        config.UpdatedAt = DateTime.UtcNow;
        config.UpdatedBy = "migration";

        if (_useMongoDB && _configs != null)
        {
            await _configs.ReplaceOneAsync(c => c.Id == config.Id, config, cancellationToken: ct);
        }

        _logger.LogInformation(
            "Migrated scheduling configuration v{From} → v{To}", from, config.Version);

        return config;
    }

    private SchedulingConfig CloneForHistory(SchedulingConfig config)
    {
        // Simple clone for history (could use serialization for deep clone)
        return new SchedulingConfig
        {
            Version = config.Version,
            CreatedAt = config.CreatedAt,
            UpdatedAt = config.UpdatedAt,
            UpdatedBy = config.UpdatedBy,
            BaselineBenchmark = config.BaselineBenchmark,
            MaxPerformanceMultiplier = config.MaxPerformanceMultiplier,
            Tiers = new Dictionary<QualityTier, TierConfiguration>(config.Tiers),
            Limits = config.Limits,
            Weights = config.Weights
        };
    }
}