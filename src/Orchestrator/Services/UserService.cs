using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Orchestrator.Data;
using Orchestrator.Models;
using Nethereum.Signer;

namespace Orchestrator.Services;

/// <summary>
/// User management service interface
/// </summary>
public interface IUserService
{
    // User CRUD operations
    Task<User?> GetUserAsync(string userId);
    Task<User?> GetUserByWalletAsync(string walletAddress);
    Task<User> CreateUserAsync(string walletAddress);
    Task UpdateUserAsync(User user);
    Task<bool> DeleteUserAsync(string userId);

    // Authentication
    Task<AuthResponse?> AuthenticateWithWalletAsync(WalletAuthRequest request);
    Task<AuthResponse?> RefreshTokenAsync(string refreshToken);

    // SSH Key Management
    Task<SshKey?> AddSshKeyAsync(string userId, AddSshKeyRequest request);
    Task<bool> RemoveSshKeyAsync(string userId, string keyId);
    Task<SshKey?> GetSshKeyAsync(string userId, string keyId);

    // API Key Management
    Task<CreateApiKeyResponse?> CreateApiKeyAsync(string userId, CreateApiKeyRequest request);
    Task<bool> DeleteApiKeyAsync(string userId, string keyId);
    Task<bool> ValidateApiKeyAsync(string apiKey);
    Task<User?> GetUserByApiKeyAsync(string apiKey);

    // Quota Management
    Task<bool> CheckQuotaAsync(string userId, int cpuCores, long memoryMb, long storageGb);
    Task UpdateQuotaUsageAsync(string userId, int cpuDelta, long memoryDelta, long storageDelta);

    // Balance Management
    Task<bool> DeductBalanceAsync(string userId, decimal amount);
    Task<bool> AddBalanceAsync(string userId, decimal amount);
}

