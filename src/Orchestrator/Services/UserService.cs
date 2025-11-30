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
    Task<AuthResponse?> AuthenticateAsync(AuthRequest request);
    Task<AuthResponse?> RefreshTokenAsync(string refreshToken);
    Task<User?> GetUserAsync(string userId);
    Task<User?> GetUserByWalletAsync(string walletAddress);
    Task<User> GetOrCreateUserAsync(string walletAddress);
    Task<bool> UpdateUserAsync(User user);
    Task<CreateApiKeyResponse?> CreateApiKeyAsync(string userId, CreateApiKeyRequest request);
    Task<bool> ValidateApiKeyAsync(string apiKey, out string? userId);
}

public class UserService : IUserService
{
    private readonly DataStore _dataStore;
    private readonly IEventService _eventService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UserService> _logger;

    public UserService(
        DataStore dataStore,
        IEventService eventService,
        IConfiguration configuration,
        ILogger<UserService> logger)
    {
        _dataStore = dataStore;
        _eventService = eventService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AuthResponse?> AuthenticateAsync(AuthRequest request)
    {
        if (string.IsNullOrEmpty(request.WalletAddress) || request.WalletAddress.Length < 10)
        {
            return null;
        }

        var user = await GetOrCreateUserAsync(request.WalletAddress);
        user.LastLoginAt = DateTime.UtcNow;
        await _dataStore.SaveUserAsync(user);

        var (accessToken, refreshToken) = GenerateTokens(user);

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

        _dataStore.UserSessions.TryRemove(refreshTokenHash, out _);

        var (newAccessToken, newRefreshToken) = GenerateTokens(user);

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
            CryptoBalance = 0,
            Quotas = new UserQuotas
            {
                MaxVms = 10,
                MaxCpuCores = 32,
                MaxMemoryMb = 65536,
                MaxStorageGb = 1000
            }
        };

        await _dataStore.SaveUserAsync(user);

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

    public async Task<bool> UpdateUserAsync(User user)
    {
        if (!_dataStore.Users.ContainsKey(user.Id))
            return false;

        await _dataStore.SaveUserAsync(user);
        return true;
    }

    public async Task<CreateApiKeyResponse?> CreateApiKeyAsync(string userId, CreateApiKeyRequest request)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return null;

        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var apiKey = $"sk_{Convert.ToBase64String(keyBytes).Replace("+", "").Replace("/", "").Replace("=", "")}";
        var keyPrefix = apiKey[..11];

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
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("API key created for user {UserId}: {KeyId}", userId, key.Id);

        return new CreateApiKeyResponse(key.Id, apiKey, keyPrefix, key.CreatedAt, key.ExpiresAt);
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
                if (key.ExpiresAt.HasValue && key.ExpiresAt.Value < DateTime.UtcNow)
                {
                    return Task.FromResult(false);
                }

                userId = user.Id;
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    private (string accessToken, string refreshToken) GenerateTokens(User user)
    {
        var jwtKey = _configuration["Jwt:Key"] ?? "default-dev-key-change-in-production-min-32-chars!";
        var jwtIssuer = _configuration["Jwt:Issuer"] ?? "orchestrator";
        var jwtAudience = _configuration["Jwt:Audience"] ?? "orchestrator-client";

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("wallet", user.WalletAddress),
            new Claim(ClaimTypes.Name, user.WalletAddress),
            new Claim(ClaimTypes.NameIdentifier, user.Id)
        };

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds
        );

        var accessToken = new JwtSecurityTokenHandler().WriteToken(token);

        var refreshTokenBytes = RandomNumberGenerator.GetBytes(32);
        var refreshToken = Convert.ToBase64String(refreshTokenBytes);

        return (accessToken, refreshToken);
    }

    private static string HashString(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}