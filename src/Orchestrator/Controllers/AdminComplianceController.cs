using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Interfaces;
using Orchestrator.Models;

namespace Orchestrator.Controllers;

/// <summary>
/// Admin compliance / takedown surface. Suspend or block a wallet, reverse either, and
/// read the denylist and the enforcement audit log. Admin-only — uses "Admin" (capital
/// A) matching the role seeded by AdminUserInitializer and the JWT role claim — lowercase
/// "admin" is an exact-comparison mismatch that fails closed and is invisible until an
/// admin tries the endpoint (see Decision 8; all known sites fixed 2026-07-10/16).
/// Enforcement is withhold-of-service only: no endpoint touches funds.
/// </summary>
[ApiController]
[Route("api/admin/compliance")]
[Authorize(Roles = "Admin")]
public partial class AdminComplianceController : ControllerBase
{
    private readonly IEnforcementService _enforcement;
    private readonly ILogger<AdminComplianceController> _logger;

    public AdminComplianceController(IEnforcementService enforcement, ILogger<AdminComplianceController> logger)
    {
        _enforcement = enforcement;
        _logger = logger;
    }

    private string Actor() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "admin";

    [GeneratedRegex("^0x[a-fA-F0-9]{40}$")]
    private static partial Regex WalletRegex();
    private static bool ValidWallet(string? w) => !string.IsNullOrWhiteSpace(w) && WalletRegex().IsMatch(w);

    // ── Suspension (platform-originated) ─────────────────────────────────────

    [HttpPost("suspend")]
    public async Task<ActionResult<ApiResponse<EnforcementResult>>> Suspend([FromBody] SuspendRequest req, CancellationToken ct)
    {
        if (!ValidWallet(req.Wallet))
            return BadRequest(ApiResponse<EnforcementResult>.Fail("INVALID_WALLET", "Malformed wallet address."));
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(ApiResponse<EnforcementResult>.Fail("REASON_REQUIRED", "A reason is required."));

        var result = await _enforcement.SuspendAsync(req.Wallet, req.Reason, Actor(), ct: ct);
        return result.Success
            ? Ok(ApiResponse<EnforcementResult>.Ok(result))
            : BadRequest(ApiResponse<EnforcementResult>.Fail(result.Error!, result.Message!));
    }

    [HttpPost("unsuspend")]
    public async Task<ActionResult<ApiResponse<EnforcementResult>>> Unsuspend([FromBody] SuspendRequest req, CancellationToken ct)
    {
        if (!ValidWallet(req.Wallet))
            return BadRequest(ApiResponse<EnforcementResult>.Fail("INVALID_WALLET", "Malformed wallet address."));

        var result = await _enforcement.UnsuspendAsync(req.Wallet, req.Reason ?? "", Actor(), ct);
        return result.Success
            ? Ok(ApiResponse<EnforcementResult>.Ok(result))
            : BadRequest(ApiResponse<EnforcementResult>.Fail(result.Error!, result.Message!));
    }

    // ── Single-VM control ────────────────────────────────────────────────────

