using Microsoft.IdentityModel.Tokens;
using Nethereum.Signer;
using Nethereum.Util;
using Orchestrator.Interfaces;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Services;

public class UserService : IUserService
{
    private readonly DataStore _dataStore;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly INonceStore _nonceStore;
    private readonly IRefreshTokenStore _refreshTokenStore;
    private readonly ILogger<UserService> _logger;

    // Configuration constants
    private const int AccessTokenExpiryMinutes = 60; // 1 hour
    private const int RefreshTokenExpiryDays = 7;

    public UserService(
        DataStore dataStore,
        IConfiguration configuration,
        IHostEnvironment environment,
        INonceStore nonceStore,
        IRefreshTokenStore refreshTokenStore,
        ILogger<UserService> logger)
    {
        _dataStore = dataStore;
        _configuration = configuration;
        _environment = environment;
        _nonceStore = nonceStore;
        _refreshTokenStore = refreshTokenStore;
        _logger = logger;
    }

    // =====================================================
    // USER MANAGEMENT
    // =====================================================

    public Task<User?> GetUserByIdAsync(string userId)
    {
        _dataStore.Users.TryGetValue(userId, out var user);
        return Task.FromResult(user);
    }

    public Task<User?> GetUserByWalletAsync(string walletAddress)
    {
        // Normalize to checksum format for lookup
        var normalizedAddress = new AddressUtil().ConvertToChecksumAddress(walletAddress);

        // Since wallet address IS the ID, this is just a direct lookup
        _dataStore.Users.TryGetValue(normalizedAddress, out var user);
        return Task.FromResult(user);
    }

    public Task<User?> GetUserByApiKeyAsync(string apiKey)
    {
        var keyHash = HashApiKey(apiKey);
        var kvp = _dataStore.Users.Where(u => u.Value.ApiKeys.Find(k => k.KeyHash == keyHash) != null).FirstOrDefault();
        return Task.FromResult(kvp.Value);
    }

    public async Task<User> CreateUserAsync(string walletAddress)
    {
        // Normalize wallet address to checksum format
        var normalizedAddress = new AddressUtil().ConvertToChecksumAddress(walletAddress);

        var user = new User
        {
            Id = normalizedAddress, // Wallet address IS the user ID
            DisplayName = null,
            Email = null,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow
        };

        await _dataStore.SaveUserAsync(user);
        _logger.LogInformation("User created with wallet ID: {WalletAddress}", normalizedAddress);

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
    // SSH KEY MANAGEMENT
    // =====================================================

    public async Task<SshKey?> AddSshKeyAsync(string userId, AddSshKeyRequest request)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return null;

        // Validate SSH key format
        if (!ValidateSshKeyFormat(request.PublicKey))
        {
            _logger.LogWarning("Invalid SSH key format for user {UserId}", userId);
            return null;
        }

        var fingerprint = GenerateSshKeyFingerprint(request.PublicKey);

        var sshKey = new SshKey
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            PublicKey = request.PublicKey,
            Fingerprint = fingerprint,
            CreatedAt = DateTime.UtcNow
        };

        user.SshKeys.Add(sshKey);
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("SSH key added for user {UserId}: {KeyName} ({Fingerprint})",
            userId, request.Name, fingerprint);

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

    public Task<List<SshKey>> GetSshKeysAsync(string userId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return Task.FromResult(new List<SshKey>());

        return Task.FromResult(user.SshKeys);
    }

    // =====================================================
    // API KEY MANAGEMENT
    // =====================================================

    public async Task<CreateApiKeyResponse?> CreateApiKeyAsync(
        string userId,
        CreateApiKeyRequest request)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return null;

