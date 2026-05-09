using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orchestrator.Services.Locality;

/// <summary>
/// Default <see cref="ILocalityService"/> implementation.
///
/// <para>
/// Loads the four locality reference files as embedded resources at
/// construction:
/// <list type="bullet">
///   <item><c>countries.json</c></item>
///   <item><c>regions.json</c></item>
///   <item><c>region-adjacency.json</c></item>
///   <item><c>country-region-defaults.json</c></item>
/// </list>
/// The .csproj declares each as <c>&lt;EmbeddedResource&gt;</c> with a
/// <c>LogicalName</c> equal to the file's basename — same pattern used for
/// <c>DeCloudEscrow.abi.json</c>.
/// </para>
///
/// <para>
/// Validates referential integrity at startup: every country has a default
/// region, every default region exists, every adjacency target exists,
/// and the adjacency graph is symmetric. Throws on inconsistency so a
/// broken resource set fails fast at orchestrator boot, not at request time.
/// </para>
/// </summary>
public sealed class LocalityService : ILocalityService
{
    // Sentinel values that bypass validation (legacy / unknown).
    private const string UnknownCountry = "ZZ";
    private const string UnknownRegion = "unknown";
    private const string LegacyDefaultZone = "default";

    private static readonly Regex CountryPattern = new(
        @"^[A-Z]{2}$", RegexOptions.Compiled);

    private static readonly Regex RegionPattern = new(
        @"^[a-z]+(-[a-z]+)*$", RegexOptions.Compiled);

    private static readonly Regex ZoneSuffixPattern = new(
        @"^[1-9][0-9]*$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, CountryEntry> _countriesByCode;
    private readonly Dictionary<string, RegionEntry> _regionsByCode;
    private readonly Dictionary<string, string> _defaultRegionByCountry;
    private readonly Dictionary<string, IReadOnlyList<string>> _adjacency;

    public IReadOnlyList<CountryEntry> Countries { get; }
    public IReadOnlyList<RegionEntry> Regions { get; }

    public LocalityService(ILogger<LocalityService> logger)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // ─── Load countries.json ─────────────────────────────────────────
        var countryEntries = LoadJsonArray<RawCountryEntry>(
            assembly, "countries.json")
            .Where(e => e.Code is not null)  // skip the comment block
            .Select(e => new CountryEntry(
                e.Code!, e.Name ?? e.Code!, e.Tags ?? new List<string>()))
            .ToList();

        _countriesByCode = countryEntries.ToDictionary(
            c => c.Code, StringComparer.OrdinalIgnoreCase);
        Countries = countryEntries;

        foreach (var c in Countries)
        {
            if (!CountryPattern.IsMatch(c.Code))
            {
                throw new InvalidOperationException(
                    $"countries.json: invalid country code '{c.Code}'. " +
                    $"Must match {CountryPattern}.");
            }
        }

        // ─── Load regions.json ───────────────────────────────────────────
        var regionEntries = LoadJsonArray<RawRegionEntry>(
            assembly, "regions.json")
            .Where(e => e.Code is not null)
            .Select(e => new RegionEntry(
                e.Code!, e.Name ?? e.Code!, e.Continent ?? "??"))
            .ToList();

        _regionsByCode = regionEntries.ToDictionary(
            r => r.Code, StringComparer.OrdinalIgnoreCase);
        Regions = regionEntries;

        foreach (var r in Regions)
        {
            if (!RegionPattern.IsMatch(r.Code))
            {
                throw new InvalidOperationException(
                    $"regions.json: invalid region code '{r.Code}'. " +
                    $"Must match {RegionPattern}.");
            }
        }

        // ─── Load country-region-defaults.json ───────────────────────────
        var rawDefaults = LoadJsonObject(assembly, "country-region-defaults.json");
        _defaultRegionByCountry = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in rawDefaults)
        {
            if (kvp.Key.StartsWith("_")) continue;  // skip _comment
            _defaultRegionByCountry[kvp.Key] = kvp.Value;
        }

        // Referential integrity: every country has a default; every default
        // points at a known region.
        var missingDefaults = _countriesByCode.Keys
            .Where(c => !_defaultRegionByCountry.ContainsKey(c))
            .ToList();
        if (missingDefaults.Count > 0)
        {
            throw new InvalidOperationException(
                $"country-region-defaults.json: missing default region for: " +
                $"[{string.Join(", ", missingDefaults)}]");
        }

        var unknownDefaultRegions = _defaultRegionByCountry.Values
            .Where(r => !_regionsByCode.ContainsKey(r))
            .Distinct()
            .ToList();
        if (unknownDefaultRegions.Count > 0)
        {
            throw new InvalidOperationException(
                $"country-region-defaults.json: references unknown regions: " +
                $"[{string.Join(", ", unknownDefaultRegions)}]");
        }

