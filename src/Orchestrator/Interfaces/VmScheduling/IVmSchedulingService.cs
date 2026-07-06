using DeCloud.Shared.Enums;
using Orchestrator.Models;

namespace Orchestrator.Interfaces.VmScheduling;

/// <summary>
/// Scheduling interface for VM node selection and eligibility checking.
///
/// All scheduling requirements are evaluated through FILTER 10 in
/// <c>ApplyHardFiltersAsync</c>: tenant-authored constraints from
/// <c>spec.Constraints</c> (architecture, locality, reputation,
/// jurisdiction, country, zone, ...) plus constraints derived from
/// first-class spec fields (<c>QualityTier</c>, <c>GpuMode</c>,
/// <c>ReplicationFactor</c>) by <c>DerivedConstraints.Derive</c>.
/// No legacy per-parameter overrides exist — the constraint vocabulary
/// is the single mechanism.
/// </summary>
public interface IVmSchedulingService
{
    /// <summary>
    /// Select the best eligible node for a VM.
    /// Tenant-expressible requirements go in <c>spec.Constraints</c>;
    /// field-carried requirements (tier, GPU mode, replication) are
    /// derived automatically at evaluation time.
    /// See <c>docs/SCHEDULING.md</c> §2 for entry paths, including the
    /// template marketplace merge policy (Path D).
    /// </summary>
    Task<Node?> SelectBestNodeForVmAsync(
        VmSpec spec,
        CancellationToken ct = default);

    /// <summary>
    /// Return all nodes that pass the hard filter chain for this spec.
    /// </summary>
    Task<List<Node>> GetAvailableNodesForVmAsync(
        VmSpec spec,
        CancellationToken ct = default);

    /// <summary>
    /// Return all online nodes scored against this spec, including rejected
    /// ones with their rejection reason. Used for diagnostics and dashboards.
    /// </summary>
    Task<List<ScoredNode>> GetScoredNodesForVmAsync(
        VmSpec spec,
        CancellationToken ct = default);

    /// <summary>
    /// Validate that a specific node can host the given VM spec.
    /// Returns null when the node is eligible, or a human-readable rejection
    /// reason when any hard filter fails.
    ///
    /// Used for user-targeted deployments (marketplace node selection)
    /// through the same hard filter chain as normal scheduling.
    /// </summary>
    Task<string?> ValidateNodeForVmAsync(
        Node node,
        VmSpec spec,
        CancellationToken ct = default);
}