        // Generate cryptographically secure API key
        var rawKey = GenerateApiKey();
        var keyHash = HashApiKey(rawKey);
        var keyPrefix = rawKey.Substring(0, 8);

        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            Scopes = request.Scopes ?? new List<string> { "vm:read", "vm:write" },
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt
        };

        user.ApiKeys.Add(apiKey);
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("API key created for user {UserId}: {KeyName}", userId, request.Name);

        return new CreateApiKeyResponse(
            apiKey.Id,
            rawKey,
            apiKey.KeyPrefix,
            apiKey.CreatedAt,
            apiKey.ExpiresAt
        );
    }

    public async Task<bool> RevokeApiKeyAsync(string userId, string keyId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return false;

        var key = user.ApiKeys.FirstOrDefault(k => k.Id == keyId);
        if (key == null)
            return false;

        user.ApiKeys.Remove(key);
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("API key revoked for user {UserId}: {KeyId}", userId, keyId);
        return true;
    }

    public Task<List<ApiKey>> GetApiKeysAsync(string userId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return Task.FromResult(new List<ApiKey>());

        return Task.FromResult(user.ApiKeys);
    }

    public async Task<User?> ValidateApiKeyAsync(string apiKey)
    {
        var keyHash = HashApiKey(apiKey);
        var keyPrefix = apiKey.Length >= 8 ? apiKey.Substring(0, 8) : "";

        foreach (var user in _dataStore.Users.Values)
        {
            var matchingKey = user.ApiKeys.FirstOrDefault(k =>
                k.KeyPrefix == keyPrefix && k.KeyHash == keyHash);

            if (matchingKey != null)
            {
                // Check if expired
                if (matchingKey.ExpiresAt.HasValue && matchingKey.ExpiresAt.Value < DateTime.UtcNow)
                {
                    _logger.LogWarning("Expired API key used: {KeyId} (User: {UserId})",
                        matchingKey.Id, user.Id);
                    return null;
                }

                // Update last used timestamp
                matchingKey.LastUsedAt = DateTime.UtcNow;
                await _dataStore.SaveUserAsync(user);

                return user;
            }
        }

        return null;
    }

    // =====================================================
    // AUTHENTICATION
    // =====================================================

    public async Task<AuthResponse?> AuthenticateWithWalletAsync(WalletAuthRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Message) || string.IsNullOrEmpty(request.Signature))
            {
                _logger.LogWarning("Auth request missing message or signature");
                return null;
            }

            string walletAddress;

            // Dev-only mock bypass (unchanged, explicit opt-in).
            if (_environment.IsDevelopment() &&
                IsMockSignature(request.Signature) &&
                Environment.GetEnvironmentVariable("DECLOUD_ALLOW_MOCK_AUTH") == "true")
            {
                _logger.LogWarning("DEV MODE: Accepting mock signature for wallet {Wallet}", request.WalletAddress);
                walletAddress = request.WalletAddress ?? string.Empty;
                if (string.IsNullOrEmpty(walletAddress)) return null;
            }
            else
            {
                // 1. Recover the signer from the EIP-4361 message + signature.
                var recovered = RecoverAddressFromSignature(request.Message, request.Signature);
                if (string.IsNullOrEmpty(recovered))
                {
                    _logger.LogWarning("Failed to recover address from signature");
                    return null;
                }

                // 2. The recovered address must appear in the signed message.
                if (!request.Message.Contains(recovered, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Recovered address not present in signed message");
                    return null;
                }

                // 3. Consume the single-use nonce. This is the replay defense:
                //    a captured (message, signature) pair cannot be reused.
                var nonceMatch = System.Text.RegularExpressions.Regex.Match(
                    request.Message, @"Nonce:\s*(\S+)");
                if (!nonceMatch.Success)
                {
                    _logger.LogWarning("Signed message has no Nonce field");
                    return null;
                }
                if (!await _nonceStore.ConsumeAsync(nonceMatch.Groups[1].Value))
                {
                    _logger.LogWarning("Nonce missing, expired, or already used (possible replay)");
                    return null;
                }

                walletAddress = recovered;
            }

            // 4. Wallet address is the identity.
            var user = await GetUserByWalletAsync(walletAddress) ?? await CreateUserAsync(walletAddress);
            user.LastLoginAt = DateTime.UtcNow;
            await UpdateUserAsync(user);

            // 5. Issue tokens. Refresh token persists in the durable store.
            var accessToken = GenerateJwtToken(user);
            var refreshToken = GenerateRefreshToken();
            await _refreshTokenStore.StoreAsync(
                refreshToken, user.Id, DateTime.UtcNow.AddDays(RefreshTokenExpiryDays));

            var expiresAt = DateTime.UtcNow.AddMinutes(AccessTokenExpiryMinutes);
            _logger.LogInformation("Wallet auth successful for: {Wallet}", walletAddress);
            return new AuthResponse(accessToken, refreshToken, expiresAt, user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during wallet authentication");
            return null;
        }
    }

    /// <summary>
    /// Refresh access token using refresh token
    /// </summary>
    public async Task<AuthResponse?> RefreshTokenAsync(string refreshToken)
    {
        try
        {
            // Atomic single-use consume. Concurrent refreshes race here; one wins,
            // the rest get null — without invalidating a legitimately-rotated token.
            var userId = await _refreshTokenStore.ConsumeAsync(refreshToken);
            if (userId == null)
            {
                _logger.LogWarning("Refresh token not found / expired / already used");
                return null;
            }

            var user = await GetUserByIdAsync(userId);
            if (user == null || user.Status != UserStatus.Active)
            {
                _logger.LogWarning("User not found or inactive: {UserId}", userId);
                return null;
            }

            var newAccessToken = GenerateJwtToken(user);
            var newRefreshToken = GenerateRefreshToken();
            await _refreshTokenStore.StoreAsync(
                newRefreshToken, user.Id, DateTime.UtcNow.AddDays(RefreshTokenExpiryDays));

            var expiresAt = DateTime.UtcNow.AddMinutes(AccessTokenExpiryMinutes);
            _logger.LogInformation("Token refreshed for user: {UserId}", user.Id);
            return new AuthResponse(newAccessToken, newRefreshToken, expiresAt, user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing token");
            return null;
        }
    }

    public async Task<string?> GetSessionWalletAsync(string refreshToken)
    {
        var userId = await _refreshTokenStore.PeekAsync(refreshToken);
        if (userId == null) return null;
        var user = await GetUserByIdAsync(userId);
        return user?.WalletAddress;
    }

    public Task LogoutAsync(string refreshToken, CancellationToken ct = default)
        => _refreshTokenStore.RevokeAsync(refreshToken, ct);

    /// <summary>
    /// Verify that an EIP-191 signature over <paramref name="message"/> recovers to
    /// <paramref name="walletAddress"/>. Reuses RecoverAddressFromSignature so there
    /// is a single signature-verification path across the platform.
    /// </summary>
    public bool VerifyWalletSignature(string walletAddress, string message, string signature)
    {
        var recovered = RecoverAddressFromSignature(message, signature);
        return !string.IsNullOrEmpty(recovered)
            && string.Equals(recovered, walletAddress, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================
    // PRIVATE HELPERS
    // =====================================================

    /// <summary>
    /// Check if signature is a mock/test signature (dev mode only)
    /// </summary>
    private bool IsMockSignature(string signature)
    {
        if (string.IsNullOrEmpty(signature))
            return false;

        // Common mock signatures used in testing
        var mockSignatures = new[] { "0xmocksig", "0xmocksignature", "0xtest", "mock" };
        return mockSignatures.Any(m => signature.StartsWith(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Recover Ethereum address from signed message using Nethereum
    /// </summary>
    private string? RecoverAddressFromSignature(string message, string signature)
    {
        try
        {
            // Nethereum's EthereumMessageSigner handles EIP-191 prefix automatically
            var signer = new EthereumMessageSigner();
            var recoveredAddress = signer.EncodeUTF8AndEcRecover(message, signature);

            // Normalize to checksum address
            return new AddressUtil().ConvertToChecksumAddress(recoveredAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover address from signature");
            return null;
        }
    }

    /// <summary>
    /// Generate JWT access token with wallet address as both 'sub' and 'wallet' claims
    /// </summary>
    private string GenerateJwtToken(User user)
    {
        var jwtKey = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT key not configured. Set Jwt:Key in appsettings or via environment variable Jwt__Key.");
        var jwtIssuer = _configuration["Jwt:Issuer"] ?? "orchestrator";
        var jwtAudience = _configuration["Jwt:Audience"] ?? "decloud";

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id), // User ID (wallet address)
            new Claim("wallet", user.WalletAddress), // Explicit wallet claim
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("user_status", user.Status.ToString())
        };

        // Add role claims
        foreach (var role in user.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (!string.IsNullOrEmpty(user.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(AccessTokenExpiryMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Generate refresh token
    /// </summary>
    private static string GenerateRefreshToken()
    {
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    /// <summary>
    /// Generate cryptographically secure API key
    /// </summary>
    private string GenerateApiKey()
    {
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);

        return $"dc_{Convert.ToBase64String(randomBytes).Replace("/", "_").Replace("+", "-").TrimEnd('=')}";
    }

    /// <summary>
    /// Hash API key using SHA256
    /// </summary>
    private string HashApiKey(string apiKey)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Validate SSH public key format
    /// </summary>
    private bool ValidateSshKeyFormat(string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
            return false;

        var validPrefixes = new[] { "ssh-rsa", "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521" };
        return validPrefixes.Any(prefix => publicKey.TrimStart().StartsWith(prefix));
    }

    /// <summary>
    /// Generate SSH key fingerprint (MD5)
    /// </summary>
    private string GenerateSshKeyFingerprint(string publicKey)
    {
        try
        {
            var parts = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return "invalid";

            var keyBytes = Convert.FromBase64String(parts[1]);
            using var md5 = MD5.Create();
            var hashBytes = md5.ComputeHash(keyBytes);

            return string.Join(":", hashBytes.Select(b => b.ToString("x2")));
        }
        catch
        {
            return "invalid";
        }
    }
}