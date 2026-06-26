using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Interfaces;
using Orchestrator.Models;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    // The refresh token lives ONLY in this httpOnly cookie — never in the
    // response body, never readable by JavaScript. Scoped to /api/auth so it is
    // sent only to the auth endpoints that need it.
    private const string RefreshCookieName = "dc_rt";
    private const string RefreshCookiePath = "/api/auth";
    // Keep in sync with UserService.RefreshTokenExpiryDays.
    private static readonly TimeSpan RefreshCookieLifetime = TimeSpan.FromDays(7);

    private readonly IUserService _userService;
    private readonly IJwtRevocationService _jwtRevocation;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IUserService userService,
        IJwtRevocationService jwtRevocation,
        ILogger<AuthController> logger)
    {
        _userService = userService;
        _jwtRevocation = jwtRevocation;
        _logger = logger;
    }

    /// <summary>
    /// Issue a single-use nonce for Sign-In With Ethereum (EIP-4361).
    /// The client (AppKit) embeds this nonce in the message it asks the wallet
    /// to sign; /wallet then consumes it exactly once.
    /// </summary>
    [HttpGet("nonce")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<NonceResponse>>> GetNonce(
        [FromServices] INonceStore nonceStore, CancellationToken ct)
    {
        var nonce = await nonceStore.IssueAsync(ct);
        return Ok(ApiResponse<NonceResponse>.Ok(new NonceResponse(nonce)));
    }

    /// <summary>
    /// Verify a signed SIWE message and establish a session.
    /// On success: sets the refresh token as an httpOnly cookie and returns the
    /// short-lived access token in the body.
    /// </summary>
    [HttpPost("wallet")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> AuthenticateWithWallet(
        [FromBody] WalletAuthRequest request)
    {
        try
        {
            var auth = await _userService.AuthenticateWithWalletAsync(request);
            if (auth == null)
            {
                return Unauthorized(ApiResponse<SessionResponse>.Fail(
                    "AUTH_FAILED",
                    "Authentication failed. Invalid signature, or nonce missing/expired/already used."));
            }

            SetRefreshCookie(auth.RefreshToken);
            return Ok(ApiResponse<SessionResponse>.Ok(
                new SessionResponse(auth.AccessToken, auth.ExpiresAt, auth.User)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during wallet authentication");
            return BadRequest(ApiResponse<SessionResponse>.Fail("AUTH_ERROR", "Authentication error"));
        }
    }

    /// <summary>
    /// Rotate tokens using the refresh token from the httpOnly cookie.
    /// The body carries nothing — the refresh token is never exposed to JS.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> RefreshToken()
    {
        var refreshToken = Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Unauthorized(ApiResponse<SessionResponse>.Fail(
                "NO_REFRESH_TOKEN", "No refresh token present"));
        }

        var auth = await _userService.RefreshTokenAsync(refreshToken);
        if (auth == null)
        {
            // Stale/used/expired — clear the dead cookie so the client stops retrying.
            ClearRefreshCookie();
            return Unauthorized(ApiResponse<SessionResponse>.Fail(
                "INVALID_REFRESH_TOKEN", "Invalid or expired refresh token"));
        }

        SetRefreshCookie(auth.RefreshToken);
        return Ok(ApiResponse<SessionResponse>.Ok(
            new SessionResponse(auth.AccessToken, auth.ExpiresAt, auth.User)));
    }

    /// <summary>
    /// SIWE getSession: report the signed-in wallet if a valid refresh cookie
    /// exists, WITHOUT rotating it. Lets AppKit restore its signed-in UI state
    /// on reload from the same httpOnly cookie the API session uses.
    /// </summary>
    [HttpGet("session")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SessionInfo>>> GetSession()
    {
        var refreshToken = Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(refreshToken))
            return Unauthorized(ApiResponse<SessionInfo>.Fail("NO_SESSION", "No active session"));

        var wallet = await _userService.GetSessionWalletAsync(refreshToken);
        if (wallet == null)
            return Unauthorized(ApiResponse<SessionInfo>.Fail("NO_SESSION", "No active session"));

        return Ok(ApiResponse<SessionInfo>.Ok(new SessionInfo(wallet)));
    }

    /// <summary>
    /// Real server-side logout: revoke the refresh token (so it can never be
    /// rotated again) and revoke the current access token by its jti (so it is
    /// rejected immediately instead of staying valid until expiry). Clears the
    /// cookie. AllowAnonymous so logout still works after the access token has
    /// expired; if a valid bearer is present, its jti is also revoked.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<bool>>> Logout(CancellationToken ct)
    {
        var refreshToken = Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrEmpty(refreshToken))
            await _userService.LogoutAsync(refreshToken, ct);

        // Best-effort access-token revocation if a valid token was sent.
        var jti = User.FindFirst("jti")?.Value
                  ?? User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Jti)?.Value;
        var wallet = User.FindFirst("wallet")?.Value ?? "user";
        if (!string.IsNullOrEmpty(jti))
            await _jwtRevocation.RevokeAsync(jti, wallet, "user logout", ct);

        ClearRefreshCookie();
        return Ok(ApiResponse<bool>.Ok(true));
    }

    // ── Cookie helpers ──────────────────────────────────────────────────────

    private void SetRefreshCookie(string token)
    {
        Response.Cookies.Append(RefreshCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,       // set Secure whenever served over HTTPS (prod)
            SameSite = SameSiteMode.Lax,    // same-origin dashboard; see guide for cross-origin
            Path = RefreshCookiePath,
            Expires = DateTimeOffset.UtcNow.Add(RefreshCookieLifetime),
            IsEssential = true
        });
    }

    private void ClearRefreshCookie()
    {
        Response.Cookies.Append(RefreshCookieName, string.Empty, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = RefreshCookiePath,
            Expires = DateTimeOffset.UnixEpoch,
            IsEssential = true
        });
    }
}

// Response DTOs. SessionResponse deliberately omits the refresh token — it
// exists only in the httpOnly cookie.
public record NonceResponse(string Nonce);
public record SessionResponse(string AccessToken, DateTime ExpiresAt, User User);
public record SessionInfo(string Address);
