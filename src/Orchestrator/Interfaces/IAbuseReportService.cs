using Orchestrator.Models;

namespace Orchestrator.Interfaces;

/// <summary>
/// Intake + storage for abuse reports. Self-contained (owns "abuse_reports" and the
/// per-year sequence). Does NOT enforce — enforcement is IEnforcementService.
/// </summary>
public interface IAbuseReportService
{
    /// <summary>True when backed by a database. Intake requires persistence, so a report
    /// is never silently accepted-and-dropped.</summary>
    bool Available { get; }

    /// <summary>File a report: assign a reference (ABU-YYYY-NNNNN), derive priority/SLA from
    /// the category, persist, and return the stored report.</summary>
    Task<AbuseReport> SubmitAsync(
        AbuseCategory category, string reportedResource, string description,
        string? targetWallet, string? reporterContact, CancellationToken ct = default);

    /// <summary>Open reports, most urgent first (priority ascending, then oldest first).</summary>
    Task<List<AbuseReport>> GetOpenQueueAsync(int limit = 200, CancellationToken ct = default);

    /// <summary>One report by its reference, or null.</summary>
    Task<AbuseReport?> GetByReferenceAsync(string reference, CancellationToken ct = default);

    /// <summary>Record an admin resolution (status + who/when/note) on a report. Returns the
    /// updated report, or null if not found.</summary>
    Task<AbuseReport?> ResolveAsync(string reference, AbuseReportStatus status,
        string resolvedBy, string note, CancellationToken ct = default);
}