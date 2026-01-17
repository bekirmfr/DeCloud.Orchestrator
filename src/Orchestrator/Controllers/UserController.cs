using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly IUserService _userService;
    private readonly IVmService _vmService;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IUserService userService,
        IVmService vmService,
        ILogger<UserController> logger)
    {
        _userService = userService;
        _vmService = vmService;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    /// <summary>
    /// Get current user profile
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<ApiResponse<UserProfileResponse>>> GetProfile()
    {
        var userId = GetUserId();
        var user = await _userService.GetUserAsync(userId);

        if (user == null)
        {
            return NotFound(ApiResponse<UserProfileResponse>.Fail("NOT_FOUND", "User not found"));
        }

        // Get VM count
        var vms = await _vmService.GetVmsByUserAsync(userId);
        var runningVms = vms.Count(v => v.Status == VmStatus.Running);

        var response = new UserProfileResponse(
            user.Id,
            user.WalletAddress,
            user.DisplayName,
            user.Email,
            user.Status,
            user.CreatedAt,
            user.LastLoginAt,
            user.Quotas,
            user.SshKeys.Select(k => new SshKeySummary(k.Id, k.Name, k.Fingerprint, k.CreatedAt)).ToList(),
            user.ApiKeys.Select(k => new ApiKeySummary(k.Id, k.Name, k.KeyPrefix, k.Scopes, k.CreatedAt, k.ExpiresAt, k.LastUsedAt)).ToList(),
            vms.Count,
            runningVms
        );

        return Ok(ApiResponse<UserProfileResponse>.Ok(response));
    }

    /// <summary>
    /// Update user profile
    /// </summary>
    [HttpPatch("me")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = GetUserId();
        var user = await _userService.GetUserAsync(userId);

        if (user == null)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "User not found"));
        }

        if (!string.IsNullOrEmpty(request.DisplayName))
            user.DisplayName = request.DisplayName;
        
        if (!string.IsNullOrEmpty(request.Email))
            user.Email = request.Email;

        await _userService.UpdateUserAsync(user);

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Get user's SSH keys
    /// </summary>
    [HttpGet("me/ssh-keys")]
    public async Task<ActionResult<ApiResponse<List<SshKey>>>> GetSshKeys()
    {
        var userId = GetUserId();
        var user = await _userService.GetUserAsync(userId);

        if (user == null)
        {
            return NotFound(ApiResponse<List<SshKey>>.Fail("NOT_FOUND", "User not found"));
        }

        return Ok(ApiResponse<List<SshKey>>.Ok(user.SshKeys));
    }

    /// <summary>
    /// Add an SSH key
    /// </summary>
    [HttpPost("me/ssh-keys")]
    public async Task<ActionResult<ApiResponse<SshKey>>> AddSshKey([FromBody] AddSshKeyRequest request)
    {
        var userId = GetUserId();
        var key = await _userService.AddSshKeyAsync(userId, request);

        if (key == null)
        {
            return BadRequest(ApiResponse<SshKey>.Fail("INVALID_KEY", "Invalid SSH key format"));
        }

        return Ok(ApiResponse<SshKey>.Ok(key));
    }

    /// <summary>
    /// Remove an SSH key
    /// </summary>
    [HttpDelete("me/ssh-keys/{keyId}")]
    public async Task<ActionResult<ApiResponse<bool>>> RemoveSshKey(string keyId)
    {
        var userId = GetUserId();
        var success = await _userService.RemoveSshKeyAsync(userId, keyId);

        if (!success)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "SSH key not found"));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Get user's API keys
    /// </summary>
    [HttpGet("me/api-keys")]
    public async Task<ActionResult<ApiResponse<List<ApiKeySummary>>>> GetApiKeys()
    {
        var userId = GetUserId();
        var user = await _userService.GetUserAsync(userId);

        if (user == null)
        {
            return NotFound(ApiResponse<List<ApiKeySummary>>.Fail("NOT_FOUND", "User not found"));
        }

        var keys = user.ApiKeys.Select(k => new ApiKeySummary(
            k.Id, k.Name, k.KeyPrefix, k.Scopes, k.CreatedAt, k.ExpiresAt, k.LastUsedAt
        )).ToList();

        return Ok(ApiResponse<List<ApiKeySummary>>.Ok(keys));
    }

    /// <summary>
    /// Create an API key
    /// </summary>
    [HttpPost("me/api-keys")]
    public async Task<ActionResult<ApiResponse<CreateApiKeyResponse>>> CreateApiKey(
        [FromBody] CreateApiKeyRequest request)
    {
        var userId = GetUserId();
        var response = await _userService.CreateApiKeyAsync(userId, request);

        if (response == null)
        {
            return BadRequest(ApiResponse<CreateApiKeyResponse>.Fail("CREATE_FAILED", "Failed to create API key"));
        }

        return Ok(ApiResponse<CreateApiKeyResponse>.Ok(response));
    }

    /// <summary>
    /// Delete an API key
    /// </summary>
    [HttpDelete("me/api-keys/{keyId}")]
    public async Task<ActionResult<ApiResponse<bool>>> DeleteApiKey(string keyId)
    {
        var userId = GetUserId();
        var user = await _userService.GetUserAsync(userId);

        if (user == null)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "User not found"));
        }

        var key = user.ApiKeys.FirstOrDefault(k => k.Id == keyId);
        if (key == null)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "API key not found"));
        }

        user.ApiKeys.Remove(key);
        await _userService.UpdateUserAsync(user);

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Get user's quotas and usage
    /// </summary>
    [HttpGet("me/quotas")]
    public async Task<ActionResult<ApiResponse<UserQuotas>>> GetQuotas()
    {
        var userId = GetUserId();
        var user = await _userService.GetUserAsync(userId);

        if (user == null)
        {
            return NotFound(ApiResponse<UserQuotas>.Fail("NOT_FOUND", "User not found"));
        }

        return Ok(ApiResponse<UserQuotas>.Ok(user.Quotas));
    }
}

// Response DTOs
public record UserProfileResponse(
    string Id,
    string WalletAddress,
    string? DisplayName,
    string? Email,
    UserStatus Status,
    DateTime CreatedAt,
    DateTime LastLoginAt,
    UserQuotas Quotas,
    List<SshKeySummary> SshKeys,
    List<ApiKeySummary> ApiKeys,
    int TotalVms,
    int RunningVms
);

public record SshKeySummary(string Id, string Name, string Fingerprint, DateTime CreatedAt);
public record ApiKeySummary(string Id, string Name, string KeyPrefix, List<string> Scopes, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt);
public record UpdateProfileRequest(string? DisplayName, string? Email);
