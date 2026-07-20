using System.Text.Json.Serialization;

namespace Orchestrator.Models;

/// <summary>What is being reported. Drives the deterministic priority/SLA (Decision 6).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbuseCategory
{
    Csam,
    MalwareC2,
    IllegalMarketplace,
    Dmca,
    TosViolation,
    Spam
}

/// <summary>P0 (most urgent) … P4. BSON still stores the int (Mongo ignores the STJ attribute),
/// so ascending sort = most urgent first is unchanged; JSON wire is the string name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AbusePriority { P0, P1, P2, P3, P4 }

[JsonConverter(typeof(JsonStringEnumConverter))]
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

    /// <summary>
    /// Targeted CSAM hash-check results ordered against this report (Phase 6 pass 2b).
    /// Append-only, newest appended last: a re-scan adds a record, never overwrites, so
    /// clicking twice cannot erase evidence. Each record moves Ordered → Completed|Failed.
    /// Empty on every report until a scan is ordered; today every Completed reads
    /// NotScanned, because the matcher is not wired (2c).
    /// </summary>
    public List<CsamScanRecord> ScanRecords { get; set; } = new();
}

/// <summary>
/// One ordered scan and its outcome. Status and outcome are two axes, never collapsed:
/// Failed means the scan did not happen (node refused, VM running, disk missing) and
/// leaves Outcome at its NotScanned default; Unscannable means the scanner ran and could
/// not hash — a real content answer. "We couldn't check" must never read as "we checked."
/// </summary>
public class CsamScanRecord
{
    public string CommandId { get; set; } = string.Empty;
    public CsamScanStatus Status { get; set; } = CsamScanStatus.Ordered;

    /// <summary>The scanner's outcome once Completed. NotScanned until then, and forever
    /// on a Failed record. Stored as its string name for a human-readable audit trail.</summary>
    public string Outcome { get; set; } = "NotScanned";

    /// <summary>Identity+version of whatever produced the outcome — filled by the scanner,
    /// never by the caller, so 2c's real matcher names itself. "NullCsamScanner" today.</summary>
    public string? Matcher { get; set; }

    /// <summary>Present only on a Match: the offending file paths + hashes for evidence.</summary>
    public string? FileMap { get; set; }

    /// <summary>Failure detail on a Failed record (e.g. "VM is running", "disk not found").</summary>
    public string? Error { get; set; }

    public string OrderedBy { get; set; } = string.Empty;   // admin wallet
    public DateTime OrderedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public enum CsamScanStatus { Ordered, Completed, Failed }