using Orchestrator.Models;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Result of an eligibility check for a single system VM role.
/// Carries the decision and all reasons, so callers can log precisely
/// why a node did or did not qualify — without re-deriving the logic.
/// </summary>
public sealed class EligibilityResult
{
    public bool IsEligible { get; }

    /// <summary>
    /// Human-readable reasons a node failed eligibility.
    /// Empty when IsEligible is true.
    /// </summary>
    public IReadOnlyList<string> FailureReasons { get; }

    private EligibilityResult(bool eligible, IReadOnlyList<string> reasons)
    {
        IsEligible = eligible;
        FailureReasons = reasons;
    }

    public static EligibilityResult Eligible() =>
        new(true, Array.Empty<string>());

    public static EligibilityResult Ineligible(params string[] reasons) =>
        new(false, reasons);

    public static EligibilityResult Ineligible(IEnumerable<string> reasons) =>
        new(false, reasons.ToArray());

    /// <summary>
    /// Returns a summary string suitable for log output.
    /// </summary>
    public override string ToString() =>
        IsEligible
            ? "Eligible"
            : $"Ineligible: {string.Join("; ", FailureReasons)}";
}

/// <summary>
/// Determines which system VM roles a node qualifies for, and why.
///
/// Each Check* method evaluates the hardware and network requirements for
/// one role and returns a detailed EligibilityResult. ComputeObligations
/// aggregates those results into the final obligation list.
///
/// This interface is the single source of truth for eligibility thresholds.
/// All other services (RelayNodeService, future InferenceNodeService, etc.)
/// must delegate to this interface instead of maintaining their own copies
/// of the same constants.
/// </summary>
public interface IObligationEligibility
{
    // ── Per-role checks ───────────────────────────────────────────────────

    /// <summary>
    /// DHT is universal — every node qualifies.
    /// The check exists so the dependency graph can reference it uniformly.
    /// </summary>
    EligibilityResult CheckDht(Node node);

    /// <summary>
    /// Relay requires a public IP, ≥2 physical cores, ≥4 GB RAM,
    /// and ≥50 Mbps bandwidth (when measured).
    /// </summary>
    EligibilityResult CheckRelay(Node node);

    /// <summary>
    /// BlockStore requires ≥100 GB total storage and ≥2 GB RAM.
    /// </summary>
    EligibilityResult CheckBlockStore(Node node);

    /// <summary>
    /// Inference requires a CUDA-capable GPU with ≥8 GB VRAM.
    /// Placeholder for future GPU workload scheduling.
    /// </summary>
    EligibilityResult CheckInference(Node node);

    // ── Aggregate ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the ordered list of roles this node is obligated to run,
    /// based on its current hardware inventory.
    /// </summary>
    List<SystemVmRole> ComputeObligations(Node node);
}
