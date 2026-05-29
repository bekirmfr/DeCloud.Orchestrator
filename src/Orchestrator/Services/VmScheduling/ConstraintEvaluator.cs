using DeCloud.Shared.Enums;
using MongoDB.Bson;
using Orchestrator.Interfaces.VmScheduling;
using Orchestrator.Models;
using Orchestrator.Services.Locality;
using System.Text.Json;

namespace Orchestrator.Services.VmScheduling;

public class ConstraintEvaluator : IConstraintEvaluator
{
    private readonly ILocalityService _locality;
    private readonly ILogger<ConstraintEvaluator> _logger;

    private readonly Dictionary<string, TargetDescriptor> _targets;
    private readonly Dictionary<string, OperatorDescriptor> _operators;

    public ConstraintEvaluator(
        ILocalityService locality,
        ILogger<ConstraintEvaluator> logger)
    {
        _locality = locality;
        _logger = logger;
        _targets = BuildTargetRegistry();
        _operators = BuildOperatorRegistry();

        _logger.LogInformation(
            "ConstraintEvaluator initialized: {TargetCount} targets, {OpCount} operators",
            _targets.Count, _operators.Count);
    }

    public IReadOnlyCollection<string> KnownTargets => _targets.Keys;
    public IReadOnlyCollection<string> KnownOperators => _operators.Keys;

