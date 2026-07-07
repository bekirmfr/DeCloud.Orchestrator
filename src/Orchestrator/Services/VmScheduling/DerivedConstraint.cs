using DeCloud.Shared.Enums;
using Orchestrator.Models;

namespace Orchestrator.Services.VmScheduling;

/// <summary>
/// A constraint derived from a first-class <see cref="VmSpec"/> field,
/// paired with the field it came from so rejection and compliance messages
/// can name their origin ("Derived from GpuMode=Proxied: ...") instead of
/// an authored-constraint index the tenant never wrote.
/// </summary>
public sealed record DerivedConstraint(Constraint Constraint, string Origin);

/// <summary>
/// Unified evaluation: reduce first-class spec fields to ephemeral
/// constraints so the single <c>IConstraintEvaluator</c> sees every
/// placement requirement — authored and field-carried alike.
///
/// <para>
/// This is the one new load-bearing piece of the unified-evaluation design
/// (docs: unified-evaluation-design §3). It generalizes the precedent set
/// by <c>MigrateVmAsync</c>'s ephemeral <c>node.architecture</c> constraint:
/// derive-for-evaluation, never persisted. Derived constraints MUST NOT be
/// written to <c>VmSpec.Constraints</c> — the spec field stays authoritative
/// for execution; the derived constraint is only how the evaluator sees it.
/// </para>
///
/// <para>
/// The derivation map (each row replaces a former hardcoded hard filter):
/// <list type="bullet">
///   <item><c>QualityTier</c> (always) → <c>node.tier contains &lt;tier&gt;</c>
///     — replaces FILTER 2. The <c>node.tier</c> extractor returns an empty
///     list when <c>PerformanceEvaluation</c> is null, so unevaluated nodes
///     are rejected exactly as FILTER 2's null-check did.</item>
///   <item><c>GpuMode == Proxied</c> → <c>node.gpu.proxiedAvailable eq true</c>
///     — replaces FILTER 5's capability checks (not its VRAM capacity check,
///     which stays in the filter chain with the other capacity filters).</item>
///   <item><c>GpuMode == Passthrough</c> → <c>node.gpu.passthroughAvailable eq true</c>
///     — likewise.</item>
///   <item><c>ReplicationFactor &gt; 0</c> → <c>node.hasActiveBlockStore eq true</c>
///     — replaces FILTER 8.1.</item>
/// </list>
/// </para>
///
/// <para>
/// Reads <c>spec.QualityTier</c> as the single tier source of truth — the
/// former <c>tier</c> parameter on <c>IVmSchedulingService</c> was removed
/// in the same change (design §3, "collapse two sources to one").
/// </para>
///
/// <para>
/// Static and stateless: both consumers (scheduling FILTER 10 in
/// <c>VmSchedulingService.ApplyHardFiltersAsync</c> and compliance in
/// <c>NodeService.FlagNonCompliantVmsAsync</c>) call the same function, so
/// they cannot drift. Deliberately not behind DI — there is nothing to
/// substitute, and a seam here would only invite a second implementation.
/// </para>
/// </summary>
public static class DerivedConstraints
{
    /// <summary>
    /// Derive the ephemeral constraints carried by this spec's first-class
    /// fields (GPU capability, BlockStore). May return an empty list — a
    /// plain VM with no GPU and no replication derives nothing. Tier is a
    /// hard filter (FILTER 2), not a derived constraint. Cheap: at most two
    /// small allocations.
    /// </summary>
    public static IReadOnlyList<DerivedConstraint> Derive(VmSpec spec)
    {
        // Capacity two — GPU capability and BlockStore. Tier is deliberately
        // NOT derived: it is enforced as a hard filter (FILTER 2). Unlike
        // GPU inventory and BlockStore status — which re-registration
        // rebuilds, so compliance can catch their drift — a node's
        // PerformanceEvaluation is PRESERVED across re-registration, so a
        // derived tier constraint could never catch tier drift in
        // compliance. It would be a check that cannot see its own target.
        var derived = new List<DerivedConstraint>(2);

        // ── GpuMode → node.gpu.* (replaces FILTER 5 capability checks) ───
        switch (spec.GpuMode)
        {
            case GpuMode.Proxied:
                derived.Add(new DerivedConstraint(
                    new Constraint
                    {
                        Target = ConstraintTargets.Node.Gpu.ProxiedAvailable,
                        Operator = ConstraintOperators.Eq,
                        Value = true
                    },
                    $"GpuMode={spec.GpuMode}"));
                break;

            case GpuMode.Passthrough:
                derived.Add(new DerivedConstraint(
                    new Constraint
                    {
                        Target = ConstraintTargets.Node.Gpu.PassthroughAvailable,
                        Operator = ConstraintOperators.Eq,
                        Value = true
                    },
                    $"GpuMode={spec.GpuMode}"));
                break;

                // GpuMode.None — no GPU requirement, nothing derived.
        }

        // ── ReplicationFactor → node.hasActiveBlockStore (replaces 8.1) ──
        // Ephemeral VMs (ReplicationFactor == 0) intentionally accept data
        // loss on node failure and can run anywhere — nothing derived.
        if (spec.ReplicationFactor > 0)
        {
            derived.Add(new DerivedConstraint(
                new Constraint
                {
                    Target = ConstraintTargets.Node.HasActiveBlockStore,
                    Operator = ConstraintOperators.Eq,
                    Value = true
                },
                $"ReplicationFactor={spec.ReplicationFactor}"));
        }

        return derived;
    }
}