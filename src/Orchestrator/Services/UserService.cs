using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Services;

public interface IUserService
{
    Task<AuthResponse?> AuthenticateWithWalletAsync(WalletAuthRequest request);
    Task<AuthResponse?> RefreshTokenAsync(string refreshToken);
    Task<User?> GetUserAsync(string userId);
    Task<User?> GetUserByWalletAsync(string walletAddress);
    Task<User> GetOrCreateUserAsync(string walletAddress);
    Task<bool> UpdateUserAsync(User user);
    Task<CreateApiKeyResponse?> CreateApiKeyAsync(string userId, CreateApiKeyRequest request);
    Task<bool> ValidateApiKeyAsync(string apiKey, out string? userId);
    Task<SshKey?> AddSshKeyAsync(string userId, AddSshKeyRequest request);
    Task<bool> RemoveSshKeyAsync(string userId, string keyId);
}

public class UserService : IUserService
{
    private readonly OrchestratorDataStore _dataStore;
    private readonly IConfiguration _configuration;
    private readonly IEventService _eventService;
    private readonly ILogger<UserService> _logger;

    public UserService(
        OrchestratorDataStore dataStore,
        IConfiguration configuration,
        IEventService eventService,
        ILogger<UserService> logger)
    {
        _dataStore = dataStore;
        _configuration = configuration;
        _eventService = eventService;
        _logger = logger;
    }

