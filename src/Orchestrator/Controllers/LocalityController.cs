using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Services.Locality;

namespace Orchestrator.Controllers;

/// <summary>
/// Public read-only endpoints serving locality reference data.
///
/// <para>
/// Used by <c>install.sh</c> at install time (country list, region list,
/// region suggestion from country) and by the marketplace UI (filter
/// dropdowns, jurisdiction tag filtering).
/// </para>
///
/// <para>
/// Anonymous because the data is non-sensitive reference material —
/// requiring auth would block install.sh's first call. Rate limiting
/// applied at the gateway layer.
/// </para>
///
/// <para>See <c>docs/LOCALITY.md</c> for the standard.</para>
/// </summary>
[ApiController]
[Route("api/locality")]
[AllowAnonymous]
public class LocalityController : ControllerBase
{
    private readonly ILocalityService _locality;
    private readonly ILogger<LocalityController> _logger;

    public LocalityController(
        ILocalityService locality,
        ILogger<LocalityController> logger)
    {
        _locality = locality;
        _logger = logger;
    }

    /// <summary>
    /// Returns the full country list with supranational tags.
    /// Source: <c>countries.json</c>.
    /// </summary>
    [HttpGet("countries")]
    public ActionResult<ApiResponse<IReadOnlyList<CountryEntry>>> GetCountries()
    {
        return Ok(ApiResponse<IReadOnlyList<CountryEntry>>.Ok(_locality.Countries));
    }

    /// <summary>
    /// Returns the full region list.
    /// Source: <c>regions.json</c>.
    /// </summary>
    [HttpGet("regions")]
    public ActionResult<ApiResponse<IReadOnlyList<RegionEntry>>> GetRegions()
    {
        return Ok(ApiResponse<IReadOnlyList<RegionEntry>>.Ok(_locality.Regions));
    }

    /// <summary>
    /// Returns the suggested default region for the given country code.
    /// Used by install.sh to pre-fill the region prompt after detecting
    /// the operator's country from IP.
    ///
    /// <para>Returns 404 with a helpful message when the country code is
    /// unknown — install.sh treats this as "no suggestion" and prompts
    /// the operator without a default.</para>
    /// </summary>
    [HttpGet("suggest/{country}")]
    public ActionResult<ApiResponse<SuggestedRegion>> Suggest(string country)
    {
        // Normalize: install.sh may send lowercase from raw IP-derived input.
        var normalized = country?.ToUpperInvariant() ?? string.Empty;

        var region = _locality.GetDefaultRegionForCountry(normalized);
        if (region is null)
        {
            return NotFound(ApiResponse<SuggestedRegion>.Fail(
                "UNKNOWN_COUNTRY",
                $"No region suggestion available for country '{country}'. " +
                $"Either the country code is invalid or it has no mapping in " +
                $"country-region-defaults.json."));
        }

        return Ok(ApiResponse<SuggestedRegion>.Ok(
            new SuggestedRegion(normalized, region)));
    }
}

/// <summary>Response for the <c>/suggest/{country}</c> endpoint.</summary>
public record SuggestedRegion(string Country, string Region);
