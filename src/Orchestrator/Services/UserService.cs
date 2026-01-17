using Microsoft.IdentityModel.Tokens;
using Nethereum.Signer;
using Nethereum.Util;
using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

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
    Task<bool> CheckQuotaAsync(string userId, int virtualCpuCores, long memoryBytes, long storageBytes);
    Task UpdateQuotaUsageAsync(string userId, int cpuDelta, long memoryDelta, long storageDelta);

    // Balance Management
    Task<decimal> GetUserBalanceAsync(string userId);
    Task<BalanceInfo> GetUserBalanceInfoAsync(string userId);
    Task<bool> HasSufficientBalanceAsync(string userId, decimal requiredAmount);

}

/// <summary>
/// User management service implementation with crypto wallet authentication
/// </summary>
public class UserService : IUserService
{
    private readonly DataStore _dataStore;
    private readonly IBlockchainService _blockchainService;
    private readonly ILogger<UserService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    
    // Refresh token storage (in production, use Redis or database)
    private static readonly Dictionary<string, RefreshTokenInfo> _refreshTokens = new();
    private static readonly object _refreshTokenLock = new();

    // Configuration constants
    private const int MaxTimestampAgeSeconds = 300; // 5 minutes for signature validity
    private const int AccessTokenExpiryMinutes = 60; // 1 hour
    private const int RefreshTokenExpiryDays = 7;

    /// <summary>
    /// Constructor matching Program.cs registration (4 parameters)
    /// </summary>
    public UserService(
        DataStore dataStore,
        IBlockchainService blockchainService,
        ILogger<UserService> logger, 
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _dataStore = dataStore;
        _blockchainService = blockchainService;
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
    }

    // =====================================================
    // WALLET AUTHENTICATION - CORE IMPLEMENTATION
    // =====================================================