        // ─── Load region-adjacency.json ──────────────────────────────────
        var rawAdjacency = LoadJsonAdjacency(assembly, "region-adjacency.json");
        _adjacency = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (region, neighbours) in rawAdjacency)
        {
            if (region.StartsWith("_")) continue;  // skip _comment
            _adjacency[region] = neighbours;
        }

        // Referential integrity: keys and values must be known regions
        var unknownAdjKeys = _adjacency.Keys
            .Where(k => !_regionsByCode.ContainsKey(k))
            .ToList();
        if (unknownAdjKeys.Count > 0)
        {
            throw new InvalidOperationException(
                $"region-adjacency.json: unknown regions as keys: " +
                $"[{string.Join(", ", unknownAdjKeys)}]");
        }

        var unknownAdjValues = _adjacency.Values
            .SelectMany(v => v)
            .Where(v => !_regionsByCode.ContainsKey(v))
            .Distinct()
            .ToList();
        if (unknownAdjValues.Count > 0)
        {
            throw new InvalidOperationException(
                $"region-adjacency.json: unknown regions as neighbours: " +
                $"[{string.Join(", ", unknownAdjValues)}]");
        }

        // Symmetry check
        var asymmetric = new List<string>();
        foreach (var (region, neighbours) in _adjacency)
        {
            foreach (var n in neighbours)
            {
                if (!_adjacency.TryGetValue(n, out var reverse) ||
                    !reverse.Contains(region, StringComparer.OrdinalIgnoreCase))
                {
                    asymmetric.Add($"{region}->{n}");
                }
            }
        }
        if (asymmetric.Count > 0)
        {
            throw new InvalidOperationException(
                $"region-adjacency.json: asymmetric edges: " +
                $"[{string.Join(", ", asymmetric)}]");
        }

        logger.LogInformation(
            "Locality service initialized: {Countries} countries, " +
            "{Regions} regions, {Defaults} country-region defaults, " +
            "{Edges} adjacency edges (symmetric).",
            Countries.Count,
            Regions.Count,
            _defaultRegionByCountry.Count,
            _adjacency.Values.Sum(v => v.Count));
    }

    // ─── ILocalityService implementation ─────────────────────────────────

    public bool IsValidCountryCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        if (string.Equals(code, UnknownCountry, StringComparison.Ordinal)) return true;
        return _countriesByCode.ContainsKey(code);
    }

    public bool IsValidRegionCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        if (string.Equals(code, UnknownRegion, StringComparison.Ordinal)) return true;
        return _regionsByCode.ContainsKey(code);
    }

    public bool IsValidZone(string? zone, string parentRegion)
    {
        if (string.IsNullOrEmpty(zone)) return true;  // zone is optional

        // Legacy sentinel
        if (string.Equals(zone, LegacyDefaultZone, StringComparison.Ordinal))
            return true;

        // Format: <parentRegion>-<positive-integer>
        if (!zone.StartsWith(parentRegion + "-", StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = zone.Substring(parentRegion.Length + 1);
        return ZoneSuffixPattern.IsMatch(suffix);
    }

    public IReadOnlyList<string> GetTagsForCountry(string? countryCode)
    {
        if (string.IsNullOrEmpty(countryCode)) return Array.Empty<string>();
        return _countriesByCode.TryGetValue(countryCode, out var entry)
            ? entry.Tags
            : Array.Empty<string>();
    }

    public string? GetDefaultRegionForCountry(string? countryCode)
    {
        if (string.IsNullOrEmpty(countryCode)) return null;
        return _defaultRegionByCountry.TryGetValue(countryCode, out var region)
            ? region
            : null;
    }

    public IReadOnlyList<string> GetAdjacentRegions(string region)
    {
        return _adjacency.TryGetValue(region, out var neighbours)
            ? neighbours
            : Array.Empty<string>();
    }

    public string? GetContinentForRegion(string region)
    {
        return _regionsByCode.TryGetValue(region, out var entry)
            ? entry.Continent
            : null;
    }

    // ─── Embedded-resource loader helpers ────────────────────────────────

    private static List<T> LoadJsonArray<T>(Assembly assembly, string logicalName)
    {
        using var stream = OpenResource(assembly, logicalName);
        var doc = JsonDocument.Parse(stream);
        return doc.RootElement.EnumerateArray()
            .Select(e => e.Deserialize<T>(JsonOptions))
            .Where(e => e is not null)
            .Cast<T>()
            .ToList();
    }

    private static Dictionary<string, string> LoadJsonObject(
        Assembly assembly, string logicalName)
    {
        using var stream = OpenResource(assembly, logicalName);
        var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            // Comment blocks are arrays — skip.
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            result[prop.Name] = prop.Value.GetString()!;
        }
        return result;
    }

    private static Dictionary<string, IReadOnlyList<string>> LoadJsonAdjacency(
        Assembly assembly, string logicalName)
    {
        using var stream = OpenResource(assembly, logicalName);
        var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var neighbours = prop.Value.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => s is not null)
                .Cast<string>()
                .ToList();
            result[prop.Name] = neighbours;
        }
        return result;
    }

    private static Stream OpenResource(Assembly assembly, string logicalName)
    {
        return assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' not found. " +
                $"Ensure the .csproj declares it as <EmbeddedResource> with " +
                $"<LogicalName>{logicalName}</LogicalName>.");
    }

    // ─── Internal raw-JSON shapes (for deserialization only) ─────────────

    private sealed class RawCountryEntry
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public List<string>? Tags { get; set; }
    }

    private sealed class RawRegionEntry
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? Continent { get; set; }
    }
}
