using Orchestrator.Models;

namespace Orchestrator.Interfaces
{
    public interface IUserService
    {
        Task<User?> GetUserByIdAsync(string userId);
        Task<User?> GetUserByWalletAsync(string walletAddress);
        Task<User?> GetUserByApiKeyAsync(string apiKey);
        Task<User> CreateUserAsync(string walletAddress);
        Task UpdateUserAsync(User user);
        Task<bool> DeleteUserAsync(string userId);

        // SSH Key Management
        Task<SshKey?> AddSshKeyAsync(string userId, AddSshKeyRequest request);
        Task<bool> RemoveSshKeyAsync(string userId, string keyId);
        Task<List<SshKey>> GetSshKeysAsync(string userId);

        // API Key Management
        Task<CreateApiKeyResponse?> CreateApiKeyAsync(string userId, CreateApiKeyRequest request);
        Task<bool> RevokeApiKeyAsync(string userId, string keyId);
        Task<List<ApiKey>> GetApiKeysAsync(string userId);
        Task<User?> ValidateApiKeyAsync(string apiKey);

        // Authentication
        Task<AuthResponse?> AuthenticateWithWalletAsync(WalletAuthRequest request);
        Task<AuthResponse?> RefreshTokenAsync(string refreshToken);

        /// <summary>Return the wallet for a valid refresh token without rotating it (SIWE getSession).</summary>
        Task<string?> GetSessionWalletAsync(string refreshToken);

        /// <summary>Revoke a refresh token (logout).</summary>
        Task LogoutAsync(string refreshToken, CancellationToken ct = default);

        /// <summary>Verify an EIP-191 wallet signature recovers to walletAddress.</summary>
        bool VerifyWalletSignature(string walletAddress, string message, string signature);
    }
}