namespace Orchestrator.Models;

/// <summary>
/// Represents a user/tenant in the system
/// </summary>
public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string WalletAddress { get; set; } = string.Empty;
    
    // Profile
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    
    // State
    public UserStatus Status { get; set; } = UserStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
    
    // Quotas
    public UserQuotas Quotas { get; set; } = new();
    
    // Balance
    public decimal CryptoBalance { get; set; }
    public string BalanceToken { get; set; } = "USDC";
    
    // SSH keys for VM access
    public List<SshKey> SshKeys { get; set; } = new();
    
    // API keys
    public List<ApiKey> ApiKeys { get; set; } = new();
}

public class UserQuotas
{
    public int MaxVms { get; set; } = 10;
    public int MaxCpuCores { get; set; } = 32;
    public long MaxMemoryMb { get; set; } = 65536;    // 64GB
    public long MaxStorageGb { get; set; } = 500;
    
    // Current usage
    public int CurrentVms { get; set; }
    public int CurrentCpuCores { get; set; }
    public long CurrentMemoryMb { get; set; }
    public long CurrentStorageGb { get; set; }
}

public class SshKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ApiKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;      // Hashed, never store plain
    public string KeyPrefix { get; set; } = string.Empty;    // First 8 chars for display
    public List<string> Scopes { get; set; } = new();        // Permissions
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public enum UserStatus
{
    Active,
    Suspended,
    Banned
}

// Auth DTOs
public record WalletAuthRequest(
    string WalletAddress,
    string Signature,
    string Message,
    long Timestamp
);

public record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    User User
);

public record CreateApiKeyRequest(
    string Name,
    List<string>? Scopes,
    DateTime? ExpiresAt
);

public record CreateApiKeyResponse(
    string KeyId,
    string ApiKey,      // Only returned once!
    string KeyPrefix,
    DateTime CreatedAt,
    DateTime? ExpiresAt
);

public record AddSshKeyRequest(
    string Name,
    string PublicKey
);
