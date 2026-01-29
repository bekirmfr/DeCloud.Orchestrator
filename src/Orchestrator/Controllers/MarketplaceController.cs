using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/marketplace")]
public class MarketplaceController : ControllerBase
{
    private readonly INodeMarketplaceService _marketplaceService;
    private readonly ILogger<MarketplaceController> _logger;

    public MarketplaceController(
        INodeMarketplaceService marketplaceService,
        ILogger<MarketplaceController> logger)
    {
        _marketplaceService = marketplaceService;
        _logger = logger;
    }

    /// <summary>
    /// Search nodes in the marketplace
    /// </summary>
    /// <remarks>
    /// Example queries:
    /// - Find GPU nodes: ?requiresGpu=true
    /// - Find cheap nodes: ?maxPricePerPoint=0.005
    /// - Find EU nodes with NVMe: ?region=eu-west&amp;tags=nvme
    /// - Find high-uptime nodes: ?minUptimePercent=99
    /// </remarks>
    [HttpGet("nodes")]
    [AllowAnonymous]
    public async Task<ActionResult<List<NodeAdvertisement>>> SearchNodes(
        [FromQuery] string? tags,
        [FromQuery] string? region,
        [FromQuery] string? jurisdiction,
        [FromQuery] decimal? maxPricePerPoint,
        [FromQuery] double? minUptimePercent,
        [FromQuery] bool? requiresGpu,
        [FromQuery] int? minAvailableComputePoints,
        [FromQuery] bool onlineOnly = true,
        [FromQuery] string? sortBy = "uptime",
        [FromQuery] bool sortDescending = true)
    {
        var criteria = new NodeSearchCriteria
        {
            Tags = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Region = region,
            Jurisdiction = jurisdiction,
            MaxPricePerPoint = maxPricePerPoint,
            MinUptimePercent = minUptimePercent,
            RequiresGpu = requiresGpu,
            MinAvailableComputePoints = minAvailableComputePoints,
            OnlineOnly = onlineOnly,
            SortBy = sortBy,
            SortDescending = sortDescending
        };

        var nodes = await _marketplaceService.SearchNodesAsync(criteria);

        _logger.LogInformation(
            "Marketplace search returned {Count} nodes (tags={Tags}, gpu={Gpu}, region={Region})",
            nodes.Count, tags ?? "none", requiresGpu, region ?? "any");

        return Ok(nodes);
    }

    /// <summary>
    /// Get featured/recommended nodes
    /// </summary>
    /// <remarks>
    /// Returns top 10 nodes with:
    /// - High uptime (>95%)
    /// - Available capacity
    /// - Currently online
    /// - Operator-provided description
    /// </remarks>
    [HttpGet("nodes/featured")]
    [AllowAnonymous]
    public async Task<ActionResult<List<NodeAdvertisement>>> GetFeaturedNodes()
    {
        var featuredNodes = await _marketplaceService.GetFeaturedNodesAsync();

        _logger.LogInformation("Returning {Count} featured nodes", featuredNodes.Count);

        return Ok(featuredNodes);
    }

    /// <summary>
    /// Get detailed advertisement for a specific node
    /// </summary>
    [HttpGet("nodes/{nodeId}")]
    [AllowAnonymous]
    public async Task<ActionResult<NodeAdvertisement>> GetNode(string nodeId)
    {
        var advertisement = await _marketplaceService.GetNodeAdvertisementAsync(nodeId);

        if (advertisement == null)
        {
            return NotFound(new { Error = "Node not found" });
        }

        return Ok(advertisement);
    }

    /// <summary>
    /// Update node profile (operator only)
    /// </summary>
    /// <remarks>
    /// Allows node operators to update their marketplace profile:
    /// - Friendly name
    /// - Description
    /// - Tags for discovery
    /// - Pricing
    /// 
    /// Requires node authentication (X-Node-Token header)
    /// </remarks>
    [HttpPatch("nodes/{nodeId}/profile")]
    [Authorize(AuthenticationSchemes = "ApiKey")]
    public async Task<IActionResult> UpdateNodeProfile(
        string nodeId,
        [FromBody] NodeProfileUpdate update)
    {
        // Verify the authenticated node matches the nodeId
        var authenticatedNodeId = User.FindFirst("node_id")?.Value;
        if (authenticatedNodeId != nodeId)
        {
            return Forbid("You can only update your own node profile");
        }

        var success = await _marketplaceService.UpdateNodeProfileAsync(nodeId, update);

        if (!success)
        {
            return NotFound(new { Error = "Node not found" });
        }

        _logger.LogInformation(
            "Node {NodeId} updated profile: name={Name}, tags={Tags}",
            nodeId, update.Name, update.Tags != null ? string.Join(",", update.Tags) : "unchanged");

        return Ok(new { Success = true, Message = "Profile updated successfully" });
    }
}
