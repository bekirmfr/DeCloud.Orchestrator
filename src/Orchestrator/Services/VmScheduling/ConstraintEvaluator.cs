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
        // node.country and node.locality.country are aliases.
        r["node.country"] = new TargetDescriptor(
            "node.country", ConstraintValueType.String,
            (n, _) => n.Locality?.Country);
        r["node.locality.country"] = new TargetDescriptor(
            "node.locality.country", ConstraintValueType.String,
            (n, _) => n.Locality?.Country);

        r["node.locality.region"] = new TargetDescriptor(
            "node.locality.region", ConstraintValueType.String,
            (n, _) => n.Locality?.Region ?? n.Region);

        r["node.locality.zone"] = new TargetDescriptor(
            "node.locality.zone", ConstraintValueType.String,
            (n, _) => n.Locality?.Zone ?? n.Zone);

        r["node.locality.jurisdictionTags"] = new TargetDescriptor(
            "node.locality.jurisdictionTags", ConstraintValueType.StringList,
            (n, _) => (object?)(n.Locality?.JurisdictionTags ?? new List<string>()));

        r["node.locality.locationMismatch"] = new TargetDescriptor(
            "node.locality.locationMismatch", ConstraintValueType.Boolean,
            (n, _) => (object?)(n.Locality?.LocationMismatch ?? false));

        // ─── Hardware capability ────────────────────────────────────
        r["node.architecture"] = new TargetDescriptor(
            "node.architecture", ConstraintValueType.String,
            (n, _) => n.Architecture);

        r["node.kvmAvailable"] = new TargetDescriptor(
            "node.kvmAvailable", ConstraintValueType.Boolean,
            (n, _) => (object?)n.HardwareInventory.KvmAvailable);

        // node.gpuModel returns the first GPU's model (or null if no GPUs).
        // Multi-GPU nodes filter on a single representative model. If a
        // future tenant requirement needs all-GPU matching, we add
        // node.gpuModels (plural, StringList type) as a separate target.
        r["node.gpuModel"] = new TargetDescriptor(
            "node.gpuModel", ConstraintValueType.String,
            (n, _) => n.HardwareInventory.Gpus.FirstOrDefault()?.Model);

        // ─── Performance / reputation ────────────────────────────
        // node.tier returns the list of tier names this node qualifies for
        // (Burstable, Balanced, Standard, Guaranteed). Use with `contains`
        // to filter for "this node can host tier X".
        r["node.tier"] = new TargetDescriptor(
            "node.tier", ConstraintValueType.StringList,
            (n, _) => (object?)(n.PerformanceEvaluation?.EligibleTiers
                .Select(t => t.ToString()).ToList()
                ?? new List<string>()));

        r["node.benchmarkScore"] = new TargetDescriptor(
            "node.benchmarkScore", ConstraintValueType.Numeric,
            (n, _) => (object?)(n.PerformanceEvaluation?.BenchmarkScore));

        r["node.uptimePercent"] = new TargetDescriptor(
            "node.uptimePercent", ConstraintValueType.Numeric,
            (n, _) => (object?)n.UptimePercentage);

        // ─── Operator metadata ──────────────────────────────────
        r["node.tags"] = new TargetDescriptor(
            "node.tags", ConstraintValueType.StringList,
            (n, _) => (object?)(n.Tags ?? new List<string>()));

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
        r["eq"] = new OperatorDescriptor(
            "eq",
            t => t is ConstraintValueType.String
                or ConstraintValueType.Numeric
                or ConstraintValueType.Boolean,
            (t, v) => ValidateScalar(t, v),
            (actual, configured, _) => ScalarEquals(actual, configured));

        r["neq"] = new OperatorDescriptor(
            "neq",
            t => t is ConstraintValueType.String
                or ConstraintValueType.Numeric
                or ConstraintValueType.Boolean,
            (t, v) => ValidateScalar(t, v),
            (actual, configured, _) => !ScalarEquals(actual, configured));

        // ── Membership: scalar in/not_in list ──────────────────────
        r["in"] = new OperatorDescriptor(
            "in",
            t => t is ConstraintValueType.String or ConstraintValueType.Numeric,
            (t, v) => ValidateList(t, v),
            (actual, configured, _) => ListContainsScalar(configured, actual));

        r["not_in"] = new OperatorDescriptor(
            "not_in",
            t => t is ConstraintValueType.String or ConstraintValueType.Numeric,
            (t, v) => ValidateList(t, v),
            (actual, configured, _) => !ListContainsScalar(configured, actual));

        // ── List-on-scalar: list contains/not_contains a value ────────
        r["contains"] = new OperatorDescriptor(
            "contains",
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, _) => ListContainsScalar(actual, configured));

        r["not_contains"] = new OperatorDescriptor(
            "not_contains",
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, _) => !ListContainsScalar(actual, configured));

        // ── List-on-list: contains_all / contains_any / contains_none ─
        r["contains_all"] = new OperatorDescriptor(
            "contains_all",
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateList(ConstraintValueType.String, v),
            (actual, configured, _) => ListContainsAll(actual, configured));

        r["contains_any"] = new OperatorDescriptor(
            "contains_any",
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateList(ConstraintValueType.String, v),
            (actual, configured, _) => ListContainsAny(actual, configured));

        r["contains_none"] = new OperatorDescriptor(
            "contains_none",
            t => t == ConstraintValueType.StringList,
            (_, v) => ValidateList(ConstraintValueType.String, v),
            (actual, configured, _) => !ListContainsAny(actual, configured));

        // ── Numeric ordering ───────────────────────────────────────
        r["gte"] = new OperatorDescriptor(
            "gte", t => t == ConstraintValueType.Numeric,
            (_, v) => ValidateScalar(ConstraintValueType.Numeric, v),
            (actual, configured, _) => NumericCompare(actual, configured) is int c && c >= 0);

        r["lte"] = new OperatorDescriptor(
            "lte", t => t == ConstraintValueType.Numeric,
            (_, v) => ValidateScalar(ConstraintValueType.Numeric, v),
            (actual, configured, _) => NumericCompare(actual, configured) is int c && c <= 0);

        r["gt"] = new OperatorDescriptor(
            "gt", t => t == ConstraintValueType.Numeric,
            (_, v) => ValidateScalar(ConstraintValueType.Numeric, v),
            (actual, configured, _) => NumericCompare(actual, configured) is int c && c > 0);

        r["lt"] = new OperatorDescriptor(
            "lt", t => t == ConstraintValueType.Numeric,
            (_, v) => ValidateScalar(ConstraintValueType.Numeric, v),
            (actual, configured, _) => NumericCompare(actual, configured) is int c && c < 0);

        // ── Domain operators (locality-aware) ──────────────────────
        // adjacent_to: node's region is adjacent to the configured region
        // per region-adjacency.json. Only valid against region targets.
        r["adjacent_to"] = new OperatorDescriptor(
            "adjacent_to", t => t == ConstraintValueType.String,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, locality) =>
            {
                var nodeRegion = actual as string;
                var configuredRegion = configured as string;
                if (nodeRegion is null || configuredRegion is null) return false;
                return locality.GetAdjacentRegions(configuredRegion)
                    .Contains(nodeRegion, StringComparer.OrdinalIgnoreCase);
            });

        // same_continent_as: node's region shares a continent with configured.
        r["same_continent_as"] = new OperatorDescriptor(
            "same_continent_as", t => t == ConstraintValueType.String,
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

        // has_jurisdiction_tag: node's country (string target) carries the
        // configured supranational tag. Lets a constraint on country target
        // ask "is this country in the EU?" without enumerating EU members.
        r["has_jurisdiction_tag"] = new OperatorDescriptor(
            "has_jurisdiction_tag", t => t == ConstraintValueType.String,
            (_, v) => ValidateScalar(ConstraintValueType.String, v),
            (actual, configured, locality) =>
            {
                var country = actual as string;
                var tag = configured as string;
                if (country is null || tag is null) return false;
                return locality.GetTagsForCountry(country)
                    .Contains(tag, StringComparer.OrdinalIgnoreCase);
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