/// <summary>
/// User management service implementation with complete authentication
/// </summary>
public class UserService : IUserService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<UserService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _jwtKey;
    private readonly string _jwtIssuer;
    private readonly string _jwtAudience;
    private readonly bool _isDevelopment;

    // Refresh token storage (in production, use Redis or database)
    private static readonly Dictionary<string, RefreshTokenData> _refreshTokens = new();
    private static readonly SemaphoreSlim _refreshTokenLock = new(1, 1);

    public UserService(
        DataStore dataStore,
        ILogger<UserService> logger,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _dataStore = dataStore;
        _logger = logger;
        _configuration = configuration;
        _isDevelopment = environment.IsDevelopment();

        // Load JWT configuration
        _jwtKey = configuration["Jwt:Key"]
            ?? "default-dev-key-change-in-production-min-32-chars!";
        _jwtIssuer = configuration["Jwt:Issuer"] ?? "orchestrator";
        _jwtAudience = configuration["Jwt:Audience"] ?? "orchestrator-client";
    }

    // =====================================================
    // Authentication Implementation
    // =====================================================

    public async Task<AuthResponse?> AuthenticateWithWalletAsync(WalletAuthRequest request)
    {
        try
        {
            // Normalize wallet address
            var walletAddress = request.WalletAddress.ToLowerInvariant();

            // Validate timestamp (prevent replay attacks)
            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var timeDiff = Math.Abs(currentTimestamp - request.Timestamp);

            if (timeDiff > 300) // 5 minutes tolerance
            {
                _logger.LogWarning(
                    "Authentication rejected: timestamp too old or in future ({Diff}s)",
                    timeDiff);
                return null;
            }

            // Verify signature
            var isValidSignature = VerifySignature(
                request.Message,
                request.Signature,
                walletAddress);

            if (!isValidSignature)
            {
                _logger.LogWarning(
                    "Authentication failed: invalid signature for wallet {Wallet}",
                    walletAddress);
                return null;
            }

            // Get or create user
            var user = await GetUserByWalletAsync(walletAddress);
            if (user == null)
            {
                _logger.LogInformation("Creating new user for wallet {Wallet}", walletAddress);
                user = await CreateUserAsync(walletAddress);
            }

            // Update last login
            user.LastLoginAt = DateTime.UtcNow;
            await _dataStore.SaveUserAsync(user);

            // Generate tokens
            var accessToken = GenerateAccessToken(user);
            var refreshToken = await GenerateRefreshTokenAsync(user.Id);

            var expiresAt = DateTime.UtcNow.AddHours(1);

            _logger.LogInformation(
                "User authenticated successfully: {UserId} ({Wallet})",
                user.Id, user.WalletAddress);

            return new AuthResponse(accessToken, refreshToken, expiresAt, user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during wallet authentication");
            return null;
        }
    }

    public async Task<AuthResponse?> RefreshTokenAsync(string refreshToken)
    {
        await _refreshTokenLock.WaitAsync();
        try
        {
            // Validate refresh token
            if (!_refreshTokens.TryGetValue(refreshToken, out var tokenData))
            {
                _logger.LogWarning("Refresh token not found or expired");
                return null;
            }

            // Check expiration
            if (tokenData.ExpiresAt < DateTime.UtcNow)
            {
                _refreshTokens.Remove(refreshToken);
                _logger.LogWarning("Refresh token expired");
                return null;
            }

            // Get user
            var user = await GetUserAsync(tokenData.UserId);
            if (user == null || user.Status != UserStatus.Active)
            {
                _refreshTokens.Remove(refreshToken);
                _logger.LogWarning("User not found or inactive for refresh token");
                return null;
            }

            // Remove old refresh token
            _refreshTokens.Remove(refreshToken);

            // Generate new tokens
            var newAccessToken = GenerateAccessToken(user);
            var newRefreshToken = await GenerateRefreshTokenAsync(user.Id);

            var expiresAt = DateTime.UtcNow.AddHours(1);

            _logger.LogInformation("Tokens refreshed for user {UserId}", user.Id);

            return new AuthResponse(newAccessToken, newRefreshToken, expiresAt, user);
        }
        finally
        {
            _refreshTokenLock.Release();
        }
    }

    /// <summary>
    /// Verify Ethereum signature
    /// In development mode, accepts mock signatures for testing
    /// </summary>
    private bool VerifySignature(string message, string signature, string walletAddress)
    {
        // DEVELOPMENT MODE: Accept mock signatures
        if (_isDevelopment && signature.StartsWith("0xmock", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Accepting mock signature in development mode");
            return true;
        }

        try
        {
            // Use Nethereum to verify the signature
            var signer = new EthereumMessageSigner();
            var recoveredAddress = signer.EncodeUTF8AndEcRecover(message, signature);

            // Compare addresses (case-insensitive)
            var isValid = string.Equals(
                recoveredAddress,
                walletAddress,
                StringComparison.OrdinalIgnoreCase);

            if (!isValid)
            {
                _logger.LogWarning(
                    "Signature verification failed: expected {Expected}, got {Actual}",
                    walletAddress, recoveredAddress);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying signature");
            return false;
        }
    }

    /// <summary>
    /// Generate JWT access token
    /// </summary>
    private string GenerateAccessToken(User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim("wallet", user.WalletAddress),
            new Claim(ClaimTypes.Role, "user"),
            new Claim(ClaimTypes.AuthenticationMethod, "Wallet"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtIssuer,
            audience: _jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Generate refresh token
    /// </summary>
    private async Task<string> GenerateRefreshTokenAsync(string userId)
    {
        await _refreshTokenLock.WaitAsync();
        try
        {
            var tokenBytes = new byte[32];
            RandomNumberGenerator.Fill(tokenBytes);
            var refreshToken = Convert.ToBase64String(tokenBytes);

            var tokenData = new RefreshTokenData
            {
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            };

            _refreshTokens[refreshToken] = tokenData;

            // Clean up expired tokens (simple cleanup strategy)
            if (_refreshTokens.Count > 10000)
            {
                var expiredTokens = _refreshTokens
                    .Where(t => t.Value.ExpiresAt < DateTime.UtcNow)
                    .Select(t => t.Key)
                    .ToList();

                foreach (var expired in expiredTokens)
                {
                    _refreshTokens.Remove(expired);
                }

                _logger.LogInformation("Cleaned up {Count} expired refresh tokens", expiredTokens.Count);
            }

            return refreshToken;
        }
        finally
        {
            _refreshTokenLock.Release();
        }
    }

    // =====================================================
    // User CRUD Operations
    // =====================================================

    public Task<User?> GetUserAsync(string userId)
    {
        _dataStore.Users.TryGetValue(userId, out var user);
        return Task.FromResult(user);
    }

    public Task<User?> GetUserByWalletAsync(string walletAddress)
    {
        var user = _dataStore.Users.Values
            .FirstOrDefault(u => string.Equals(
                u.WalletAddress,
                walletAddress,
                StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(user);
    }

    public async Task<User> CreateUserAsync(string walletAddress)
    {
        var user = new User
        {
            WalletAddress = walletAddress.ToLowerInvariant(),
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow,
            CryptoBalance = 0,
            BalanceToken = "USDC"
        };

        await _dataStore.SaveUserAsync(user);
        _logger.LogInformation("User created: {UserId} ({Wallet})", user.Id, walletAddress);

        return user;
    }

    public async Task UpdateUserAsync(User user)
    {
        await _dataStore.SaveUserAsync(user);
        _logger.LogDebug("User updated: {UserId}", user.Id);
    }

    public async Task<bool> DeleteUserAsync(string userId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return false;

        user.Status = UserStatus.Deleted;
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("User deleted: {UserId}", userId);
        return true;
    }

    // =====================================================
    // SSH Key Management
    // =====================================================

    public async Task<SshKey?> AddSshKeyAsync(string userId, AddSshKeyRequest request)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return null;

        // Generate fingerprint
        var fingerprint = GenerateSshKeyFingerprint(request.PublicKey);

        var sshKey = new SshKey
        {
            Name = request.Name,
            PublicKey = request.PublicKey,
            Fingerprint = fingerprint,
            CreatedAt = DateTime.UtcNow
        };

        user.SshKeys.Add(sshKey);
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("SSH key added for user {UserId}: {KeyName}", userId, request.Name);

        return sshKey;
    }

    public async Task<bool> RemoveSshKeyAsync(string userId, string keyId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return false;

        var key = user.SshKeys.FirstOrDefault(k => k.Id == keyId);
        if (key == null)
            return false;

        user.SshKeys.Remove(key);
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("SSH key removed for user {UserId}: {KeyId}", userId, keyId);

        return true;
    }

    public Task<SshKey?> GetSshKeyAsync(string userId, string keyId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return Task.FromResult<SshKey?>(null);

        var key = user.SshKeys.FirstOrDefault(k => k.Id == keyId);
        return Task.FromResult<SshKey?>(key);
    }

    private string GenerateSshKeyFingerprint(string publicKey)
    {
        try
        {
            var keyBytes = Encoding.UTF8.GetBytes(publicKey);
            var hash = SHA256.HashData(keyBytes);
            return Convert.ToBase64String(hash);
        }
        catch
        {
            return $"fp-{Guid.NewGuid():N}";
        }
    }

    // =====================================================
    // API Key Management
    // =====================================================

    public async Task<CreateApiKeyResponse?> CreateApiKeyAsync(string userId, CreateApiKeyRequest request)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return null;

        // Generate API key
        var apiKeyBytes = new byte[32];
        RandomNumberGenerator.Fill(apiKeyBytes);
        var apiKey = $"dc_{Convert.ToBase64String(apiKeyBytes).Replace("+", "").Replace("/", "").Replace("=", "")}";

        // Hash the key
        var keyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

        var apiKeyObj = new ApiKey
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            KeyHash = keyHash,
            KeyPrefix = apiKey[..10],
            Scopes = request.Scopes ?? new List<string> { "read", "write" },
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt
        };

        user.ApiKeys.Add(apiKeyObj);
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("API key created for user {UserId}: {KeyName}", userId, request.Name);

        return new CreateApiKeyResponse(
            apiKeyObj.Id,
            apiKey,  // ONLY time the full key is returned!
            apiKeyObj.KeyPrefix,
            apiKeyObj.CreatedAt,
            apiKeyObj.ExpiresAt
        );
    }

    public async Task<bool> DeleteApiKeyAsync(string userId, string keyId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return false;

        var key = user.ApiKeys.FirstOrDefault(k => k.Id == keyId);
        if (key == null)
            return false;

        user.ApiKeys.Remove(key);
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("API key deleted for user {UserId}: {KeyId}", userId, keyId);

        return true;
    }

    public Task<bool> ValidateApiKeyAsync(string apiKey)
    {
        var keyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

        var user = _dataStore.Users.Values.FirstOrDefault(u =>
            u.ApiKeys.Any(k => k.KeyHash == keyHash && (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow)));

        return Task.FromResult(user != null);
    }

    public Task<User?> GetUserByApiKeyAsync(string apiKey)
    {
        var keyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

        var user = _dataStore.Users.Values.FirstOrDefault(u =>
            u.ApiKeys.Any(k => k.KeyHash == keyHash && (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow)));

        return Task.FromResult(user);
    }

    // =====================================================
    // Quota Management
    // =====================================================

    public Task<bool> CheckQuotaAsync(string userId, int cpuCores, long memoryMb, long storageGb)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return Task.FromResult(false);

        var quotas = user.Quotas;

        var hasQuota =
            quotas.CurrentVms < quotas.MaxVms &&
            quotas.CurrentCpuCores + cpuCores <= quotas.MaxCpuCores &&
            quotas.CurrentMemoryMb + memoryMb <= quotas.MaxMemoryMb &&
            quotas.CurrentStorageGb + storageGb <= quotas.MaxStorageGb;

        return Task.FromResult(hasQuota);
    }

    public async Task UpdateQuotaUsageAsync(string userId, int cpuDelta, long memoryDelta, long storageDelta)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return;

        user.Quotas.CurrentCpuCores += cpuDelta;
        user.Quotas.CurrentMemoryMb += memoryDelta;
        user.Quotas.CurrentStorageGb += storageDelta;

        // Ensure non-negative
        user.Quotas.CurrentCpuCores = Math.Max(0, user.Quotas.CurrentCpuCores);
        user.Quotas.CurrentMemoryMb = Math.Max(0, user.Quotas.CurrentMemoryMb);
        user.Quotas.CurrentStorageGb = Math.Max(0, user.Quotas.CurrentStorageGb);

        await _dataStore.SaveUserAsync(user);
    }

    // =====================================================
    // Balance Management
    // =====================================================

    public async Task<bool> DeductBalanceAsync(string userId, decimal amount)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return false;

        if (user.CryptoBalance < amount)
            return false;

        user.CryptoBalance -= amount;
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("Balance deducted for user {UserId}: {Amount}", userId, amount);

        return true;
    }

    public async Task<bool> AddBalanceAsync(string userId, decimal amount)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return false;

        user.CryptoBalance += amount;
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("Balance added for user {UserId}: {Amount}", userId, amount);

        return true;
    }
}

/// <summary>
/// Refresh token data (in production, store in Redis/database)
/// </summary>
internal class RefreshTokenData
{
    public string UserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}