using Orchestrator.Models;

namespace Orchestrator.Interfaces.VmScheduling;

/// <summary>
/// Interface for VM scheduling service
/// </summary>
public interface IVmSchedulingService
{
    /// <summary>
    /// Select the best node for a VM with optional region/zone preferences
    /// </summary>
    Task<Node?> SelectBestNodeForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        string? preferredRegion = null,
        string? preferredZone = null,
        string? requiredArchitecture = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get all available nodes for a VM with filtering options
    /// </summary>
    Task<List<Node>> GetAvailableNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        string? regionFilter = null,
        string? zoneFilter = null,
        string? architectureFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get scored nodes for a VM with detailed scoring information
    /// </summary>
    Task<List<ScoredNode>> GetScoredNodesForVmAsync(
        VmSpec spec,
        QualityTier tier = QualityTier.Standard,
        string? preferredRegion = null,
        string? preferredZone = null,
        string? requiredArchitecture = null,
        CancellationToken ct = default);

    /// <summary>
    /// Validate that a specific node can host the given VM spec.
    /// Returns null when the node is eligible, or a human-readable rejection
    /// reason when any hard filter fails.
    ///
    /// Used to validate user-targeted deployments (marketplace node selection)
    /// through the same hard filters applied during normal scheduling — so the
    /// rules are never duplicated and never drift.
    /// </summary>
    Task<string?> ValidateNodeForVmAsync(
        Node node,
        VmSpec spec,
        QualityTier tier,
        string? requiredArchitecture = null,
        string? requiredRegion = null,
        string? requiredZone = null,
        CancellationToken ct = default);
}
