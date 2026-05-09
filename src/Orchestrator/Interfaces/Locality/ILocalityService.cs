namespace Orchestrator.Services.Locality;

/// <summary>
/// Locality lookup and validation service. Singleton; loads the four JSON
/// resource files once at construction and serves immutable in-memory views.
///
/// See <c>docs/LOCALITY.md</c> for the standard this service implements.
/// </summary>
public interface ILocalityService
{
    // ─── Read views ───────────────────────────────────────────────────────

    /// <summary>All known countries from <c>countries.json</c>.</summary>
    IReadOnlyList<CountryEntry> Countries { get; }

    /// <summary>All known regions from <c>regions.json</c>.</summary>
    IReadOnlyList<RegionEntry> Regions { get; }

    // ─── Validation ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if <paramref name="code"/> is a valid uppercase ISO
    /// 3166-1 alpha-2 code present in <c>countries.json</c>.
    /// The literal value <c>"ZZ"</c> is accepted as "unknown" and returns true.
    /// </summary>
    bool IsValidCountryCode(string? code);

    /// <summary>
    /// Returns true if <paramref name="code"/> is a known region from
    /// <c>regions.json</c>. The literal value <c>"unknown"</c> is accepted
    /// as a legacy sentinel and returns true.
    /// </summary>
    bool IsValidRegionCode(string? code);

    /// <summary>
    /// Returns true if <paramref name="zone"/> matches the format
    /// <c>&lt;region&gt;-&lt;n&gt;</c> where <c>&lt;region&gt;</c> equals
    /// <paramref name="parentRegion"/> and <c>&lt;n&gt;</c> is a positive
    /// integer. Null/empty <paramref name="zone"/> returns true (zone is
    /// optional). The literal <c>"default"</c> is accepted as legacy.
    /// </summary>
    bool IsValidZone(string? zone, string parentRegion);

    // ─── Derivation ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the supranational tags for the given country code, or an
    /// empty list if the country is unknown or has no tracked memberships.
    /// </summary>
    IReadOnlyList<string> GetTagsForCountry(string? countryCode);

    /// <summary>
    /// Returns the suggested default region for the given country code,
    /// or null if the country is unknown.
    /// </summary>
    string? GetDefaultRegionForCountry(string? countryCode);

    /// <summary>
    /// Returns the adjacency neighbours of the given region, or an empty
    /// list if the region is unknown. Used by the locality scorer.
    /// </summary>
    IReadOnlyList<string> GetAdjacentRegions(string region);

    /// <summary>
    /// Returns the continent code (NA, SA, EU, ME, AF, AS, OC) for the
    /// given region, or null if the region is unknown. Used by the
    /// locality scorer for the "same continent" tier.
    /// </summary>
    string? GetContinentForRegion(string region);
}

/// <summary>Entry in <c>countries.json</c>.</summary>
public record CountryEntry(
    string Code,
    string Name,
    IReadOnlyList<string> Tags);

/// <summary>Entry in <c>regions.json</c>.</summary>
public record RegionEntry(
    string Code,
    string Name,
    string Continent);