    /// <summary>
    /// Authenticate user with Ethereum wallet signature
    /// Uses EIP-191 signature verification via Nethereum
    /// In Development mode, allows mock signatures for testing
    /// </summary>
    public async Task<AuthResponse?> AuthenticateWithWalletAsync(WalletAuthRequest request)
    {
        try
        {
            _logger.LogInformation("Wallet auth attempt for: {Wallet}", request.WalletAddress);

            // 1. Validate timestamp to prevent replay attacks
            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var timeDiff = Math.Abs(currentTimestamp - request.Timestamp);
            
            if (timeDiff > MaxTimestampAgeSeconds)
            {
                _logger.LogWarning("Timestamp too old/future: {Diff}s (max: {Max}s)", timeDiff, MaxTimestampAgeSeconds);
                return null;
            }

            // 2. Verify the signature (or bypass in development mode)
            bool signatureValid = false;
            
            if (_environment.IsDevelopment() && IsMockSignature(request.Signature))
            {
                // Development mode: allow mock signatures for testing
                _logger.LogWarning("DEV MODE: Accepting mock signature for wallet {Wallet}", request.WalletAddress);
                signatureValid = true;
            }
            else
            {
                // Production mode: verify real signature
                var recoveredAddress = RecoverAddressFromSignature(request.Message, request.Signature);
                
                if (string.IsNullOrEmpty(recoveredAddress))
                {
                    _logger.LogWarning("Failed to recover address from signature");
                    return null;
                }

                // Compare addresses (case-insensitive)
                var providedAddress = request.WalletAddress.ToLowerInvariant();
                var recovered = recoveredAddress.ToLowerInvariant();
                
                if (providedAddress != recovered)
                {
                    _logger.LogWarning("Address mismatch: provided={Provided}, recovered={Recovered}", 
                        providedAddress, recovered);
                    return null;
                }

                // Verify the message contains the correct wallet address
                if (!request.Message.Contains(request.WalletAddress, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Message does not contain wallet address");
                    return null;
                }

                signatureValid = true;
            }

            if (!signatureValid)
            {
                return null;
            }

            // 3. Get or create user
            var user = await GetUserByWalletAsync(request.WalletAddress);
            if (user == null)
            {
                _logger.LogInformation("Creating new user for wallet: {Wallet}", request.WalletAddress);
                user = await CreateUserAsync(request.WalletAddress);
            }

            // 4. Update last login
            user.LastLoginAt = DateTime.UtcNow;
            await UpdateUserAsync(user);

            // 5. Generate tokens
            var accessToken = GenerateJwtToken(user);
            var refreshToken = GenerateRefreshToken(user.Id);
            var expiresAt = DateTime.UtcNow.AddMinutes(AccessTokenExpiryMinutes);

            _logger.LogInformation("Wallet auth successful for: {Wallet} (User: {UserId})", 
                request.WalletAddress, user.Id);

            return new AuthResponse(accessToken, refreshToken, expiresAt, user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during wallet authentication for: {Wallet}", request.WalletAddress);
            return null;
        }
    }

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
    /// Generate JWT access token
    /// </summary>
    private string GenerateJwtToken(User user)
    {
        var jwtKey = _configuration["Jwt:Key"] ?? "default-dev-key-change-in-production-min-32-chars!";
        var jwtIssuer = _configuration["Jwt:Issuer"] ?? "orchestrator";
        var jwtAudience = _configuration["Jwt:Audience"] ?? "orchestrator-client";

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim("wallet", user.WalletAddress),
            new Claim(ClaimTypes.Role, "user"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(AccessTokenExpiryMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Generate secure refresh token
    /// </summary>
    private string GenerateRefreshToken(string userId)
    {
        var tokenBytes = new byte[64];
        RandomNumberGenerator.Fill(tokenBytes);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        var info = new RefreshTokenInfo
        {
            UserId = userId,
            Token = token,
            ExpiresAt = DateTime.UtcNow.AddDays(RefreshTokenExpiryDays),
            CreatedAt = DateTime.UtcNow
        };

        lock (_refreshTokenLock)
        {
            // Clean up expired tokens
            var expiredTokens = _refreshTokens
                .Where(kvp => kvp.Value.ExpiresAt < DateTime.UtcNow)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var expired in expiredTokens)
            {
                _refreshTokens.Remove(expired);
            }

            _refreshTokens[token] = info;
        }

        return token;
    }

    /// <summary>
    /// Refresh access token using refresh token
    /// </summary>
    public async Task<AuthResponse?> RefreshTokenAsync(string refreshToken)
    {
        try
        {
            RefreshTokenInfo? tokenInfo;
            
            lock (_refreshTokenLock)
            {
                if (!_refreshTokens.TryGetValue(refreshToken, out tokenInfo))
                {
                    _logger.LogWarning("Refresh token not found");
                    return null;
                }

                if (tokenInfo.ExpiresAt < DateTime.UtcNow)
                {
                    _refreshTokens.Remove(refreshToken);
                    _logger.LogWarning("Refresh token expired");
                    return null;
                }

                // Remove old token (one-time use)
                _refreshTokens.Remove(refreshToken);
            }

            var user = await GetUserAsync(tokenInfo.UserId);
            if (user == null || user.Status != UserStatus.Active)
            {
                _logger.LogWarning("User not found or inactive: {UserId}", tokenInfo.UserId);
                return null;
            }

            // Generate new tokens
            var newAccessToken = GenerateJwtToken(user);
            var newRefreshToken = GenerateRefreshToken(user.Id);
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

    // =====================================================
    // USER CRUD OPERATIONS
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
        // Normalize wallet address to checksum format
        var normalizedAddress = new AddressUtil().ConvertToChecksumAddress(walletAddress);
        
        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            WalletAddress = normalizedAddress,
            DisplayName = null,
            Email = null,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow
        };

        await _dataStore.SaveUserAsync(user);
        _logger.LogInformation("User created: {UserId} ({Wallet})", user.Id, normalizedAddress);

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

        // Check for duplicate
        if (user.SshKeys.Any(k => k.PublicKey == request.PublicKey.Trim()))
        {
            _logger.LogWarning("Duplicate SSH key for user {UserId}", userId);
            return null;
        }

        var sshKey = new SshKey
        {
            Id = Guid.NewGuid().ToString(),
            Name = request.Name,
            PublicKey = request.PublicKey.Trim(),
            Fingerprint = GenerateSshKeyFingerprint(request.PublicKey),
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
        return Task.FromResult(key);
    }

    private bool ValidateSshKeyFormat(string publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
            return false;

        var parts = publicKey.Trim().Split(' ');
        if (parts.Length < 2)
            return false;

        // Check key type
        var validTypes = new[] { "ssh-rsa", "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521" };
        if (!validTypes.Contains(parts[0]))
            return false;

        // Check base64 encoding
        try
        {
            var base64 = parts[1];
            if (string.IsNullOrWhiteSpace(base64))
                return false;

            Convert.FromBase64String(base64);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GenerateSshKeyFingerprint(string publicKey)
    {
        try
        {
            var parts = publicKey.Trim().Split(' ');
            if (parts.Length < 2)
                return "invalid";

            var keyData = Convert.FromBase64String(parts[1]);
            var hash = MD5.HashData(keyData);

            return "MD5:" + string.Join(":", hash.Select(b => b.ToString("x2")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate SSH key fingerprint");
            return $"invalid-{Guid.NewGuid().ToString()[..8]}";
        }
    }

    // =====================================================
    // API KEY MANAGEMENT
    // =====================================================

    public async Task<CreateApiKeyResponse?> CreateApiKeyAsync(string userId, CreateApiKeyRequest request)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return null;

        var apiKeyBytes = new byte[32];
        RandomNumberGenerator.Fill(apiKeyBytes);
        var apiKey = $"dc_{Convert.ToBase64String(apiKeyBytes).Replace("+", "").Replace("/", "").Replace("=", "")}";

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
            apiKey,
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
        
        var user = _dataStore.Users.Values
            .FirstOrDefault(u => u.ApiKeys.Any(k => k.KeyHash == keyHash && 
                (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow)));

        return Task.FromResult(user != null);
    }

    public Task<User?> GetUserByApiKeyAsync(string apiKey)
    {
        var keyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        
        var user = _dataStore.Users.Values
            .FirstOrDefault(u => u.ApiKeys.Any(k => k.KeyHash == keyHash && 
                (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow)));

        return Task.FromResult(user);
    }

    // =====================================================
    // QUOTA MANAGEMENT
    // =====================================================

    public Task<bool> CheckQuotaAsync(string userId, int virtualCpuCores, long memoryBytes, long storageBytes)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return Task.FromResult(false);

        var quotas = user.Quotas;
        
        var cpuOk = quotas.CurrentVirtualCpuCores + virtualCpuCores <= quotas.MaxVirtualCpuCores;
        var memOk = quotas.CurrentMemoryBytes + memoryBytes <= quotas.MaxMemoryBytes;
        var storageOk = quotas.CurrentStorageBytes + storageBytes <= quotas.MaxStorageBytes;
        var vmOk = quotas.CurrentVms < quotas.MaxVms;

        return Task.FromResult(cpuOk && memOk && storageOk && vmOk);
    }

    public async Task UpdateQuotaUsageAsync(string userId, int cpuDelta, long memoryDelta, long storageDelta)
    {
        if (!_dataStore.Users.TryGetValue(userId, out var user))
            return;

        user.Quotas.CurrentVirtualCpuCores = Math.Max(0, user.Quotas.CurrentVirtualCpuCores + cpuDelta);
        user.Quotas.CurrentMemoryBytes = Math.Max(0, user.Quotas.CurrentMemoryBytes + memoryDelta);
        user.Quotas.CurrentStorageBytes = Math.Max(0, user.Quotas.CurrentStorageBytes + storageDelta);
        
        if (cpuDelta > 0) user.Quotas.CurrentVms++;
        else if (cpuDelta < 0) user.Quotas.CurrentVms = Math.Max(0, user.Quotas.CurrentVms - 1);

        await _dataStore.SaveUserAsync(user);
    }

    /// <summary>
    /// Get complete balance information for user
    /// Orchestrates: BlockchainService for on-chain data + DataStore for unpaid usage
    /// </summary>
    public async Task<BalanceInfo> GetUserBalanceInfoAsync(string userId)
    {
        var user = await GetUserAsync(userId);
        if (user == null)
        {
            throw new Exception($"User {userId} not found");
        }

        if (_blockchainService == null)
        {
            _logger.LogWarning("Required services not available, returning zero balance");
            return new BalanceInfo
            {
                ConfirmedBalance = 0,
                PendingDeposits = 0,
                UnpaidUsage = 0,
                AvailableBalance = 0,
                TotalBalance = 0,
                TokenSymbol = "USDC"
            };
        }

        // ✅ CONFIRMED BALANCE: From escrow contract (BlockchainService)
        var confirmedBalance = await _blockchainService.GetEscrowBalanceAsync(user.WalletAddress);

        // ✅ PENDING DEPOSITS: From recent blockchain events (BlockchainService)
        var pendingDeposits = await _blockchainService.GetPendingDepositsAsync(
            user.WalletAddress);

        var pendingAmount = pendingDeposits.Sum(d => d.Amount);

        // ✅ UNPAID USAGE: From database (direct query - no service dependency)
        var unpaidUsage = _dataStore.UsageRecords.Values
            .Where(u => u.UserId == userId && !u.SettledOnChain)
            .Sum(u => u.TotalCost);

        // Calculate final balances
        var availableBalance = confirmedBalance - unpaidUsage;
        var totalBalance = confirmedBalance + pendingAmount - unpaidUsage;

        _logger.LogDebug(
            "Balance for {UserId}: Confirmed={Confirmed}, Pending={Pending}, Unpaid={Unpaid}, Available={Available}",
            userId, confirmedBalance, pendingAmount, unpaidUsage, availableBalance);

        return new BalanceInfo
        {
            ConfirmedBalance = confirmedBalance,
            PendingDeposits = pendingAmount,
            UnpaidUsage = unpaidUsage,
            AvailableBalance = Math.Max(0, availableBalance), // Can't be negative
            TotalBalance = totalBalance,
            TokenSymbol = "USDC",
            PendingDepositsList = pendingDeposits
        };
    }

    /// <summary>
    /// Get available balance (legacy method for compatibility)
    /// </summary>
    public async Task<decimal> GetUserBalanceAsync(string userId)
    {
        var balanceInfo = await GetUserBalanceInfoAsync(userId);
        return balanceInfo.AvailableBalance;
    }

    /// <summary>
    /// Check if user has sufficient available balance
    /// Inline calculation - no dependency on SettlementService
    /// </summary>
    public async Task<bool> HasSufficientBalanceAsync(string userId, decimal requiredAmount)
    {
        try
        {
            var balanceInfo = await GetUserBalanceInfoAsync(userId);
            return balanceInfo.AvailableBalance >= requiredAmount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check balance for user {UserId}", userId);
            return false;
        }
    }

}

/// <summary>
/// Refresh token info for storage
/// </summary>
internal class RefreshTokenInfo
{
    public string UserId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}