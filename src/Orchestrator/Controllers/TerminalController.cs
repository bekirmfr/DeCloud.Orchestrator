using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Background;

namespace Orchestrator.Controllers;

/// <summary>
/// Terminal access API for the dashboard.
/// Handles ephemeral key-based terminal session setup.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TerminalController : ControllerBase
{
    private readonly ITerminalService _terminalService;
    private readonly ILogger<TerminalController> _logger;

    public TerminalController(
        ITerminalService terminalService,
        ILogger<TerminalController> logger)
    {
        _terminalService = terminalService;
        _logger = logger;
    }

    /// <summary>
    /// Request terminal access to a VM.
    /// This generates an ephemeral SSH key, injects it into the VM,
    /// and returns the credentials needed to connect via WebSocket.
    /// </summary>
    [HttpPost("vms/{vmId}/access")]
    public async Task<ActionResult<ApiResponse<TerminalAccessResponse>>> RequestAccess(
        string vmId,
        [FromBody] TerminalAccessRequest? request = null)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<TerminalAccessResponse>.Fail(
                "UNAUTHORIZED", "User not authenticated"));
        }

        _logger.LogInformation(
            "Terminal access requested for VM {VmId} by user {UserId}",
            vmId, userId);

        var result = await _terminalService.SetupTerminalAccessAsync(
            vmId,
            userId,
            request?.TtlSeconds ?? 300);

        if (!result.Success)
        {
            return BadRequest(ApiResponse<TerminalAccessResponse>.Fail(
                "TERMINAL_SETUP_FAILED", result.Error ?? "Unknown error"));
        }

        var credentials = result.Credentials!;

        return Ok(ApiResponse<TerminalAccessResponse>.Ok(new TerminalAccessResponse
        {
            SessionId = credentials.SessionId,
            WebSocketUrl = credentials.WebSocketUrl,
            NodeIp = credentials.NodeIp,
            NodePort = credentials.NodePort,
            VmIp = credentials.VmIp,
            Username = credentials.Username,
            PrivateKey = credentials.PrivateKey,
            PrivateKeyBase64 = credentials.PrivateKeyBase64,
            Password = credentials.Password,
            ExpiresAt = credentials.ExpiresAt
        }));
    }

    /// <summary>
    /// Get active terminal sessions for the current user.
    /// </summary>
    [HttpGet("sessions")]
    public async Task<ActionResult<ApiResponse<List<TerminalSessionInfo>>>> GetSessions()
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<List<TerminalSessionInfo>>.Fail(
                "UNAUTHORIZED", "User not authenticated"));
        }

        var sessions = await _terminalService.GetActiveSessionsAsync(userId);
        return Ok(ApiResponse<List<TerminalSessionInfo>>.Ok(sessions));
    }

    /// <summary>
    /// End a terminal session.
    /// </summary>
    [HttpDelete("sessions/{sessionId}")]
    public async Task<ActionResult<ApiResponse<bool>>> EndSession(string sessionId)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<bool>.Fail(
                "UNAUTHORIZED", "User not authenticated"));
        }

        var result = await _terminalService.EndSessionAsync(sessionId);
        return Ok(ApiResponse<bool>.Ok(result));
    }

    private string? GetUserId()
    {
        return User.FindFirst("sub")?.Value ??
               User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }
}

#region DTOs

public class TerminalAccessRequest
{
    /// <summary>
    /// Time-to-live for the ephemeral key in seconds. Default is 300 (5 minutes).
    /// </summary>
    public int TtlSeconds { get; init; } = 300;
}

public class TerminalAccessResponse
{
    /// <summary>
    /// Unique session identifier
    /// </summary>
    public string SessionId { get; init; } = "";

    /// <summary>
    /// WebSocket URL to connect to (without credentials)
    /// </summary>
    public string WebSocketUrl { get; init; } = "";

    /// <summary>
    /// Node IP address for direct WebSocket connection
    /// </summary>
    public string NodeIp { get; init; } = "";

    /// <summary>
    /// Node port for the SSH proxy
    /// </summary>
    public int NodePort { get; init; }

    /// <summary>
    /// VM's private IP address
    /// </summary>
    public string VmIp { get; init; } = "";

    /// <summary>
    /// SSH username
    /// </summary>
    public string Username { get; init; } = "root";

    /// <summary>
    /// Ephemeral private key (PEM format) - use this for SSH connection.
    /// This key expires after TTL seconds.
    /// </summary>
    public string PrivateKey { get; init; } = "";

    /// <summary>
    /// Base64-encoded private key for easy transport
    /// </summary>
    public string PrivateKeyBase64 { get; init; } = "";

    public string? Password { get; init; }

    /// <summary>
    /// When the ephemeral key expires
    /// </summary>
    public DateTime ExpiresAt { get; init; }
}

#endregion