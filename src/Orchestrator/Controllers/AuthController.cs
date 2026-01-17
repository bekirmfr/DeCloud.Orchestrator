using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IUserService userService, ILogger<AuthController> logger)
    {
        _userService = userService;
        _logger = logger;
    }

    /// <summary>
    /// Authenticate with a crypto wallet signature
    /// </summary>
    [HttpPost("wallet")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> AuthenticateWithWallet(
        [FromBody] WalletAuthRequest request)
    {
        try
        {
            var response = await _userService.AuthenticateWithWalletAsync(request);
            
            if (response == null)
            {
                return Unauthorized(ApiResponse<AuthResponse>.Fail(
                    "AUTH_FAILED", 
                    "Authentication failed. Invalid signature or timestamp."));
            }

            return Ok(ApiResponse<AuthResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during wallet authentication");
            return BadRequest(ApiResponse<AuthResponse>.Fail("AUTH_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Refresh access token
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResponse<AuthResponse>>> RefreshToken(
        [FromBody] RefreshTokenRequest request)
    {
        var response = await _userService.RefreshTokenAsync(request.RefreshToken);
        
        if (response == null)
        {
            return Unauthorized(ApiResponse<AuthResponse>.Fail(
                "INVALID_REFRESH_TOKEN", 
                "Invalid or expired refresh token"));
        }

        return Ok(ApiResponse<AuthResponse>.Ok(response));
    }

    /// <summary>
    /// Get a message to sign for authentication
    /// </summary>
    [HttpGet("message")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<AuthMessageResponse>> GetAuthMessage([FromQuery] string walletAddress)
    {
        if (string.IsNullOrEmpty(walletAddress))
        {
            return BadRequest(ApiResponse<AuthMessageResponse>.Fail(
                "INVALID_WALLET", 
                "Wallet address is required"));
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var message = $"Sign this message to authenticate with Orchestrator.\n\nWallet: {walletAddress}\nTimestamp: {timestamp}\nNonce: {Guid.NewGuid():N}";

        return Ok(ApiResponse<AuthMessageResponse>.Ok(new AuthMessageResponse(message, timestamp)));
    }
}

public record RefreshTokenRequest(string RefreshToken);
public record AuthMessageResponse(string Message, long Timestamp);
