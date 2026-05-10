using Orchestrator.Models;

namespace Orchestrator.Interfaces.VmScheduling;

/// <summary>
/// Scheduling interface for VM node selection and eligibility checking.
///
/// All scheduling constraints (architecture, locality, reputation, GPU,
/// jurisdiction, country, zone) are expressed via <c>spec.Constraints</c>
/// and evaluated through FILTER 10 in <c>ApplyHardFiltersAsync</c>.
/// No legacy per-parameter overrides exist — the constraint vocabulary
/// is the single mechanism.
/// </summary>
public interface IVmSchedulingService
{
    /// <summary>
    /// Select the best eligible node for a VM.
    /// All scheduling requirements must be in <c>spec.Constraints</c>
    /// before calling — see <c>VmService.LowerLegacyFieldsToConstraints</c>.
    /// </summary>
    Task<Node?> SelectBestNodeForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        CancellationToken ct = default);

    /// <summary>
    /// Return all nodes that pass the hard filter chain for this spec.
    /// </summary>
    Task<List<Node>> GetAvailableNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        CancellationToken ct = default);

    /// <summary>
    /// Return all online nodes scored against this spec, including rejected
    /// ones with their rejection reason. Used for diagnostics and dashboards.
    /// </summary>
    Task<List<ScoredNode>> GetScoredNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
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
        QualityTier tier,
        CancellationToken ct = default);
}