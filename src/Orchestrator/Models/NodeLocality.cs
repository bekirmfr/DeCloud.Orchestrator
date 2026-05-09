namespace Orchestrator.Models;

/// <summary>
/// Three-axis location attributes for a node: country, region, zone.
/// See <c>docs/LOCALITY.md</c> for the full standard.
///
/// <para>
/// The three axes answer three different questions and have different
/// matching semantics:
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Country</term>
///     <description>Legal jurisdiction; ISO 3166-1 alpha-2; categorical.
///     Source of truth is the operator's declaration.</description>
///   </item>
///   <item>
///     <term>Region</term>
///     <description>Network locality; DeCloud-defined enumeration; similarity
///     scoring via adjacency graph.</description>
///   </item>
///   <item>
///     <term>Zone</term>
///     <description>Operator-scoped grouping within a region; categorical;
///     no failure-independence guarantee.</description>
///   </item>
/// </list>
/// </summary>
public class NodeLocality
{
    /// <summary>
    /// ISO 3166-1 alpha-2 country code, uppercase. Operator-declared.
    /// <c>"ZZ"</c> indicates unknown / declined / pre-locality-standard.
    /// Validated against <c>countries.json</c> at registration.
    /// </summary>
    public string Country { get; set; } = "ZZ";

    /// <summary>
    /// Supranational membership tags derived from <see cref="Country"/> at
    /// registration time using <c>countries.json</c>'s <c>tags</c> field.
    /// Examples: <c>["EU", "EEA", "Schengen", "NATO"]</c>.
    /// Recomputed on each registration; not directly mutable by node agents.
    /// </summary>
    public List<string> JurisdictionTags { get; set; } = new();

    /// <summary>
    /// DeCloud-defined region code from <c>regions.json</c>.
    /// <c>"unknown"</c> indicates pre-locality-standard.
    /// Validated against the regions list at registration.
    /// </summary>
    public string Region { get; set; } = "unknown";

    /// <summary>
    /// Operator-scoped zone, format <c>&lt;region&gt;-&lt;n&gt;</c>.
    /// Optional. Must start with the parent <see cref="Region"/> code if
    /// present. No failure-independence guarantee — zone is an organizational
    /// convenience, not a SLA primitive.
    /// </summary>
    public string? Zone { get; set; }

    /// <summary>
    /// Best-effort country code derived from the registration request's
    /// source IP using GeoIP lookup. Used for corroboration only; not
    /// authoritative. Null if GeoIP lookup unavailable or unsuccessful.
    /// </summary>
    public string? IpDerivedCountry { get; set; }

    /// <summary>
    /// True when <see cref="Country"/> and <see cref="IpDerivedCountry"/>
    /// disagree. Surfaced in marketplace listings so tenants paying a
    /// premium for jurisdiction can see when network location differs from
    /// declared location (VPN, leased datacenter, CGNAT).
    /// </summary>
    public bool LocationMismatch { get; set; }
}
