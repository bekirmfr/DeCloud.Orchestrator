using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nethereum.Util;
using Orchestrator.Interfaces;
using Orchestrator.Models;

namespace Orchestrator.Controllers;

/// <summary>
/// Public abuse-report intake. Unauthenticated by design (anyone can report), so it is
/// rate-limited per client IP and strictly validated — fail closed on malformed or oversized
/// input. Intake only: it records a report and returns a reference + SLA, and never fetches
/// the reported content. Enforcement is a separate admin action (slice 2) via IEnforcementService.
/// </summary>
[ApiController]
[Route("api/abuse")]
public sealed partial class AbuseController : ControllerBase
{
    private const int MaxResource = 512;
    private const int MaxDescription = 4000;
    private const int MaxContact = 256;

    private readonly IAbuseReportService _reports;
    private readonly ILogger<AbuseController> _logger;
    private readonly AddressUtil _addr = new();

    public AbuseController(IAbuseReportService reports, ILogger<AbuseController> logger)
    {
        _reports = reports;
        _logger = logger;
    }

    [GeneratedRegex("^0x[a-fA-F0-9]{40}$")]
    private static partial Regex WalletRegex();

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("abuse-intake")]
    [RequestSizeLimit(16 * 1024)] // 16 KB — a report is text; reject anything larger outright
    public async Task<ActionResult<ApiResponse<AbuseReceipt>>> Submit(
        [FromBody] AbuseReportRequest req, CancellationToken ct)
    {
        if (req is null)
            return BadRequest(ApiResponse<AbuseReceipt>.Fail("INVALID_BODY", "Missing report body."));

        if (!Enum.IsDefined(typeof(AbuseCategory), req.Category))
            return BadRequest(ApiResponse<AbuseReceipt>.Fail("INVALID_CATEGORY", "Unknown report category."));

        var resource = (req.ReportedResource ?? "").Trim();
        if (resource.Length == 0 || resource.Length > MaxResource)
            return BadRequest(ApiResponse<AbuseReceipt>.Fail("INVALID_RESOURCE",
                $"A reported resource is required (max {MaxResource} chars)."));

        var description = (req.Description ?? "").Trim();
        if (description.Length == 0 || description.Length > MaxDescription)
            return BadRequest(ApiResponse<AbuseReceipt>.Fail("INVALID_DESCRIPTION",
                $"A description is required (max {MaxDescription} chars)."));

        var contact = (req.ReporterContact ?? "").Trim();
        if (contact.Length > MaxContact)
            return BadRequest(ApiResponse<AbuseReceipt>.Fail("INVALID_CONTACT",
                $"Contact is too long (max {MaxContact} chars)."));

        string? targetWallet = null;
        var rawWallet = (req.TargetWallet ?? "").Trim();
        if (rawWallet.Length > 0)
        {
            if (!WalletRegex().IsMatch(rawWallet))
                return BadRequest(ApiResponse<AbuseReceipt>.Fail("INVALID_WALLET", "Malformed target wallet."));
            targetWallet = _addr.ConvertToChecksumAddress(rawWallet);
        }

        if (!_reports.Available)
            return StatusCode(503, ApiResponse<AbuseReceipt>.Fail("UNAVAILABLE",
                "Reporting is temporarily unavailable. Please try again."));

        var report = await _reports.SubmitAsync(
            req.Category, resource, description, targetWallet,
            contact.Length > 0 ? contact : null, ct);

        return Ok(ApiResponse<AbuseReceipt>.Ok(
            new AbuseReceipt(report.Reference, report.Priority, report.Sla)));
    }
}

public record AbuseReportRequest(
    AbuseCategory Category,
    string ReportedResource,
    string Description,
    string? TargetWallet,
    string? ReporterContact);

public record AbuseReceipt(string Reference, AbusePriority Priority, string Sla);