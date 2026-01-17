namespace Orchestrator.Models;

/// <summary>
/// User/tenant in the system
/// </summary>
public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string WalletAddress { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Active;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;

    // SSH Keys for VMs
    public List<SshKey> SshKeys { get; set; } = new();

    // API Keys
    public List<ApiKey> ApiKeys { get; set; } = new();

    // Quotas
    public UserQuotas Quotas { get; set; } = new();

    // Preferences
    public Dictionary<string, string> Preferences { get; set; } = new();
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
    public string KeyHash { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public class UserQuotas
{
    public int MaxVms { get; set; } = 5;
    public int MaxVirtualCpuCores { get; set; } = 16;
    public long MaxMemoryBytes { get; set; } = 32768;
    public long MaxStorageBytes { get; set; } = 500;

    public int CurrentVms { get; set; }
    public int CurrentVirtualCpuCores { get; set; }
    public long CurrentMemoryBytes { get; set; }
    public long CurrentStorageBytes { get; set; }
}

public enum UserStatus
{
    Active,
    Suspended,
    Deleted
}

/// <summary>
/// Request to authenticate with crypto wallet signature
/// </summary>
public record WalletAuthRequest(
    string WalletAddress,
    string Signature,
    string Message,
    long Timestamp
);

// DTOs for API
public record AuthRequest(
    string WalletAddress,
    string? Signature = null,
    string? Message = null
);

public record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    User User
);

public record AddSshKeyRequest(
    string Name,
    string PublicKey
);

public record CreateApiKeyRequest(
    string Name,
    List<string>? Scopes = null,
    DateTime? ExpiresAt = null
);

public record CreateApiKeyResponse(
    string KeyId,
    string ApiKey,
    string KeyPrefix,
    DateTime CreatedAt,
    DateTime? ExpiresAt
);