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
}