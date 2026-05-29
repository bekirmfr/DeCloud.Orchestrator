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
    /// Calculate the estimated hourly cost for a VM configuration at platform
    /// default rates. Reflects what a tenant would be billed on a node with no
    /// operator-set pricing — real billing on a specific node may be higher
    /// (operator-set rates above the platform default) but never below the
    /// platform floor.
    ///
    /// Delegates to <see cref="HourlyRateCalculator"/>, which is also the
    /// formula used by <see cref="VmServiceModel"/> at scheduling time. Passing
    /// <c>nodePricing = null</c> yields the platform-default version.
    /// </summary>
    [HttpPost("pricing/calculate")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<PriceCalculation>> CalculatePrice([FromBody] VmSpec spec)
    {
        var breakdown = HourlyRateCalculator.Calculate(
            spec, nodePricing: null, cfg: _pricingConfig);

        var calculation = new PriceCalculation(
            CpuCost: breakdown.CpuCost,
            MemoryCost: breakdown.MemoryCost,
            StorageCost: breakdown.StorageCost,
            GpuCost: breakdown.GpuCost,
            BandwidthCost: breakdown.BandwidthCost,
            ReplicationCost: breakdown.ReplicationCost,
            HourlyTotal: breakdown.Total,
            DailyTotal: breakdown.Total * 24m,
            MonthlyTotal: breakdown.Total * 24m * 30m,
            Currency: breakdown.Currency
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

/// <summary>
/// Per-hour cost breakdown returned by /api/system/pricing/calculate.
/// Mirrors the line items in HourlyRateBreakdown that matter to tenants
/// — TierMultiplier is intentionally excluded; the post-multiplier compute
/// costs already reflect its effect.
///
/// CpuCost + MemoryCost + StorageCost + BandwidthCost + GpuCost +
/// ReplicationCost == HourlyTotal.
/// </summary>
public record PriceCalculation(
    decimal CpuCost,
    decimal MemoryCost,
    decimal StorageCost,
    decimal GpuCost,
    decimal BandwidthCost,
    decimal ReplicationCost,
    decimal HourlyTotal,
    decimal DailyTotal,
    decimal MonthlyTotal,
    string Currency
);
