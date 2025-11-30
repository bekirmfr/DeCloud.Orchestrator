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

// <summary>
/// User management service implementation
/// This is a REFERENCE IMPLEMENTATION showing the missing methods
/// </summary>
public class UserService : IUserService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<UserService> _logger;

    public UserService(DataStore dataStore, ILogger<UserService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    // =====================================================
    // MISSING METHODS - ADD THESE TO YOUR UserService
    // =====================================================

    /// <summary>
    /// Add an SSH key to a user's account
    /// </summary>
    public async Task<SshKey?> AddSshKeyAsync(string userId, AddSshKeyRequest request)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
        {
            _logger.LogWarning("Cannot add SSH key - user {UserId} not found", userId);
            return null;
        }

        // Validate SSH key format
        if (!IsValidSshPublicKey(request.PublicKey))
        {
            _logger.LogWarning("Invalid SSH key format for user {UserId}", userId);
            return null;
        }

        // Check for duplicate
        var fingerprint = GenerateSshKeyFingerprint(request.PublicKey);
        if (user.SshKeys.Any(k => k.Fingerprint == fingerprint))
        {
            _logger.LogWarning("SSH key already exists for user {UserId}", userId);
            return null;
        }

        var sshKey = new SshKey
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            PublicKey = request.PublicKey.Trim(),
            Fingerprint = fingerprint,
            CreatedAt = DateTime.UtcNow
        };

        user.SshKeys.Add(sshKey);
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("SSH key added for user {UserId}: {KeyName}", userId, request.Name);

        return sshKey;
    }

    /// <summary>
    /// Remove an SSH key from a user's account
    /// </summary>
    public async Task<bool> RemoveSshKeyAsync(string userId, string keyId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
        {
            _logger.LogWarning("Cannot remove SSH key - user {UserId} not found", userId);
            return false;
        }

        var key = user.SshKeys.FirstOrDefault(k => k.Id == keyId);
        if (key == null)
        {
            _logger.LogWarning("SSH key {KeyId} not found for user {UserId}", keyId, userId);
            return false;
        }

        user.SshKeys.Remove(key);
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("SSH key removed for user {UserId}: {KeyName}", userId, key.Name);

        return true;
    }

    /// <summary>
    /// Get a specific SSH key
    /// </summary>
    public Task<SshKey?> GetSshKeyAsync(string userId, string keyId)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
        {
            return Task.FromResult<SshKey?>(null);
        }

        var key = user.SshKeys.FirstOrDefault(k => k.Id == keyId);
        return Task.FromResult(key);
    }

    // =====================================================
    // SSH KEY VALIDATION HELPERS
    // =====================================================

    /// <summary>
    /// Validate SSH public key format
    /// Supports: ssh-rsa, ssh-ed25519, ecdsa-sha2-nistp256/384/521
    /// </summary>
    private bool IsValidSshPublicKey(string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
            return false;

        var key = publicKey.Trim();

        // Check for valid SSH key types
        var validPrefixes = new[]
        {
            "ssh-rsa ",
            "ssh-ed25519 ",
            "ecdsa-sha2-nistp256 ",
            "ecdsa-sha2-nistp384 ",
            "ecdsa-sha2-nistp521 ",
            "sk-ecdsa-sha2-nistp256@openssh.com ",
            "sk-ssh-ed25519@openssh.com "
        };

        if (!validPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return false;
        }

        // Basic format check: <type> <base64-key> [optional-comment]
        var parts = key.Split(' ', 3);
        if (parts.Length < 2)
        {
            return false;
        }

        // Validate base64 portion
        try
        {
            var base64 = parts[1];
            if (string.IsNullOrWhiteSpace(base64))
                return false;

            // Try to decode base64
            Convert.FromBase64String(base64);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generate SSH key fingerprint (MD5 hash, OpenSSH format)
    /// </summary>
    private string GenerateSshKeyFingerprint(string publicKey)
    {
        try
        {
            var parts = publicKey.Trim().Split(' ');
            if (parts.Length < 2)
                return "invalid";

            var keyData = Convert.FromBase64String(parts[1]);
            var hash = MD5.HashData(keyData);

            // Format as OpenSSH fingerprint: MD5:xx:xx:xx:...
            var fingerprint = "MD5:" + string.Join(":", hash.Select(b => b.ToString("x2")));
            return fingerprint;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate SSH key fingerprint");
            return $"invalid-{Guid.NewGuid().ToString()[..8]}";
        }
    }

    // =====================================================
    // PLACEHOLDER IMPLEMENTATIONS FOR OTHER METHODS
    // (You should already have these in your UserService)
    // =====================================================

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

    public async Task<User> CreateUserAsync(string walletAddress)
    {
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            WalletAddress = walletAddress,
            DisplayName = null,
            Email = null,
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

    public Task<AuthResponse?> AuthenticateWithWalletAsync(WalletAuthRequest request)
    {
        // TODO: Implement wallet signature verification
        throw new NotImplementedException("Implement wallet signature verification");
    }

    public Task<AuthResponse?> RefreshTokenAsync(string refreshToken)
    {
        // TODO: Implement refresh token logic
        throw new NotImplementedException("Implement refresh token logic");
    }

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

        _logger.LogInformation("API key deleted for user {UserId}: {KeyName}", userId, key.Name);
        return true;
    }

    public Task<bool> ValidateApiKeyAsync(string apiKey)
    {
        var keyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

        var valid = _dataStore.Users.Values.Any(u =>
            u.ApiKeys.Any(k => k.KeyHash == keyHash &&
                              (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow)));

        return Task.FromResult(valid);
    }

    public Task<User?> GetUserByApiKeyAsync(string apiKey)
    {
        var keyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

        var user = _dataStore.Users.Values.FirstOrDefault(u =>
            u.ApiKeys.Any(k => k.KeyHash == keyHash &&
                              (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow)));

        return Task.FromResult(user);
    }

    public Task<bool> CheckQuotaAsync(string userId, int cpuCores, long memoryMb, long storageGb)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return Task.FromResult(false);

        var quotas = user.Quotas;
        var canCreate = quotas.CurrentVms < quotas.MaxVms &&
                       quotas.CurrentCpuCores + cpuCores <= quotas.MaxCpuCores &&
                       quotas.CurrentMemoryMb + memoryMb <= quotas.MaxMemoryMb &&
                       quotas.CurrentStorageGb + storageGb <= quotas.MaxStorageGb;

        return Task.FromResult(canCreate);
    }

    public async Task UpdateQuotaUsageAsync(string userId, int cpuDelta, long memoryDelta, long storageDelta)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return;

        user.Quotas.CurrentCpuCores += cpuDelta;
        user.Quotas.CurrentMemoryMb += memoryDelta;
        user.Quotas.CurrentStorageGb += storageDelta;

        await _dataStore.SaveUserAsync(user);
    }

    public async Task<bool> DeductBalanceAsync(string userId, decimal amount)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return false;

        if (user.CryptoBalance < amount)
        {
            _logger.LogWarning("Insufficient balance for user {UserId}: {Balance} < {Amount}",
                userId, user.CryptoBalance, amount);
            return false;
        }

        user.CryptoBalance -= amount;
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("Deducted {Amount} {Token} from user {UserId}",
            amount, user.BalanceToken, userId);

        return true;
    }

    public async Task<bool> AddBalanceAsync(string userId, decimal amount)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return false;

        user.CryptoBalance += amount;
        await _dataStore.SaveUserAsync(user);

        _logger.LogInformation("Added {Amount} {Token} to user {UserId}",
            amount, user.BalanceToken, userId);

        return true;
    }
}