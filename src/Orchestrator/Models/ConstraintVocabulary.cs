namespace Orchestrator.Models;

/// <summary>
/// Compile-time constants for constraint target names.
///
/// <para>
/// Use these instead of raw string literals everywhere a
/// <see cref="Constraint.Target"/> is constructed or compared.
/// The nesting mirrors the dot-separated wire format:
/// <c>ConstraintTargets.Node.Locality.Country</c> →
/// <c>"node.locality.country"</c>.
/// </para>
///
/// <para>
/// When adding a new target, add the constant here first, then register
/// the extractor in <c>ConstraintEvaluator.BuildTargetRegistry</c>, then
/// document in <c>docs/SCHEDULING.md</c> §7.
/// </para>
/// </summary>
public static class ConstraintTargets
{
    public static class Node
    {
        // ── Locality ──────────────────────────────────────────────────────

        public static class Locality
        {
            /// <summary>
            /// ISO 3166-1 alpha-2 country code of the node's declared
            /// location. Use <c>eq</c> / <c>in</c> / <c>not_in</c>.
            /// Alias: <see cref="Country"/>.
            /// </summary>
            public const string Country = "node.locality.country";

            /// <summary>
            /// DeCloud region code (e.g. "eu-central"). Must be a value
            /// from <c>regions.json</c>. Use <c>eq</c>, <c>in</c>,
            /// <c>adjacent_to</c>, or <c>same_continent_as</c>.
            /// </summary>
            public const string Region = "node.locality.region";

            /// <summary>
            /// Operator-scoped zone within a region (e.g. "eu-central-1").
            /// Use <c>eq</c> or <c>in</c>.
            /// </summary>
            public const string Zone = "node.locality.zone";

            /// <summary>
            /// Supranational membership tags the node carries (e.g. "EU",
            /// "NATO", "Schengen"). Use <c>contains</c>, <c>contains_all</c>,
            /// or <c>contains_any</c>.
            /// </summary>
            public const string JurisdictionTags = "node.locality.jurisdictionTags";

            /// <summary>
            /// True when the node's declared country and IP-derived country
            /// disagree. Use <c>eq false</c> to require jurisdictional
            /// certainty.
            /// </summary>
            public const string LocationMismatch = "node.locality.locationMismatch";
        }

        /// <summary>
        /// Alias for <see cref="Locality.Country"/>. Both resolve to the
        /// same extractor and are interchangeable on the wire.
        /// Prefer <see cref="Locality.Country"/> in new code for clarity.
        /// </summary>
        public const string Country = "node.country";

        // ── Hardware capability ───────────────────────────────────────────

        /// <summary>
        /// Extended hardware capability predicates, evaluated against static
        /// node attributes (hardware spec, not live utilisation).
        /// These are the building blocks for the preset library — "GPU required",
        /// "NVMe storage", "high bandwidth" — each compiles to one of these
        /// constraints on the wire.
        /// </summary>
        public static class Hardware
        {
            /// <summary>
            /// True when the node has at least one GPU.
            /// Use <c>eq true</c>. For GPU model specificity, use
            /// <see cref="GpuModel"/> instead.
            /// </summary>
            public const string HasGpu = "node.hardware.hasGpu";

            /// <summary>
            /// True when at least one storage device is of type NVMe.
            /// Use <c>eq true</c> for latency-sensitive workloads (databases,
            /// model checkpoints).
            /// </summary>
            public const string HasNvme = "node.hardware.hasNvme";

            /// <summary>
            /// True when the node's declared network bandwidth exceeds 1 Gbps.
            /// Use <c>eq true</c> for high-throughput workloads.
            /// </summary>
            public const string HighBandwidth = "node.hardware.highBandwidth";

            /// <summary>
            /// Physical CPU core count on the host.
            /// Use numeric operators (<c>gte</c>, <c>gt</c>) to require a
            /// minimum host core count. Distinct from vCPU allocation —
            /// this is the physical hardware.
            /// </summary>
            public const string CpuCores = "node.hardware.cpuCores";

            /// <summary>
            /// Total GPU VRAM in bytes, summed across all GPUs on the host.
            /// Use <c>gte</c> to require a minimum VRAM pool for large model
            /// inference (e.g., <c>gte 25769803776</c> for ≥24 GB).
            /// </summary>
            public const string GpuVramBytes = "node.hardware.gpuVramBytes";
        }

        /// <summary>
        /// CPU architecture string, normalised to "x86_64" or "aarch64".
        /// Use <c>eq</c>. Typically only needed for single-arch templates;
        /// multi-arch templates resolve the correct artifact post-scheduling.
        /// </summary>
        public const string Architecture = "node.architecture";

        /// <summary>
        /// True when the node has KVM virtualisation available.
        /// Use <c>eq true</c> (though KVM is also a fixed hard filter —
        /// expressing it as a constraint is redundant but harmless).
        /// </summary>
        public const string KvmAvailable = "node.kvmAvailable";

        /// <summary>
        /// Model name of the first GPU (null when no GPU is present).
        /// Use <c>eq</c> or <c>in</c> for GPU-model pinning.
        /// </summary>
        public const string GpuModel = "node.gpuModel";

        // ── Performance / reputation ──────────────────────────────────────

