namespace Orchestrator.Models;

/// <summary>What is being reported. Drives the deterministic priority/SLA (Decision 6).</summary>
public enum AbuseCategory
{
    Csam,
    MalwareC2,
    IllegalMarketplace,
    Dmca,
    TosViolation,
    Spam
}

/// <summary>P0 (most urgent) … P4. Stored as its int value, so ascending sort = most urgent first.</summary>
public enum AbusePriority { P0, P1, P2, P3, P4 }

public enum AbuseReportStatus { Open, Actioned, Dismissed }

/// <summary>
/// Deterministic category → (priority, SLA). No model calls (Decision 6) — this is the exact
/// shape an AI triage would later emit, so it can slot in without changing consumers.
/// </summary>
public static class AbuseTriage
{
    public static readonly IReadOnlyDictionary<AbuseCategory, (AbusePriority Priority, string Sla)> Map =
        new Dictionary<AbuseCategory, (AbusePriority, string)>
        {
            [AbuseCategory.Csam] = (AbusePriority.P0, "2h"),
            [AbuseCategory.MalwareC2] = (AbusePriority.P1, "4h"),
            [AbuseCategory.IllegalMarketplace] = (AbusePriority.P1, "8h"),
            [AbuseCategory.Dmca] = (AbusePriority.P2, "48h"),
            [AbuseCategory.TosViolation] = (AbusePriority.P3, "72h"),
            [AbuseCategory.Spam] = (AbusePriority.P4, "best-effort"),
        };
}

/// <summary>
/// A filed abuse report. Intake record only — an admin resolves it later (slice 2) and any
/// enforcement runs through IEnforcementService, which writes its own audit row.
/// </summary>
public class AbuseReport
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Human reference returned to the reporter, ABU-YYYY-NNNNN.</summary>
    public string Reference { get; set; } = string.Empty;

    public AbuseCategory Category { get; set; }
    public AbusePriority Priority { get; set; }

    /// <summary>Informational SLA target for the reporter (e.g. "2h", "best-effort").</summary>
    public string Sla { get; set; } = string.Empty;

    /// <summary>Free-text pointer to what's being reported (template slug, VM id, wallet, URL). Capped.</summary>
    public string ReportedResource { get; set; } = string.Empty;

    /// <summary>Optional checksum wallet, when identifiable — lets the admin queue join the
    /// wallet's existing enforcement history (slice 2). Never required.</summary>
    public string? TargetWallet { get; set; }

    /// <summary>Reporter's description. Capped.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Optional contact for follow-up. Anonymous reports are allowed. Capped.</summary>
    public string? ReporterContact { get; set; }

    public AbuseReportStatus Status { get; set; } = AbuseReportStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Resolution (set in slice 2)
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
}