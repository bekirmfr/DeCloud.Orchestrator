using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Interfaces;
using Orchestrator.Models;

namespace Orchestrator.Controllers;

/// <summary>
/// Admin abuse-report queue + resolution. Admin-only ("Admin", matching the seeded role and
/// the compliance surface). Reads the open queue with each target's enforcement history, and
/// resolves a report: dismiss / warn (close without withholding service) or takedown (suspend
/// the target wallet through IEnforcementService, linked to the report via the enforcement
/// action's Reference). Enforcement lives entirely in IEnforcementService — this controller
/// only routes an admin decision to it.
/// </summary>
[ApiController]
[Route("api/admin/abuse")]
[Authorize(Roles = "Admin")]
public partial class AdminAbuseController : ControllerBase
{
    private readonly IAbuseReportService _reports;
    private readonly IEnforcementService _enforcement;
    private readonly ILogger<AdminAbuseController> _logger;

    public AdminAbuseController(
        IAbuseReportService reports, IEnforcementService enforcement, ILogger<AdminAbuseController> logger)
    {
        _reports = reports;
        _enforcement = enforcement;
        _logger = logger;
    }

    private string Actor() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "admin";

    [GeneratedRegex("^0x[a-fA-F0-9]{40}$")]
    private static partial Regex WalletRegex();
    private static bool ValidWallet(string? w) => !string.IsNullOrWhiteSpace(w) && WalletRegex().IsMatch(w);

    /// <summary>The open queue, most urgent first, each item carrying the target wallet's
    /// enforcement history (prior suspensions/blocks) so the admin has context in one place.</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<AbuseQueueItem>>>> Queue(CancellationToken ct)
    {
        var open = await _reports.GetOpenQueueAsync(200, ct);

        // One history lookup per distinct target wallet (several reports often name the same
        // actor), then map back onto each report.
        var wallets = open.Where(r => !string.IsNullOrEmpty(r.TargetWallet))
                          .Select(r => r.TargetWallet!).Distinct().ToList();
        var historyByWallet = new Dictionary<string, List<EnforcementAction>>();
        foreach (var w in wallets)
            historyByWallet[w] = await _enforcement.GetActionsAsync(w, ct);

        var items = open.Select(r => new AbuseQueueItem(
            r,
            r.TargetWallet != null && historyByWallet.TryGetValue(r.TargetWallet, out var h)
                ? h : new List<EnforcementAction>())).ToList();

        return Ok(ApiResponse<List<AbuseQueueItem>>.Ok(items));
    }

    /// <summary>Resolve one report. action ∈ { dismiss, warn, takedown }.</summary>
    [HttpPost("{reference}/resolve")]
    public async Task<ActionResult<ApiResponse<AbuseResolution>>> Resolve(
        string reference, [FromBody] ResolveRequest req, CancellationToken ct)
    {
        var report = await _reports.GetByReferenceAsync(reference, ct);
        if (report == null)
            return NotFound(ApiResponse<AbuseResolution>.Fail("NOT_FOUND", "No such report."));
        if (report.Status != AbuseReportStatus.Open)
            return Conflict(ApiResponse<AbuseResolution>.Fail("ALREADY_RESOLVED", "This report is already resolved."));
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(ApiResponse<AbuseResolution>.Fail("REASON_REQUIRED", "A reason is required."));

        var action = (req.Action ?? "").Trim().ToLowerInvariant();
        EnforcementResult? enforcement = null;
        AbuseReportStatus newStatus;

        switch (action)
        {
            case "dismiss":
            case "warn":
                // No service is withheld either way; the report records which decision it was
                // (in the note). "warn" does not notify the reported party — there is no
                // notification mechanism in the system to reuse, and one isn't invented here.
                newStatus = AbuseReportStatus.Dismissed;
                break;

            case "takedown":
                // Withhold service on the target wallet, linked to this report via the
                // enforcement action's Reference. Needs a valid target wallet — from the
                // report, or supplied by the admin if the report didn't capture one.
                var wallet = ValidWallet(req.TargetWallet) ? req.TargetWallet : report.TargetWallet;
                if (!ValidWallet(wallet))
                    return BadRequest(ApiResponse<AbuseResolution>.Fail("WALLET_REQUIRED",
                        "Takedown needs a valid target wallet; this report has none, so supply one."));

                enforcement = await _enforcement.SuspendAsync(
                    wallet!, req.Reason, Actor(), reference: report.Reference, ct: ct);
                if (!enforcement.Success)
                    return BadRequest(ApiResponse<AbuseResolution>.Fail(enforcement.Error!, enforcement.Message!));

                newStatus = AbuseReportStatus.Actioned;
                break;

            default:
                return BadRequest(ApiResponse<AbuseResolution>.Fail("INVALID_ACTION",
                    "Action must be dismiss, warn, or takedown."));
        }

        var updated = await _reports.ResolveAsync(reference, newStatus, Actor(), $"{action}: {req.Reason}", ct);
        if (updated == null)
            return NotFound(ApiResponse<AbuseResolution>.Fail("NOT_FOUND", "No such report."));

        _logger.LogInformation("Abuse report {Reference} resolved: {Action} by {Actor}",
            report.Reference, action, Actor());

        return Ok(ApiResponse<AbuseResolution>.Ok(
            new AbuseResolution(updated.Reference, updated.Status.ToString(), enforcement?.AffectedVms ?? 0)));
    }
}

public record ResolveRequest(string Action, string Reason, string? TargetWallet);
public record AbuseQueueItem(AbuseReport Report, List<EnforcementAction> TargetHistory);
public record AbuseResolution(string Reference, string Status, int AffectedVms);