namespace Orchestrator.Models;

/// <summary>
/// Per-template author earnings, summed from settled template-fee usage records.
/// <para>Net is the author's actual cut (<c>UsageRecord.NodeShare</c>, after the
/// platform fee); Gross is what deployers paid (<c>UsageRecord.TotalCost</c>).</para>
/// Read from the settlement ledger rather than a stored counter, so it cannot drift
/// from on-chain reality and needs no backfill.
/// </summary>
public class TemplateEarnings
{
    /// <summary>Author's cut after the platform fee (sum of NodeShare).</summary>
    public decimal Net { get; set; }

    /// <summary>Total paid by deployers before the platform fee (sum of TotalCost).</summary>
    public decimal Gross { get; set; }

    /// <summary>Number of paid deployments that settled a fee.</summary>
    public int Deploys { get; set; }
}
