using DeCloud.Shared.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Services.VmScheduling;

namespace Orchestrator.Controllers;

/// <summary>
/// Admin API for managing scheduling configuration
/// </summary>
[ApiController]
[Route("api/admin/scheduling-config")]
[Authorize(Roles = "Admin")]
public class SchedulingConfigController : ControllerBase
{
    private readonly ISchedulingConfigService _configService;
    private readonly ILogger<SchedulingConfigController> _logger;

    public SchedulingConfigController(
        ISchedulingConfigService configService,
        ILogger<SchedulingConfigController> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Get current scheduling configuration
    /// </summary>
    /// <returns>Current scheduling configuration</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<SchedulingConfig>), 200)]
    public async Task<ActionResult<ApiResponse<SchedulingConfig>>> GetConfig()
    {
        try
        {
            var config = await _configService.GetConfigAsync();
            return Ok(ApiResponse<SchedulingConfig>.Ok(config));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scheduling configuration");
            return StatusCode(500, ApiResponse<SchedulingConfig>.Fail("INTERNAL_ERROR", "Failed to retrieve configuration"));
        }
    }

    /// <summary>
    /// Update scheduling configuration
    /// </summary>
    /// <param name="config">New configuration to apply</param>
    /// <returns>Updated configuration</returns>
    [HttpPut]
    [ProducesResponseType(typeof(ApiResponse<SchedulingConfig>), 200)]
    [ProducesResponseType(typeof(ApiResponse<SchedulingConfig>), 400)]
    public async Task<ActionResult<ApiResponse<SchedulingConfig>>> UpdateConfig(
        [FromBody] SchedulingConfig config)
    {
        try
        {
            // Get updater identity (in production, get from authenticated user)
            var updatedBy = User.Identity?.Name ?? "admin";

            var updated = await _configService.UpdateConfigAsync(config, updatedBy);

            _logger.LogWarning(
                "Scheduling configuration updated to v{Version} by {User}",
                updated.Version, updatedBy);

            return Ok(ApiResponse<SchedulingConfig>.Ok(updated));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid configuration update attempt");
            return BadRequest(ApiResponse<SchedulingConfig>.Fail("VALIDATION_FAILED", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update scheduling configuration");
            return StatusCode(500, ApiResponse<SchedulingConfig>.Fail("INTERNAL_ERROR", "An error occurred while updating configuration"));
        }
    }

    /// <summary>
    /// Reload configuration from database (clears cache)
    /// </summary>
    /// <returns>Success message</returns>
    [HttpPost("reload")]
    [ProducesResponseType(typeof(ApiResponse<bool>), 200)]
    public async Task<ActionResult<ApiResponse<bool>>> ReloadConfig()
    {
        try
        {
            await _configService.ReloadConfigAsync();

            _logger.LogInformation("Configuration cache cleared and reloaded");

            return Ok(ApiResponse<bool>.Ok(true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload configuration");
            return StatusCode(500, ApiResponse<bool>.Fail("INTERNAL_ERROR", "An error occurred while reloading configuration"));
        }
    }

    /// <summary>
    /// Get configuration history (for audit trail)
    /// </summary>
    /// <param name="limit">Number of historical versions to return (default: 10)</param>
    /// <returns>List of historical configurations</returns>
    [HttpGet("history")]
    [ProducesResponseType(typeof(ApiResponse<List<SchedulingConfig>>), 200)]
    public async Task<ActionResult<ApiResponse<List<SchedulingConfig>>>> GetConfigHistory(
        [FromQuery] int limit = 10)
    {
        try
        {
            if (limit < 1 || limit > 100)
                return BadRequest(ApiResponse<List<SchedulingConfig>>.Fail("INVALID_LIMIT", "Limit must be between 1 and 100"));

            var history = await _configService.GetConfigHistoryAsync(limit);
            return Ok(ApiResponse<List<SchedulingConfig>>.Ok(history));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get configuration history");
            return StatusCode(500, ApiResponse<List<SchedulingConfig>>.Fail("INTERNAL_ERROR", "An error occurred while retrieving configuration history"));
        }
    }

    /// <summary>
    /// Validate a configuration without saving it
    /// Useful for testing configuration changes before applying
    /// </summary>
    /// <param name="config">Configuration to validate</param>
    /// <returns>Validation result</returns>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(ApiResponse<bool>), 200)]
    [ProducesResponseType(typeof(ApiResponse<bool>), 400)]
    public ActionResult<ApiResponse<bool>> ValidateConfig(
        [FromBody] SchedulingConfig config)
    {
        try
        {
            // Validation happens in the service
            // We'll try to create a temporary service instance just for validation
            var errors = new List<string>();

            // Basic validation
            if (config.BaselineBenchmark <= 0)
                errors.Add("BaselineBenchmark must be positive");

            if (config.MaxPerformanceMultiplier <= 0)
                errors.Add("MaxPerformanceMultiplier must be positive");

            if (config.Tiers == null || config.Tiers.Count == 0)
                errors.Add("At least one tier must be configured");

            if (config.Tiers != null && !config.Tiers.ContainsKey(QualityTier.Burstable))
                errors.Add("Burstable tier must be configured");

            if (!config.Weights.IsValid(out var weightsError))
                errors.Add($"Invalid scoring weights: {weightsError}");

            // Validate each tier
            if (config.Tiers != null)
            {
                foreach (var (tier, tierConfig) in config.Tiers)
                {
                    if (tierConfig.MinimumBenchmark <= 0)
                        errors.Add($"Tier {tier} has invalid minimum benchmark");

                    if (tierConfig.CpuOvercommitRatio <= 0)
                        errors.Add($"Tier {tier} has invalid CPU overcommit ratio");

                    if (tierConfig.StorageOvercommitRatio <= 0)
                        errors.Add($"Tier {tier} has invalid storage overcommit ratio");

                    if (tierConfig.PriceMultiplier < 0)
                        errors.Add($"Tier {tier} has invalid price multiplier");
                }
            }

            if (errors.Any())
            {
                return BadRequest(ApiResponse<bool>.Fail("VALIDATION_FAILED", "Configuration validation failed", new Dictionary<string, object> { ["errors"] = errors }));
            }

            return Ok(ApiResponse<bool>.Ok(true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during configuration validation");
            return BadRequest(ApiResponse<bool>.Fail("VALIDATION_FAILED", "Configuration validation failed", new Dictionary<string, object> { ["errors"] = new List<string> { ex.Message } }));
        }
    }
}