    public IReadOnlyDictionary<string, string> TargetTypes =>
        _targets.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ValueType.ToString(),
            StringComparer.Ordinal);

    // ─── Evaluation hot path ──────────────────────────────────────────

    public ConstraintEvaluation Evaluate(Constraint constraint, Node node)
    {
        // Belt-and-suspenders validation. Constraints SHOULD be validated
        // at create time, but failing soft (reject) is safer than throwing
        // here in the scheduling hot path.
        var validationError = Validate(constraint);
        if (validationError is not null)
        {
            _logger.LogWarning(
                "Malformed constraint reached evaluator: {Error}", validationError);
            return ConstraintEvaluation.Reject(validationError);
        }

        var targetDesc = _targets[constraint.Target];
        var opDesc = _operators[constraint.Operator];

        // Normalize wire-format wrappers (JsonElement, BsonValue) to native
        // C# types so the operator's evaluator sees typed values. See
        // NormalizeValue for the full contract.
        var configuredValue = NormalizeValue(constraint.Value);

        var actual = targetDesc.Extract(node, _locality);
        var passed = opDesc.Eval(actual, configuredValue, _locality);

        if (!passed)
        {
            return ConstraintEvaluation.Reject(
                $"{constraint.Target} ({FormatValue(actual)}) " +
                $"{constraint.Operator} {FormatValue(configuredValue)}");
        }

        return ConstraintEvaluation.Pass();
    }

    // ─── Validation ───────────────────────────────────────────────────

    public string? Validate(Constraint constraint)
    {
        if (string.IsNullOrEmpty(constraint.Target))
            return "Target is required";
        if (string.IsNullOrEmpty(constraint.Operator))
            return "Operator is required";

        if (!_targets.TryGetValue(constraint.Target, out var targetDesc))
            return $"Unknown target '{constraint.Target}'. " +
                   $"Known targets: [{string.Join(", ", _targets.Keys.OrderBy(k => k))}]";

        if (!_operators.TryGetValue(constraint.Operator, out var opDesc))
            return $"Unknown operator '{constraint.Operator}'. " +
                   $"Known operators: [{string.Join(", ", _operators.Keys.OrderBy(k => k))}]";

        if (!opDesc.AcceptsTargetType(targetDesc.ValueType))
            return $"Operator '{constraint.Operator}' is not compatible with " +
                   $"target '{constraint.Target}' (target type: {targetDesc.ValueType})";

        var valueError = opDesc.ValidateValue(targetDesc.ValueType, NormalizeValue(constraint.Value));
        if (valueError is not null)
            return $"Constraint value invalid: {valueError}";

        return null;
    }

    public string? ValidateSet(IEnumerable<Constraint> constraints)
    {
        var index = 0;
        foreach (var c in constraints)
        {
            var err = Validate(c);
            if (err is not null)
                return $"Constraint #{index}: {err}";
            index++;
        }
        return null;
    }

    // ─── Target registry ──────────────────────────────────────────────

    /// <summary>
    /// Build the target vocabulary. Each entry maps a wire-format target
    /// name to its source field on <see cref="Node"/> and value type.
    ///
    /// <para>Adding a target: define an extractor function, append to the
    /// registry, document in <c>SCHEDULING.md</c> §7.4.</para>
    /// </summary>
    private static Dictionary<string, TargetDescriptor> BuildTargetRegistry()
    {
        var r = new Dictionary<string, TargetDescriptor>(StringComparer.Ordinal);

        // ─── Locality (jurisdiction-aware) ─────────────────────────
        // Node.Country and Node.Locality.Country are aliases.
        r[ConstraintTargets.Node.Country] = new TargetDescriptor(
            ConstraintTargets.Node.Country, ConstraintValueType.String,
            (n, _) => n.Locality?.Country);
        r[ConstraintTargets.Node.Locality.Country] = new TargetDescriptor(
            ConstraintTargets.Node.Locality.Country, ConstraintValueType.String,
            (n, _) => n.Locality?.Country);

        r[ConstraintTargets.Node.Locality.Region] = new TargetDescriptor(
            ConstraintTargets.Node.Locality.Region, ConstraintValueType.String,
            (n, _) => n.Locality?.Region ?? n.Locality.Region);

        r[ConstraintTargets.Node.Locality.Zone] = new TargetDescriptor(
            ConstraintTargets.Node.Locality.Zone, ConstraintValueType.String,
            (n, _) => n.Locality?.Zone ?? n.Locality.Zone);

        r[ConstraintTargets.Node.Locality.JurisdictionTags] = new TargetDescriptor(
            ConstraintTargets.Node.Locality.JurisdictionTags, ConstraintValueType.StringList,
            (n, _) => (object?)(n.Locality?.JurisdictionTags ?? new List<string>()));

        r[ConstraintTargets.Node.Locality.LocationMismatch] = new TargetDescriptor(
            ConstraintTargets.Node.Locality.LocationMismatch, ConstraintValueType.Boolean,
            (n, _) => (object?)(n.Locality?.LocationMismatch ?? false));

        // ─── Hardware capability ────────────────────────────────────
        r[ConstraintTargets.Node.Architecture] = new TargetDescriptor(
            ConstraintTargets.Node.Architecture, ConstraintValueType.String,
            (n, _) => n.Architecture);

        r[ConstraintTargets.Node.KvmAvailable] = new TargetDescriptor(
            ConstraintTargets.Node.KvmAvailable, ConstraintValueType.Boolean,
            (n, _) => (object?)n.HardwareInventory.KvmAvailable);

        // GpuModel returns the first GPU's model (null if no GPUs).
        // Multi-GPU nodes filter on a single representative model. If a
        // future requirement needs all-GPU matching, add GpuModels
        // (plural, StringList type) as a separate target.
        r[ConstraintTargets.Node.GpuModel] = new TargetDescriptor(
            ConstraintTargets.Node.GpuModel, ConstraintValueType.String,
            (n, _) => n.HardwareInventory.Gpus.FirstOrDefault()?.Model);

        // ─── Performance / reputation ────────────────────────────
        // Tier returns the list of quality-tier names this node qualifies
        // for. Use with Contains to require a specific tier capability.
        r[ConstraintTargets.Node.Tier] = new TargetDescriptor(
            ConstraintTargets.Node.Tier, ConstraintValueType.StringList,
            (n, _) => (object?)(n.PerformanceEvaluation?.EligibleTiers
                .Select(t => t.ToString()).ToList()
                ?? new List<string>()));

        r[ConstraintTargets.Node.BenchmarkScore] = new TargetDescriptor(
            ConstraintTargets.Node.BenchmarkScore, ConstraintValueType.Numeric,
            (n, _) => (object?)(n.PerformanceEvaluation?.BenchmarkScore));

        r[ConstraintTargets.Node.UptimePercent] = new TargetDescriptor(
            ConstraintTargets.Node.UptimePercent, ConstraintValueType.Numeric,
            (n, _) => (object?)n.UptimePercentage);

        // ReputationScore: full composite formula via NodeReputation.Compute.
        // Use when the weighted uptime+success measure matters; use
        // UptimePercent when only uptime matters.
        r[ConstraintTargets.Node.ReputationScore] = new TargetDescriptor(
            ConstraintTargets.Node.ReputationScore, ConstraintValueType.Numeric,
            (n, _) => (object?)NodeReputation.Compute(n));

        // ─── Operator metadata ──────────────────────────────────────────────
        r[ConstraintTargets.Node.Tags] = new TargetDescriptor(
            ConstraintTargets.Node.Tags, ConstraintValueType.StringList,
            (n, _) => (object?)(n.Tags ?? new List<string>()));

        // ─── Hardware capabilities ──────────────────────────────────────────
        // Static node attributes — reflect hardware spec, not live utilisation.
        // These power the preset library: "GPU required", "NVMe storage", etc.

        r[ConstraintTargets.Node.Hardware.HasGpu] = new TargetDescriptor(
            ConstraintTargets.Node.Hardware.HasGpu, ConstraintValueType.Boolean,
            (n, _) => (object?)n.HardwareInventory.SupportsGpu);

        r[ConstraintTargets.Node.Hardware.HasNvme] = new TargetDescriptor(
            ConstraintTargets.Node.Hardware.HasNvme, ConstraintValueType.Boolean,
            (n, _) => (object?)n.HardwareInventory.Storage
                .Any(s => s.Type == StorageType.NVMe));

        r[ConstraintTargets.Node.Hardware.HighBandwidth] = new TargetDescriptor(
            ConstraintTargets.Node.Hardware.HighBandwidth, ConstraintValueType.Boolean,
            (n, _) => (object?)(
                (n.HardwareInventory.Network.BandwidthBitsPerSecond ?? 0) > 1_000_000_000));

        // NatType.None = public IP matches private IP = direct internet connection,
        // no NAT or CGNAT in the path. Any other NatType (Unknown, FullCone, etc.)
        // means the node is behind NAT and accessed via relay.
        r[ConstraintTargets.Node.Hardware.HasPublicIp] = new TargetDescriptor(
            ConstraintTargets.Node.Hardware.HasPublicIp, ConstraintValueType.Boolean,
            (n, _) => (object?)(n.HardwareInventory.Network.NatType == NatType.None));

        r[ConstraintTargets.Node.Hardware.CpuCores] = new TargetDescriptor(
            ConstraintTargets.Node.Hardware.CpuCores, ConstraintValueType.Numeric,
            (n, _) => (object?)(double)n.HardwareInventory.Cpu.PhysicalCores);

        r[ConstraintTargets.Node.Hardware.GpuVramBytes] = new TargetDescriptor(
            ConstraintTargets.Node.Hardware.GpuVramBytes, ConstraintValueType.Numeric,
            (n, _) => (object?)(double)n.HardwareInventory.Gpus.Sum(g => g.MemoryBytes));

        return r;
    }

    // ─── Operator registry ────────────────────────────────────────────

    /// <summary>
    /// Build the operator vocabulary. Each entry declares the target types
    /// it accepts, validates the configured value, and evaluates a node's
    /// actual value against the configured value.
    /// </summary>
    private static Dictionary<string, OperatorDescriptor> BuildOperatorRegistry()
    {
        var r = new Dictionary<string, OperatorDescriptor>(StringComparer.Ordinal);

        // ── Equality / inequality (scalar = scalar) ────────────────
        r[ConstraintOperators.Eq] = new OperatorDescriptor(
            ConstraintOperators.Eq,
            t => t is ConstraintValueType.String
                or ConstraintValueType.Numeric
                or ConstraintValueType.Boolean,
            (t, v) => ValidateScalar(t, v),
            (actual, configured, _) => ScalarEquals(actual, configured));

        r[ConstraintOperators.Neq] = new OperatorDescriptor(
            ConstraintOperators.Neq,
            t => t is ConstraintValueType.String
                or ConstraintValueType.Numeric
                or ConstraintValueType.Boolean,
            (t, v) => ValidateScalar(t, v),
            (actual, configured, _) => !ScalarEquals(actual, configured));

        // ── Membership: scalar in/not_in list ──────────────────────
        r[ConstraintOperators.In] = new OperatorDescriptor(
            ConstraintOperators.In,
            t => t is ConstraintValueType.String or ConstraintValueType.Numeric,
            (t, v) => ValidateList(t, v),
            (actual, configured, _) => ListContainsScalar(configured, actual));

        r[ConstraintOperators.NotIn] = new OperatorDescriptor(
            ConstraintOperators.NotIn,
            t => t is ConstraintValueType.String or ConstraintValueType.Numeric,
            (t, v) => ValidateList(t, v),
            (actual, configured, _) => !ListContainsScalar(configured, actual));

        // ── List-on-scalar: list contains/not_contains a value ────────
        r[ConstraintOperators.Contains] = new OperatorDescriptor(
            ConstraintOperators.Contains,
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, _) => ListContainsScalar(actual, configured));

        r[ConstraintOperators.NotContains] = new OperatorDescriptor(
            ConstraintOperators.NotContains,
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, _) => !ListContainsScalar(actual, configured));

        // ── List-on-list ───────────────────────────────────────────
        r[ConstraintOperators.ContainsAll] = new OperatorDescriptor(
            ConstraintOperators.ContainsAll,
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateList(ConstraintValueType.String, v),
            (actual, configured, _) => ListContainsAll(actual, configured));

        r[ConstraintOperators.ContainsAny] = new OperatorDescriptor(
            ConstraintOperators.ContainsAny,
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateList(ConstraintValueType.String, v),
            (actual, configured, _) => ListContainsAny(actual, configured));

        r[ConstraintOperators.ContainsNone] = new OperatorDescriptor(
            ConstraintOperators.ContainsNone,
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateList(ConstraintValueType.String, v),
            (actual, configured, _) => !ListContainsAny(actual, configured));

        // ── Numeric ordering ───────────────────────────────────────
        r[ConstraintOperators.Gte] = new OperatorDescriptor(
            ConstraintOperators.Gte, t => t == ConstraintValueType.Numeric,
            (_, v) => ValidateScalar(ConstraintValueType.Numeric, v),
            (actual, configured, _) => NumericCompare(actual, configured) is int c && c >= 0);

        r[ConstraintOperators.Lte] = new OperatorDescriptor(
            ConstraintOperators.Lte, t => t == ConstraintValueType.Numeric,
            (_, v) => ValidateScalar(ConstraintValueType.Numeric, v),
            (actual, configured, _) => NumericCompare(actual, configured) is int c && c <= 0);

        r[ConstraintOperators.Gt] = new OperatorDescriptor(
            ConstraintOperators.Gt, t => t == ConstraintValueType.Numeric,
            (_, v) => ValidateScalar(ConstraintValueType.Numeric, v),
            (actual, configured, _) => NumericCompare(actual, configured) is int c && c > 0);

        r[ConstraintOperators.Lt] = new OperatorDescriptor(
            ConstraintOperators.Lt, t => t == ConstraintValueType.Numeric,
            (_, v) => ValidateScalar(ConstraintValueType.Numeric, v),
            (actual, configured, _) => NumericCompare(actual, configured) is int c && c < 0);

        // ── Domain operators (locality-aware) ──────────────────────
        // AdjacentTo: node's region is adjacent to the configured region
        // per region-adjacency.json.
        r[ConstraintOperators.AdjacentTo] = new OperatorDescriptor(
            ConstraintOperators.AdjacentTo, t => t == ConstraintValueType.String,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, locality) =>
            {
                var nodeRegion = actual as string;
                var configuredRegion = configured as string;
                if (nodeRegion is null || configuredRegion is null) return false;
                return locality.GetAdjacentRegions(configuredRegion)
                    .Contains(nodeRegion, StringComparer.OrdinalIgnoreCase);
            });

        // SameContinentAs: node's region shares a continent with configured.
        r[ConstraintOperators.SameContinentAs] = new OperatorDescriptor(
            ConstraintOperators.SameContinentAs, t => t == ConstraintValueType.String,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, locality) =>
            {
                var nodeRegion = actual as string;
                var configuredRegion = configured as string;
                if (nodeRegion is null || configuredRegion is null) return false;
                var c1 = locality.GetContinentForRegion(nodeRegion);
                var c2 = locality.GetContinentForRegion(configuredRegion);
                return c1 is not null && c2 is not null &&
                       string.Equals(c1, c2, StringComparison.OrdinalIgnoreCase);
            });

        // HasJurisdictionTag: node's country carries the configured
        // supranational tag — lets a constraint ask "is this country in
        // the EU?" without enumerating member states.
        r[ConstraintOperators.HasJurisdictionTag] = new OperatorDescriptor(
            ConstraintOperators.HasJurisdictionTag, t => t == ConstraintValueType.String,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, locality) =>
            {
                var country = actual as string;
                var tag = configured as string;
                if (country is null || tag is null) return false;
                return locality.GetTagsForCountry(country)
                    .Contains(tag, StringComparer.OrdinalIgnoreCase);
            });

        // ── String matching (scalar string targets only) ───────────────────
        // starts_with / ends_with / includes form a matched set.
        // None accepts StringList targets — use contains_* for those.

        r[ConstraintOperators.StartsWith] = new OperatorDescriptor(
            ConstraintOperators.StartsWith,
            t => t == ConstraintValueType.String,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, _) =>
            {
                var s = actual as string;
                var prefix = configured as string;
                if (s is null || prefix is null) return false;
                return s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            });

        r[ConstraintOperators.EndsWith] = new OperatorDescriptor(
            ConstraintOperators.EndsWith,
            t => t == ConstraintValueType.String,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, _) =>
            {
                var s = actual as string;
                var suffix = configured as string;
                if (s is null || suffix is null) return false;
                return s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            });

        r[ConstraintOperators.Includes] = new OperatorDescriptor(
            ConstraintOperators.Includes,
            t => t == ConstraintValueType.String,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, _) =>
            {
                var s = actual as string;
                var substring = configured as string;
                if (s is null || substring is null) return false;
                return s.Contains(substring, StringComparison.OrdinalIgnoreCase);
            });

        return r;
    }

    // ─── Operator implementation helpers ──────────────────────────────

    private static bool ScalarEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        // String comparison: case-insensitive for human-friendly behavior
        // (EU country codes, region codes, architecture names are all
        // conventionally compared case-insensitively in the rest of the
        // codebase — e.g. ApplyHardFiltersAsync uses
        // StringComparison.OrdinalIgnoreCase consistently).
        if (a is string s1 && b is string s2)
            return string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase);

        if (a is bool ba && b is bool bb)
            return ba == bb;

        // Numeric: compare via double precision
        if (TryToDouble(a, out var d1) && TryToDouble(b, out var d2))
            return d1 == d2;

        return Equals(a, b);
    }

    private static bool ListContainsScalar(object? listObj, object? scalar)
    {
        if (listObj is not IEnumerable<string> list) return false;
        if (scalar is not string s) return false;
        return list.Contains(s, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ListContainsAll(object? actualList, object? configuredList)
    {
        if (actualList is not IEnumerable<string> a) return false;
        if (configuredList is not IEnumerable<string> c) return false;
        return c.All(item => a.Contains(item, StringComparer.OrdinalIgnoreCase));
    }

    private static bool ListContainsAny(object? actualList, object? configuredList)
    {
        if (actualList is not IEnumerable<string> a) return false;
        if (configuredList is not IEnumerable<string> c) return false;
        return c.Any(item => a.Contains(item, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Compare two numeric values. Returns null if either is non-numeric;
    /// otherwise &lt; 0, 0, &gt; 0 in standard CompareTo semantics.
    /// </summary>
    private static int? NumericCompare(object? a, object? b)
    {
        if (!TryToDouble(a, out var d1)) return null;
        if (!TryToDouble(b, out var d2)) return null;
        return d1.CompareTo(d2);
    }

    private static bool TryToDouble(object? o, out double d)
    {
        switch (o)
        {
            case double dv: d = dv; return true;
            case float fv: d = fv; return true;
            case int iv: d = iv; return true;
            case long lv: d = lv; return true;
            case decimal mv: d = (double)mv; return true;
            default: d = 0; return false;
        }
    }

    // ─── Validators (shape-only checks at constraint creation) ───────

    private static string? ValidateScalar(ConstraintValueType expectedType, object? value)
    {
        if (value is null) return "value cannot be null for scalar operators";
        return expectedType switch
        {
            ConstraintValueType.String when value is string => null,
            ConstraintValueType.Boolean when value is bool => null,
            ConstraintValueType.Numeric when TryToDouble(value, out _) => null,
            _ => $"expected {expectedType}, got {value.GetType().Name}"
        };
    }

    private static string? ValidateList(ConstraintValueType elementType, object? value)
    {
        if (value is null) return "value cannot be null for list operators";
        if (value is not IEnumerable<string> list)
            return $"expected list of {elementType} (got {value.GetType().Name})";

        var arr = list.ToList();
        if (arr.Count == 0) return "list cannot be empty";

        // For string lists we don't need per-element type checking — the
        // parameterization already constrains element type. If we add
        // numeric lists later, validate elements here.
        return null;
    }

    // ─── Rejection-message formatter ──────────────────────────────────

    private static string FormatValue(object? v) => v switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        IEnumerable<string> list => $"[{string.Join(", ", list)}]",
        _ => v.ToString() ?? "?"
    };

    // ─── Wire-format normalization ────────────────────────────────────

    /// <summary>
    /// Convert wire-format value wrappers to native C# types.
    ///
    /// <para>
    /// JSON deserialization (System.Text.Json) packages <c>object?</c> values
    /// as <see cref="JsonElement"/>. MongoDB BSON deserialization packages
    /// them as <see cref="BsonValue"/>. The evaluator's type-checking and
    /// operator implementations expect native C# types
    /// (<c>string</c>, <c>double</c>, <c>bool</c>, <c>List&lt;string&gt;</c>),
    /// so we unbox here at the public entry points.
    /// </para>
    ///
    /// <para>
    /// Already-typed values pass through unchanged. Unknown wrappers return
    /// as-is and will fail type-checking with a clear error rather than
    /// throwing — keeping evaluation safe for malformed input.
    /// </para>
    ///
    /// <para>
    /// Numeric values from BSON int32/int64 are widened to <c>double</c>
    /// because the operator vocabulary uses double for all numeric
    /// comparisons (consistent with <c>TryToDouble</c> elsewhere in this
    /// class).
    /// </para>
    /// </summary>
    private static object? NormalizeValue(object? value)
    {
        if (value is null) return null;

        // Already-native types pass through
        if (value is string or bool or double or float or int or long or decimal)
            return value;
        if (value is IEnumerable<string>)
            return value;

        // System.Text.Json
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => je.GetDouble(),
                JsonValueKind.Array => je.EnumerateArray()
                    .Select(x => x.GetString() ?? string.Empty)
                    .ToList(),
                JsonValueKind.Null => null,
                _ => je.ToString()
            };
        }

        // MongoDB BSON
        if (value is BsonValue bv)
        {
            return bv.BsonType switch
            {
                BsonType.String => bv.AsString,
                BsonType.Boolean => bv.AsBoolean,
                BsonType.Double => bv.AsDouble,
                BsonType.Int32 => (double)bv.AsInt32,
                BsonType.Int64 => (double)bv.AsInt64,
                BsonType.Array => bv.AsBsonArray
                    .Select(x => x.IsString ? x.AsString : x.ToString() ?? string.Empty)
                    .ToList(),
                BsonType.Null => null,
                _ => bv.ToString() ?? string.Empty
            };
        }

        // Unknown wrapper — return as-is, validation will catch the type mismatch
        return value;
    }

    // ─── Internal types ──────────────────────────────────────────────

    /// <summary>Type a target's value carries. Operators declare which
    /// of these they accept.</summary>
    internal enum ConstraintValueType
    {
        String,
        StringList,
        Numeric,
        Boolean,
    }

    /// <summary>Vocabulary entry describing a single target.</summary>
    internal record TargetDescriptor(
        string Name,
        ConstraintValueType ValueType,
        Func<Node, ILocalityService, object?> Extract);

    /// <summary>Vocabulary entry describing a single operator.</summary>
    internal record OperatorDescriptor(
        string Name,
        Func<ConstraintValueType, bool> AcceptsTargetType,
        Func<ConstraintValueType, object?, string?> ValidateValue,
        Func<object?, object?, ILocalityService, bool> Eval);
}