    public async Task<AuthResponse?> AuthenticateWithWalletAsync(WalletAuthRequest request)
    {
        // Validate timestamp (must be within 5 minutes)
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(request.Timestamp).UtcDateTime;
        if (Math.Abs((DateTime.UtcNow - timestamp).TotalMinutes) > 5)
        {
            _logger.LogWarning("Auth request timestamp too old for wallet {Wallet}", request.WalletAddress);
            return null;
        }

        // In production, verify the signature against the message using the wallet's public key
        // This is blockchain-specific (Ethereum uses ecrecover, Solana uses ed25519, etc.)
        // For now, we'll accept valid-looking requests
        
        if (string.IsNullOrEmpty(request.WalletAddress) || request.WalletAddress.Length < 10)
        {
            return null;
        }

        // Get or create user
        var user = await GetOrCreateUserAsync(request.WalletAddress);
        user.LastLoginAt = DateTime.UtcNow;

        // Generate tokens
        var (accessToken, refreshToken) = GenerateTokens(user);

        // Store refresh token
        var refreshTokenHash = HashString(refreshToken);
        _dataStore.UserSessions[refreshTokenHash] = user.Id;

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.UserLoggedIn,
            ResourceType = "user",
            ResourceId = user.Id,
            UserId = user.Id
        });

        var expiresAt = DateTime.UtcNow.AddHours(24);

        return new AuthResponse(accessToken, refreshToken, expiresAt, user);
    }

    public async Task<AuthResponse?> RefreshTokenAsync(string refreshToken)
    {
        var refreshTokenHash = HashString(refreshToken);
        
        if (!_dataStore.UserSessions.TryGetValue(refreshTokenHash, out var userId))
        {
            return null;
        }

        var user = await GetUserAsync(userId);
        if (user == null || user.Status != UserStatus.Active)
        {
            return null;
        }

        // Remove old refresh token
        _dataStore.UserSessions.TryRemove(refreshTokenHash, out _);

        // Generate new tokens
        var (newAccessToken, newRefreshToken) = GenerateTokens(user);

        // Store new refresh token
        var newRefreshTokenHash = HashString(newRefreshToken);
        _dataStore.UserSessions[newRefreshTokenHash] = user.Id;

        var expiresAt = DateTime.UtcNow.AddHours(24);

        return new AuthResponse(newAccessToken, newRefreshToken, expiresAt, user);
    }

    public Task<User?> GetUserAsync(string userId)
    {
        _dataStore.Users.TryGetValue(userId, out var user);
        return Task.FromResult(user);
    }

    public Task<User?> GetUserByWalletAsync(string walletAddress)
    {
        var user = _dataStore.Users.Values
            .FirstOrDefault(u => u.WalletAddress.Equals(walletAddress, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(user);
    }

    public async Task<User> GetOrCreateUserAsync(string walletAddress)
    {
        var existing = await GetUserByWalletAsync(walletAddress);
        if (existing != null)
            return existing;

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            WalletAddress = walletAddress,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow,
            CryptoBalance = 0
        };

        _dataStore.Users.TryAdd(user.Id, user);

        _logger.LogInformation("New user created: {UserId} ({Wallet})", user.Id, walletAddress);

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.UserCreated,
            ResourceType = "user",
            ResourceId = user.Id,
            UserId = user.Id
        });

        return user;
    }

    public Task<bool> UpdateUserAsync(User user)
    {
        if (!_dataStore.Users.ContainsKey(user.Id))
            return Task.FromResult(false);

        _dataStore.Users[user.Id] = user;
        return Task.FromResult(true);
    }

    public Task<CreateApiKeyResponse?> CreateApiKeyAsync(string userId, CreateApiKeyRequest request)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return Task.FromResult<CreateApiKeyResponse?>(null);

        // Generate API key
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var apiKey = $"sk_{Convert.ToBase64String(keyBytes).Replace("+", "").Replace("/", "").Replace("=", "")}";
        var keyPrefix = apiKey[..11]; // "sk_" + first 8 chars

        var key = new ApiKey
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            KeyHash = HashString(apiKey),
            KeyPrefix = keyPrefix,
            Scopes = request.Scopes ?? new List<string> { "vms:read", "vms:write" },
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt
        };

        user.ApiKeys.Add(key);

        _logger.LogInformation("API key created for user {UserId}: {KeyId}", userId, key.Id);

        return Task.FromResult<CreateApiKeyResponse?>(
            new CreateApiKeyResponse(key.Id, apiKey, keyPrefix, key.CreatedAt, key.ExpiresAt));
    }

    public Task<bool> ValidateApiKeyAsync(string apiKey, out string? userId)
    {
        userId = null;
        var keyHash = HashString(apiKey);

        foreach (var user in _dataStore.Users.Values)
        {
            var key = user.ApiKeys.FirstOrDefault(k => k.KeyHash == keyHash);
            if (key != null)
            {
                // Check expiration
                if (key.ExpiresAt.HasValue && key.ExpiresAt.Value < DateTime.UtcNow)
                {
                    return Task.FromResult(false);
                }

                key.LastUsedAt = DateTime.UtcNow;
                userId = user.Id;
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<SshKey?> AddSshKeyAsync(string userId, AddSshKeyRequest request)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return Task.FromResult<SshKey?>(null);

        // Validate SSH key format
        if (!request.PublicKey.StartsWith("ssh-") && !request.PublicKey.StartsWith("ecdsa-"))
        {
            return Task.FromResult<SshKey?>(null);
        }

        // Calculate fingerprint (simplified - in production use proper SSH fingerprint)
        var fingerprint = HashString(request.PublicKey)[..32];

        var sshKey = new SshKey
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            PublicKey = request.PublicKey,
            Fingerprint = fingerprint,
            CreatedAt = DateTime.UtcNow
        };

        user.SshKeys.Add(sshKey);

        _logger.LogInformation("SSH key added for user {UserId}: {KeyName}", userId, request.Name);

        return Task.FromResult<SshKey?>(sshKey);
    }

    public Task<bool> RemoveSshKeyAsync(string userId, string keyId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return Task.FromResult(false);

        var key = user.SshKeys.FirstOrDefault(k => k.Id == keyId);
        if (key == null)
            return Task.FromResult(false);

        user.SshKeys.Remove(key);
        return Task.FromResult(true);
    }

    private (string accessToken, string refreshToken) GenerateTokens(User user)
    {
        var jwtKey = _configuration["Jwt:Key"] ?? "default-dev-key-change-in-production-min-32-chars!";
        var jwtIssuer = _configuration["Jwt:Issuer"] ?? "orchestrator";
        var jwtAudience = _configuration["Jwt:Audience"] ?? "orchestrator-client";

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("wallet", user.WalletAddress),
            new Claim(ClaimTypes.Role, "user")
        };

        var accessToken = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: credentials
        );

        var refreshTokenBytes = RandomNumberGenerator.GetBytes(32);
        var refreshToken = Convert.ToBase64String(refreshTokenBytes);

        return (new JwtSecurityTokenHandler().WriteToken(accessToken), refreshToken);
    }

    private static string HashString(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
