using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly DataStore _dataStore;
    private readonly ILogger<SystemController> _logger;

    public SystemController(DataStore dataStore, ILogger<SystemController> logger)
    {
        _dataStore = dataStore;
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
                ["nodes"] = _dataStore.Nodes.Count,
                ["vms"] = _dataStore.VirtualMachines.Count,
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
    /// Calculate price for custom configuration
    /// </summary>
    [HttpPost("pricing/calculate")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<PriceCalculation>> CalculatePrice([FromBody] VmSpec spec)
    {
        // Simple pricing model
        decimal cpuRate = 0.005m * spec.CpuCores;
        decimal memoryRate = 0.002m * (spec.MemoryMb / 1024m);
        decimal storageRate = 0.0001m * spec.DiskGb;
        decimal gpuRate = spec.RequiresGpu ? 0.10m : 0;

        var hourlyTotal = cpuRate + memoryRate + storageRate + gpuRate;
        var dailyTotal = hourlyTotal * 24;
        var monthlyTotal = dailyTotal * 30;

        var calculation = new PriceCalculation(
            cpuRate,
            memoryRate,
            storageRate,
            gpuRate,
            hourlyTotal,
            dailyTotal,
            monthlyTotal,
            "USDC"
        );

        return Ok(ApiResponse<PriceCalculation>.Ok(calculation));
    }

    /// <summary>
    /// Get available regions
    /// </summary>
    [HttpGet("regions")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<List<RegionInfo>>> GetRegions()
    {
        // Aggregate regions from nodes
        var regions = _dataStore.Nodes.Values
            .GroupBy(n => n.Region)
            .Select(g => new RegionInfo(
                g.Key,
                g.Key.ToUpper().Replace("-", " "),
                g.Count(),
                g.Count(n => n.Status == NodeStatus.Online),
                g.Where(n => n.Status == NodeStatus.Online).Sum(n => n.AvailableResources.CpuCores),
                g.Where(n => n.Status == NodeStatus.Online).Sum(n => n.AvailableResources.MemoryMb)
            ))
            .ToList();

        // Add default if no nodes
        if (!regions.Any())
        {
            regions.Add(new RegionInfo("default", "Default", 0, 0, 0, 0));
        }

        return Ok(ApiResponse<List<RegionInfo>>.Ok(regions));
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

public record RegionInfo(
    string Id,
    string Name,
    int TotalNodes,
    int OnlineNodes,
    long AvailableCpu,
    long AvailableMemoryMb
);