        /// <summary>
        /// List of quality-tier names the node is eligible for
        /// ("Burstable", "Balanced", "Standard", "Guaranteed").
        /// Use <c>contains</c> to require a specific tier capability.
        /// </summary>
        public const string Tier = "node.tier";

        /// <summary>
        /// Raw benchmark score from <c>NodePerformanceEvaluator</c>.
        /// Use numeric operators (<c>gte</c>, <c>gt</c>, etc.) to
        /// require minimum compute capability beyond tier eligibility.
        /// </summary>
        public const string BenchmarkScore = "node.benchmarkScore";

        /// <summary>
        /// 30-day rolling uptime percentage (0.0–100.0).
        /// Use numeric operators. Prefer <see cref="ReputationScore"/>
        /// when the full composite measure is relevant.
        /// </summary>
        public const string UptimePercent = "node.uptimePercent";

        /// <summary>
        /// Composite reputation score (0.0–1.0) computed as
        /// (uptimePercent × 0.7) + (successRate × 0.3).
        /// Single source of truth: <c>NodeReputation.Compute</c>.
        /// Use <c>gte</c> to set a quality floor.
        /// </summary>
        public const string ReputationScore = "node.reputationScore";

        // ── Operator metadata ─────────────────────────────────────────────

        /// <summary>
        /// Free-form tags declared by the node operator.
        /// Use <c>contains</c>, <c>contains_all</c>, or <c>contains_any</c>.
        /// </summary>
        public const string Tags = "node.tags";
    }
}

/// <summary>
/// Compile-time constants for constraint operator names.
///
/// <para>
/// Use these instead of raw string literals everywhere a
/// <see cref="Constraint.Operator"/> is constructed or compared.
/// </para>
///
/// <para>
/// Operator–target type compatibility is enforced at validation time
/// in <c>ConstraintEvaluator.Validate</c>. See <c>docs/SCHEDULING.md</c>
/// §7 for the full compatibility matrix.
/// </para>
/// </summary>
public static class ConstraintOperators
{
    // ── Equality / inequality ─────────────────────────────────────────────

    /// <summary>Scalar equality. Case-insensitive for strings.</summary>
    public const string Eq = "eq";

    /// <summary>Scalar inequality. Negation of <see cref="Eq"/>.</summary>
    public const string Neq = "neq";

    // ── Membership ────────────────────────────────────────────────────────

    /// <summary>Scalar is a member of the configured list.</summary>
    public const string In = "in";

    /// <summary>Scalar is not a member of the configured list.</summary>
    public const string NotIn = "not_in";

    // ── List containment ──────────────────────────────────────────────────

    /// <summary>Target list contains the configured scalar value.</summary>
    public const string Contains = "contains";

    /// <summary>Target list does not contain the configured scalar value.</summary>
    public const string NotContains = "not_contains";

    /// <summary>Target list is a superset of the configured list.</summary>
    public const string ContainsAll = "contains_all";

    /// <summary>Target list and configured list have at least one element in common.</summary>
    public const string ContainsAny = "contains_any";

    /// <summary>Target list and configured list have no elements in common.</summary>
    public const string ContainsNone = "contains_none";

    // ── Numeric ordering ──────────────────────────────────────────────────

    /// <summary>Greater than or equal.</summary>
    public const string Gte = "gte";

    /// <summary>Less than or equal.</summary>
    public const string Lte = "lte";

    /// <summary>Strictly greater than.</summary>
    public const string Gt = "gt";

    /// <summary>Strictly less than.</summary>
    public const string Lt = "lt";

    // ── Domain operators ──────────────────────────────────────────────────

    /// <summary>
    /// Node's region is adjacent to the configured region per
    /// <c>region-adjacency.json</c>. Only valid against region targets.
    /// </summary>
    public const string AdjacentTo = "adjacent_to";

    /// <summary>
    /// Node's region shares a continent with the configured region.
    /// Only valid against region targets.
    /// </summary>
    public const string SameContinentAs = "same_continent_as";

    /// <summary>
    /// Node's country carries the configured supranational tag.
    /// Only valid against country targets.
    /// </summary>
    public const string HasJurisdictionTag = "has_jurisdiction_tag";

    // ── String matching (scalar string targets only) ──────────────────────
    // These three operators form a matched set for pattern matching on
    // string scalars. None applies to StringList targets — use the
    // contains_* family for those.

    /// <summary>
    /// Target string starts with the configured prefix (case-insensitive).
    /// Primary use: hierarchical region codes where the prefix encodes a
    /// geographic tier.
    ///   <c>node.locality.region starts_with "na"</c> → na-central, na-east …
    ///   <c>node.locality.region starts_with "eu"</c> → eu-west, eu-central …
    /// </summary>
    public const string StartsWith = "starts_with";

    /// <summary>
    /// Target string ends with the configured suffix (case-insensitive).
    ///   <c>node.locality.region ends_with "central"</c> → na-central,
    ///   eu-central, ap-central …
    /// </summary>
    public const string EndsWith = "ends_with";

    /// <summary>
    /// Target string contains the configured substring (case-insensitive).
    /// Named <c>includes</c> to avoid collision with <see cref="Contains"/>,
    /// which checks whether a <em>list</em> target contains a scalar value.
    ///   <c>node.gpuModel includes "3090"</c> → RTX 3090, RTX 3090 Ti …
    ///   <c>node.locality.region includes "central"</c> → any -central region
    /// </summary>
    public const string Includes = "includes";
}