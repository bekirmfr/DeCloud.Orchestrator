using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;
using Orchestrator.Services.Payment;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly DataStore _dataStore;
    private readonly PricingConfig _pricingConfig;
    private readonly ILogger<SystemController> _logger;

    public SystemController(
        DataStore dataStore,
        IOptions<PricingConfig> pricingConfig,
        ILogger<SystemController> logger)
    {
        _dataStore = dataStore;
        _pricingConfig = pricingConfig.Value;
        _logger = logger;
    }

    /// <summary>
    /// Health check endpoint
    /// </summary>
    [HttpGet("health")]
    [AllowAnonymous]
    public ActionResult<HealthStatus> Health()
    {
        var components = new Dictionary<string, ComponentHealth>
        {
            ["datastore"] = new ComponentHealth("healthy", null, new Dictionary<string, object>
            {
                ["nodes"] = _dataStore.GetActiveNodes().Count(),
                ["vms"] = _dataStore.GetActiveVMs().Count(),
                ["users"] = _dataStore.Users.Count
            }),
            ["scheduler"] = new ComponentHealth("healthy", null, null)
        };

        return Ok(new HealthStatus("healthy", DateTime.UtcNow, components));
    }

    /// <summary>
    /// Get system statistics
    /// </summary>
    [HttpGet("stats")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SystemStats>>> GetStats()
    {
        var stats = await _dataStore.GetSystemStatsAsync();
        return Ok(ApiResponse<SystemStats>.Ok(stats));
    }

    /// <summary>
    /// Get available VM images
    /// </summary>
    [HttpGet("images")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<List<VmImage>>> GetImages()
    {
        var images = _dataStore.Images.Values
            .Where(i => i.IsPublic)
            .OrderBy(i => i.OsFamily)
            .ThenBy(i => i.Name)
            .ToList();

        return Ok(ApiResponse<List<VmImage>>.Ok(images));
    }

    /// <summary>
    /// Get a specific image
    /// </summary>
    [HttpGet("images/{imageId}")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<VmImage>> GetImage(string imageId)
    {
        if (!_dataStore.Images.TryGetValue(imageId, out var image))
        {
            return NotFound(ApiResponse<VmImage>.Fail("NOT_FOUND", "Image not found"));
        }

        return Ok(ApiResponse<VmImage>.Ok(image));
    }

    /// <summary>
    /// Get pricing tiers
    /// </summary>
    [HttpGet("pricing")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<List<VmPricingTier>>> GetPricing()
    {
        var tiers = _dataStore.PricingTiers.Values
            .OrderBy(t => t.HourlyPriceUsd)
            .ToList();

        return Ok(ApiResponse<List<VmPricingTier>>.Ok(tiers));
    }

    /// <summary>
    /// Calculate estimated price for a VM configuration at platform default rates.
    /// Reflects the rates a tenant would see on a node with no operator-set
    /// pricing. Real billing applies the host node's effective rates, which may
    /// be higher (but never below the platform floor).
    ///
    /// The formula mirrors <c>VmService.CalculateHourlyRate</c> but excludes
    /// the quality-tier multiplier, bandwidth tier surcharge, and storage
    /// replication cost — those depend on data not present in a stand-alone
    /// VmSpec estimate.
    /// </summary>
    [HttpPost("pricing/calculate")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<PriceCalculation>> CalculatePrice([FromBody] VmSpec spec)
    {
        const decimal BYTES_PER_GB = 1024m * 1024m * 1024m;

        // Resolve platform default effective rates. Passing null for the operator
        // pricing means: "use platform defaults for every field, clamped to floor."
        var rates = PricingResolver.Resolve(raw: null, cfg: _pricingConfig);

        var memoryGb = spec.MemoryBytes / BYTES_PER_GB;
        var diskGb = spec.DiskBytes / BYTES_PER_GB;
        var gpuVramGb = (spec.GpuVramBytes ?? 0) / BYTES_PER_GB;

        var cpuCost = spec.VirtualCpuCores * rates.CpuPerHour;
        var memoryCost = memoryGb * rates.MemoryPerGbPerHour;
        var storageCost = diskGb * rates.StoragePerGbPerHour;
        var gpuCost = gpuVramGb * rates.GpuVramPerGbPerHour;

        var hourlyTotal = cpuCost + memoryCost + storageCost + gpuCost;
        var dailyTotal = hourlyTotal * 24m;
        var monthlyTotal = dailyTotal * 30m;

        var calculation = new PriceCalculation(
            cpuCost,
            memoryCost,
            storageCost,
            gpuCost,
            hourlyTotal,
            dailyTotal,
            monthlyTotal,
            rates.Currency
        );

        return Ok(ApiResponse<PriceCalculation>.Ok(calculation));
    }

    /// <summary>
    /// Get recent events (for debugging/monitoring)
    /// </summary>
    [HttpGet("events")]
    [Authorize(Roles = "admin")]
    public ActionResult<ApiResponse<List<OrchestratorEvent>>> GetEvents(
        [FromQuery] int limit = 100,
        [FromQuery] string? type = null)
    {
        var events = _dataStore.EventHistory
            .Reverse()
            .Take(limit);

        if (!string.IsNullOrEmpty(type) && Enum.TryParse<EventType>(type, true, out var eventType))
        {
            events = events.Where(e => e.Type == eventType);
        }

        return Ok(ApiResponse<List<OrchestratorEvent>>.Ok(events.ToList()));
    }

    // Then in controller:
    /// <summary>
    /// Get pending command acknowledgments (for monitoring/debugging)
    /// </summary>
    [HttpGet("commands/pending")]
    public ActionResult<ApiResponse<List<PendingCommandDto>>> GetPendingCommands()
    {
        var pending = _dataStore.GetPendingAcks()
            .Select(cmd => new PendingCommandDto(
                cmd.CommandId,
                cmd.Type.ToString(),
                cmd.TargetResourceId,
                cmd.QueuedAt,
                cmd.Age.TotalSeconds,
                cmd.IsExpired,
                cmd.ExpiresAt
            ))
            .OrderByDescending(x => x.AgeSeconds)
            .ToList();

        return Ok(ApiResponse<List<PendingCommandDto>>.Ok(pending));
    }
}

public record PriceCalculation(
    decimal CpuCost,
    decimal MemoryCost,
    decimal StorageCost,
    decimal GpuCost,
    decimal HourlyTotal,
    decimal DailyTotal,
    decimal MonthlyTotal,
    string Currency
);
