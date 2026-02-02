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
    [ProducesResponseType(typeof(SchedulingConfig), 200)]
    public async Task<ActionResult<SchedulingConfig>> GetConfig()
    {
        try
        {
            var config = await _configService.GetConfigAsync();
            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scheduling configuration");
            return StatusCode(500, new { error = "Failed to retrieve configuration" });
        }
    }

    /// <summary>
    /// Update scheduling configuration
    /// </summary>
    /// <param name="config">New configuration to apply</param>
    /// <returns>Updated configuration</returns>
    [HttpPut]
    [ProducesResponseType(typeof(SchedulingConfig), 200)]
    [ProducesResponseType(typeof(ErrorResponse), 400)]
    public async Task<ActionResult<SchedulingConfig>> UpdateConfig(
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

            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid configuration update attempt");
            return BadRequest(new ErrorResponse
            {
                Error = "Validation failed",
                Message = ex.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update scheduling configuration");
            return StatusCode(500, new ErrorResponse
            {
                Error = "Update failed",
                Message = "An error occurred while updating configuration"
            });
        }
    }

    /// <summary>
    /// Reload configuration from database (clears cache)
    /// </summary>
    /// <returns>Success message</returns>
    [HttpPost("reload")]
    [ProducesResponseType(typeof(SuccessResponse), 200)]
    public async Task<ActionResult<SuccessResponse>> ReloadConfig()
    {
        try
        {
            await _configService.ReloadConfigAsync();

            _logger.LogInformation("Configuration cache cleared and reloaded");

            return Ok(new SuccessResponse
            {
                Message = "Configuration reloaded successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload configuration");
            return StatusCode(500, new ErrorResponse
            {
                Error = "Reload failed",
                Message = "An error occurred while reloading configuration"
            });
        }
    }

    /// <summary>
    /// Get configuration history (for audit trail)
    /// </summary>
    /// <param name="limit">Number of historical versions to return (default: 10)</param>
    /// <returns>List of historical configurations</returns>
    [HttpGet("history")]
    [ProducesResponseType(typeof(List<SchedulingConfig>), 200)]
    public async Task<ActionResult<List<SchedulingConfig>>> GetConfigHistory(
        [FromQuery] int limit = 10)
    {
        try
        {
            if (limit < 1 || limit > 100)
                return BadRequest(new ErrorResponse
                {
                    Error = "Invalid limit",
                    Message = "Limit must be between 1 and 100"
                });

            var history = await _configService.GetConfigHistoryAsync(limit);
            return Ok(history);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get configuration history");
            return StatusCode(500, new ErrorResponse
            {
                Error = "History retrieval failed",
                Message = "An error occurred while retrieving configuration history"
            });
        }
    }

    /// <summary>
    /// Validate a configuration without saving it
    /// Useful for testing configuration changes before applying
    /// </summary>
    /// <param name="config">Configuration to validate</param>
    /// <returns>Validation result</returns>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(ValidationResult), 200)]
    [ProducesResponseType(typeof(ValidationResult), 400)]
    public ActionResult<ValidationResult> ValidateConfig(
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
                return BadRequest(new ValidationResult
                {
                    IsValid = false,
                    Errors = errors
                });
            }

            return Ok(new ValidationResult
            {
                IsValid = true,
                Message = "Configuration is valid"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during configuration validation");
            return BadRequest(new ValidationResult
            {
                IsValid = false,
                Errors = new List<string> { ex.Message }
            });
        }
    }
}

/// <summary>
/// Standard error response
/// </summary>
public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Standard success response
/// </summary>
public class SuccessResponse
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Validation result response
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = new();
}