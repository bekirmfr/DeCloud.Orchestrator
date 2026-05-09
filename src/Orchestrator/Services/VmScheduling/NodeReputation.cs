using Orchestrator.Models;

namespace Orchestrator.Services.VmScheduling;

/// <summary>
/// Single source of truth for the node reputation-score formula.
///
/// <para>Used by three sites that must not drift:</para>
/// <list type="bullet">
///   <item><c>VmSchedulingService.ApplyHardFiltersAsync</c> — FILTER 4f
///         hard threshold (legacy compat path, retained for VMs persisted
///         before Phase B+.2 lowering shipped).</item>
///   <item><c>VmSchedulingService.CalculateNodeScores</c> — soft scoring
///         component (weight 0.20 in the default scoring config).</item>
///   <item><c>ConstraintEvaluator</c> — extractor for the
///         <c>node.reputationScore</c> constraint target (FILTER 10).</item>
/// </list>
///
/// <para>
/// Earlier this lived as a private static helper inside
/// <c>VmSchedulingService</c>. Phase B+.2 promoted it to its own static
/// class so the constraint evaluator can call it without importing the
/// scheduler's internals, and so the formula has exactly one home.
/// </para>
/// </summary>
public static class NodeReputation
{
    /// <summary>
    /// Compute a node's reputation score in <c>[0.0, 1.0]</c>.
    ///
    /// <para>Formula: <c>(uptimePercent × 0.7) + (successRate × 0.3)</c>.</para>
    ///
    /// <para>
    /// New nodes (<c>TotalVmsHosted == 0</c>) score <c>0.5</c> on the success
    /// component — neutral, neither rewarded nor penalised. Without this
    /// neutralisation a brand-new node would score <c>0</c> on the success
    /// term and never get scheduling traffic to build a track record.
    /// </para>
    ///
    /// <para>
    /// The 0.7 / 0.3 weighting reflects that uptime is a 30-day rolling
    /// measurement of operational reliability while successRate accumulates
    /// over a node's lifetime. Uptime carries more weight because it's the
    /// more decision-relevant signal — a node that was reliable last year
    /// but is unreliable now should score low.
    /// </para>
    /// </summary>
    public static double Compute(Node node)
    {
        var uptimeComponent = node.UptimePercentage / 100.0;
        var successComponent = node.TotalVmsHosted > 0
            ? (double)node.SuccessfulVmCompletions / node.TotalVmsHosted
            : 0.5;
        return (uptimeComponent * 0.7) + (successComponent * 0.3);
    }
}