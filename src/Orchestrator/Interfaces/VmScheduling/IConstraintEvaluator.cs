using Orchestrator.Models;

namespace Orchestrator.Interfaces.VmScheduling;
/// <summary>
/// Evaluates scheduling constraints against candidate nodes, and validates
/// constraints at creation time before they reach the scheduler.
///
/// <para>
/// Design and vocabulary documented in <c>docs/SCHEDULING.md</c> §7.
/// Singleton — registry is built once at construction and is immutable
/// thereafter. Stateless evaluation; safe to call from any thread.
/// </para>
///
/// <para>
/// Phase A scope: types, vocabulary, evaluation logic, validation.
/// No call sites yet — <see cref="VmSchedulingService.ApplyHardFiltersAsync"/>
/// will invoke <see cref="Evaluate"/> in Phase B.
/// </para>
/// </summary>
public interface IConstraintEvaluator
{
    /// <summary>
    /// Evaluate a single constraint against a node. Hot path — must be fast.
    /// Assumes constraint has been validated at creation time; performs a
    /// belt-and-suspenders re-check and rejects malformed constraints
    /// rather than throwing.
    /// </summary>
    ConstraintEvaluation Evaluate(Constraint constraint, Node node);

    /// <summary>
    /// Validate a constraint. Returns null if valid, otherwise a
    /// human-readable error message. Called at VM creation time before
    /// the constraint is persisted to <c>VmSpec.Constraints</c>.
    /// </summary>
    string? Validate(Constraint constraint);

    /// <summary>
    /// Validate a list of constraints. Returns null if all valid; otherwise
    /// returns the first error prefixed with the failing constraint's index
    /// (e.g., <c>"Constraint #2: Unknown target 'node.foobar'"</c>).
    /// </summary>
    string? ValidateSet(IEnumerable<Constraint> constraints);

    /// <summary>
    /// All registered target names. Useful for the dashboard's constraint
    /// builder UI and for OpenAPI documentation.
    /// </summary>
    IReadOnlyCollection<string> KnownTargets { get; }

    /// <summary>
    /// All registered operator names. Useful for the dashboard's constraint
    /// builder UI and for OpenAPI documentation.
    /// </summary>
    IReadOnlyCollection<string> KnownOperators { get; }
}
