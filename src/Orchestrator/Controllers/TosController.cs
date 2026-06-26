using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Interfaces;
using System.Security.Claims;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TosController : ControllerBase
{
    private readonly ITosService _tos;
    private readonly IUserService _userService;
    private readonly ILogger<TosController> _logger;

    public TosController(ITosService tos, IUserService userService, ILogger<TosController> logger)
    {
        _tos = tos;
        _userService = userService;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    /// <summary>
    /// Current Terms of Service: version, hash, and full text. Public — the
    /// client needs it to display the terms and build the message to sign.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public ActionResult<ApiResponse<TosDocument>> GetCurrent()
        => Ok(ApiResponse<TosDocument>.Ok(_tos.GetCurrent()));

    /// <summary>
    /// Whether the authenticated wallet has accepted the current ToS version.
    /// The client uses this to decide whether to prompt for (re-)acceptance.
    /// </summary>
    [HttpGet("status")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<TosStatusResponse>>> GetStatus(CancellationToken ct)
    {
        var wallet = GetUserId();
        if (string.IsNullOrEmpty(wallet))
            return Unauthorized(ApiResponse<TosStatusResponse>.Fail("UNAUTHORIZED", "Not authenticated"));

        var current = _tos.GetCurrent();
        var accepted = await _tos.HasAcceptedCurrentAsync(wallet, ct);
        return Ok(ApiResponse<TosStatusResponse>.Ok(
            new TosStatusResponse(current.Version, current.Hash, accepted)));
    }

    /// <summary>
    /// Record a wallet-signed acceptance of the current ToS.
    ///
    /// Security: the canonical message is reconstructed server-side from the
    /// CURRENT version + hash and the authenticated wallet, then the signature is
    /// verified to recover that same wallet. A client cannot accept on behalf of
    /// another wallet, nor accept a stale/substituted document.
    /// </summary>
    [HttpPost("accept")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<TosStatusResponse>>> Accept(
        [FromBody] TosAcceptRequest request, CancellationToken ct)
    {
        var wallet = GetUserId();
        if (string.IsNullOrEmpty(wallet))
            return Unauthorized(ApiResponse<TosStatusResponse>.Fail("UNAUTHORIZED", "Not authenticated"));

        var current = _tos.GetCurrent();

        // The submitted acceptance must target the document currently in effect.
        if (!string.Equals(request.Version, current.Version, StringComparison.Ordinal) ||
            !string.Equals(request.Hash, current.Hash, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(ApiResponse<TosStatusResponse>.Fail(
                "TOS_VERSION_MISMATCH",
                "Submitted ToS version/hash is not current. Reload the latest terms and re-sign."));
        }

        // Reconstruct the canonical message from trusted inputs + client timestamp,
        // then verify the signature recovers the authenticated wallet.
        var message = _tos.BuildAcceptanceMessage(wallet, request.Timestamp);
        if (!_userService.VerifyWalletSignature(wallet, message, request.Signature))
        {
            return Unauthorized(ApiResponse<TosStatusResponse>.Fail(
                "INVALID_SIGNATURE", "Signature does not match the authenticated wallet."));
        }

        await _tos.RecordAcceptanceAsync(wallet, request.Signature, request.Timestamp, ct);
        _logger.LogInformation("ToS {Version} accepted by {Wallet}", current.Version, wallet);

        return Ok(ApiResponse<TosStatusResponse>.Ok(
            new TosStatusResponse(current.Version, current.Hash, true)));
    }
}

public record TosAcceptRequest(string Version, string Hash, long Timestamp, string Signature);
public record TosStatusResponse(string Version, string Hash, bool Accepted);
