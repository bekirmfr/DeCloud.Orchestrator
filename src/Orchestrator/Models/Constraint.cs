namespace Orchestrator.Models;

/// <summary>
/// A scheduling constraint expressed in the form
/// <c>{ target, operator, value }</c>. A list of constraints on a
/// <c>VmSpec</c> is evaluated as a flat AND — every constraint must
/// pass for a node to be eligible.
///
/// <para>
/// See <c>docs/SCHEDULING.md</c> §7 for the full design. v1 design notes:
/// </para>
/// <list type="bullet">
///   <item>VM-side only — nodes do not impose their own constraints in v1.</item>
///   <item>Hard filter only — constraints do not feed into soft scoring.</item>
///   <item>Flat AND — no OR/NOT/nested groups in v1. Negation expressible
///         per-constraint via operators (<c>not_in</c>, <c>not_contains</c>, etc.).</item>
///   <item>Static fields only — derived/runtime values (computePoints,
///         currentLoad) are not addressable as constraint targets in v1.</item>
///   <item>Fixed vocabulary — targets and operators come from a build-time
///         registry. Operators cannot define new ones at runtime.</item>
/// </list>
///
/// <para>Wire format example:</para>
/// <code>
/// {
///   "target":   "node.locality.jurisdictionTags",
///   "operator": "contains_all",
///   "value":    ["EU", "Schengen"]
/// }
/// </code>
/// </summary>
public class Constraint
{
    /// <summary>
    /// Target field on the candidate node, from the registered vocabulary.
    /// Validated at constraint-creation time (rejected if unknown).
    /// </summary>
    public required string Target { get; set; }

    /// <summary>
    /// Operator from the registered vocabulary. Must be type-compatible
    /// with the target's value type — validation rejects mismatches.
    /// </summary>
    public required string Operator { get; set; }

    /// <summary>
    /// Configured value the target is compared against. Type depends on
    /// (target, operator): may be a scalar (string, double, bool) or a
    /// list (string[]). Validated at constraint-creation time.
    ///
    /// <para>
    /// Note: when deserialized from JSON, this will be a
    /// <c>JsonElement</c>; when deserialized from MongoDB BSON, a
    /// <c>BsonValue</c>. The evaluator's normalization layer (Phase B)
    /// handles wire-format unboxing. For Phase A, callers passing
    /// already-typed C# objects (string, List&lt;string&gt;, double, bool)
    /// work directly.
    /// </para>
    /// </summary>
    public required object? Value { get; set; }
}

/// <summary>
/// Result of evaluating a single constraint against a node.
/// </summary>
public class ConstraintEvaluation
{
    /// <summary>True if the constraint passed against the node.</summary>
    public bool Passed { get; init; }

    /// <summary>
    /// Human-readable rejection reason when <see cref="Passed"/> is false.
    /// Designed for logs and for surfacing in the
    /// <c>ScoredNode.RejectionReason</c> field.
    ///
    /// <para>Format: <c>"target (actual_value) operator configured_value"</c></para>
    /// <para>Example: <c>"node.locality.country (BR) not_in [DE, FR, NL]"</c></para>
    /// </summary>
    public string? RejectionReason { get; init; }

    /// <summary>Constraint that passed.</summary>
    public static ConstraintEvaluation Pass() => new() { Passed = true };

    /// <summary>Constraint that failed, with a structured reason.</summary>
    public static ConstraintEvaluation Reject(string reason) =>
        new() { Passed = false, RejectionReason = reason };
}