    [HttpPost("suspend-vm")]
    public async Task<ActionResult<ApiResponse<EnforcementResult>>> SuspendVm([FromBody] VmComplianceRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.VmId))
            return BadRequest(ApiResponse<EnforcementResult>.Fail("VM_ID_REQUIRED", "A VM id is required."));
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(ApiResponse<EnforcementResult>.Fail("REASON_REQUIRED", "A reason is required."));

        var result = await _enforcement.SuspendVmAsync(req.VmId, req.Reason, Actor(), ct: ct);
        return result.Success
            ? Ok(ApiResponse<EnforcementResult>.Ok(result))
            : BadRequest(ApiResponse<EnforcementResult>.Fail(result.Error!, result.Message!));
    }

    [HttpPost("resume-vm")]
    public async Task<ActionResult<ApiResponse<EnforcementResult>>> ResumeVm([FromBody] VmComplianceRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.VmId))
            return BadRequest(ApiResponse<EnforcementResult>.Fail("VM_ID_REQUIRED", "A VM id is required."));

        var result = await _enforcement.ResumeVmAsync(req.VmId, req.Reason ?? "", Actor(), ct);
        return result.Success
            ? Ok(ApiResponse<EnforcementResult>.Ok(result))
            : BadRequest(ApiResponse<EnforcementResult>.Fail(result.Error!, result.Message!));
    }

    // ── Operator-node immediate cutoff (override the graceful drain) ──────────

    [HttpPost("cutoff")]
    public async Task<ActionResult<ApiResponse<EnforcementResult>>> Cutoff([FromBody] SuspendRequest req, CancellationToken ct)
    {
        if (!ValidWallet(req.Wallet))
            return BadRequest(ApiResponse<EnforcementResult>.Fail("INVALID_WALLET", "Malformed wallet address."));
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(ApiResponse<EnforcementResult>.Fail("REASON_REQUIRED", "A reason is required."));

        var result = await _enforcement.CutoffOperatorNodesNowAsync(req.Wallet, req.Reason, Actor(), ct);
        return result.Success
            ? Ok(ApiResponse<EnforcementResult>.Ok(result))
            : BadRequest(ApiResponse<EnforcementResult>.Fail(result.Error!, result.Message!));
    }

    // ── Denylist (provenance-bearing) ────────────────────────────────────────

    [HttpPost("block")]
    public async Task<ActionResult<ApiResponse<bool>>> Block([FromBody] BlockRequest req, CancellationToken ct)
    {
        if (!ValidWallet(req.Wallet))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_WALLET", "Malformed wallet address."));
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(ApiResponse<bool>.Fail("REASON_REQUIRED", "A reason is required."));

        await _enforcement.BlockAsync(req.Wallet, req.Source, req.Reason, req.Reference, Actor(), ct);
        return Ok(ApiResponse<bool>.Ok(true));
    }

    [HttpPost("unblock")]
    public async Task<ActionResult<ApiResponse<bool>>> Unblock([FromBody] BlockRequest req, CancellationToken ct)
    {
        if (!ValidWallet(req.Wallet))
            return BadRequest(ApiResponse<bool>.Fail("INVALID_WALLET", "Malformed wallet address."));

        var removed = await _enforcement.UnblockAsync(req.Wallet, req.Source, req.Reason ?? "", Actor(), ct);
        return removed
            ? Ok(ApiResponse<bool>.Ok(true))
            : NotFound(ApiResponse<bool>.Fail("NOT_BLOCKED", "No denylist entry for that wallet and source."));
    }

    [HttpPost("block/bulk")]
    public async Task<ActionResult<ApiResponse<int>>> BulkBlock([FromBody] BulkBlockRequest req, CancellationToken ct)
    {
        if (req.Wallets == null || req.Wallets.Count == 0)
            return BadRequest(ApiResponse<int>.Fail("NO_WALLETS", "Provide at least one wallet."));
        if (string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(ApiResponse<int>.Fail("REASON_REQUIRED", "A reason is required."));

        var count = await _enforcement.BulkImportBlocksAsync(req.Wallets, req.Source, req.Reason, Actor(), ct);
        return Ok(ApiResponse<int>.Ok(count));
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    [HttpGet("blocks")]
    public async Task<ActionResult<ApiResponse<List<BlockedWallet>>>> Blocks([FromQuery] string? wallet, CancellationToken ct)
        => Ok(ApiResponse<List<BlockedWallet>>.Ok(await _enforcement.ListBlocksAsync(wallet, ct)));

    [HttpGet("actions")]
    public async Task<ActionResult<ApiResponse<List<EnforcementAction>>>> Actions([FromQuery] string? wallet, CancellationToken ct)
        => Ok(ApiResponse<List<EnforcementAction>>.Ok(await _enforcement.GetActionsAsync(wallet, ct)));
}

public record SuspendRequest(string Wallet, string? Reason);
public record VmComplianceRequest(string VmId, string? Reason);
public record BlockRequest(string Wallet, BlockSource Source, string Reason, string? Reference);
public record BulkBlockRequest(List<string> Wallets, BlockSource Source, string Reason);
