namespace Orchestrator.Models;

/// <summary>
/// Standard API response wrapper
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public ApiError? Error { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }

    public static ApiResponse<T> Ok(T data, Dictionary<string, object>? metadata = null) => new()
    {
        Success = true,
        Data = data,
        Metadata = metadata
    };

    public static ApiResponse<T> Fail(string code, string message, Dictionary<string, object>? details = null) => new()
    {
        Success = false,
        Error = new ApiError(code, message, details)
    };
}

public record ApiError(
    string Code,
    string Message,
    Dictionary<string, object>? Details = null
);

/// <summary>
/// Pagination wrapper
/// </summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

/// <summary>
/// Query parameters for listing resources
/// </summary>
public record ListQueryParams(
    int Page = 1,
    int PageSize = 20,
    string? SortBy = null,
    bool SortDescending = false,
    string? Search = null,
    Dictionary<string, string>? Filters = null
);

/// <summary>
/// Event for pub/sub notifications
/// </summary>
public class OrchestratorEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public EventType Type { get; set; }
    public string ResourceType { get; set; } = string.Empty;   // "vm", "node", "user"
    public string ResourceId { get; set; } = string.Empty;
    public string? NodeId { get; set; }                        // FIXED: Added for node/VM event association
    public string? UserId { get; set; }                        // Associated user ID
    public object? Payload { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum EventType
{
    // VM events
    VmCreated,
    VmScheduled,
    VmProvisioning,
    VmStarted,
    VmStopped,
    VmDeleted,
    VmRecovered,
    VmError,
    VmMetricsUpdated,
    VmStateChanged,     // Added for state synchronization from heartbeats

    // Node events
    NodeRegistered,
    NodeOnline,
    NodeOffline,
    NodeDraining,
    NodeMetricsUpdated,

    // User events
    UserCreated,
    UserLoggedIn,
    BalanceUpdated
}

/// <summary>
/// System-wide statistics
/// </summary>
public class SystemStats
{
    // Node statistics
    public int TotalNodes { get; set; }
    public int OnlineNodes { get; set; }
    public int OfflineNodes { get; set; }

    // VM statistics
    public int TotalVms { get; set; }
    public int RunningVms { get; set; }
    public int StoppedVms { get; set; }

    // User statistics
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }

    // Resource statistics
    public long TotalCpuCores { get; set; }
    public long AvailableCpuCores { get; set; }
    public long UsedCpuCores { get; set; }
    public double CpuUtilizationPercent { get; set; }

    public long TotalMemoryMb { get; set; }
    public long AvailableMemoryMb { get; set; }
    public long UsedMemoryMb { get; set; }
    public double MemoryUtilizationPercent { get; set; }

    public long TotalStorageGb { get; set; }
    public long AvailableStorageGb { get; set; }
    public long UsedStorageGb { get; set; }
    public double StorageUtilizationPercent { get; set; }
}

/// <summary>
/// Health check response
/// </summary>
public record HealthStatus(
    string Status,
    DateTime Timestamp,
    Dictionary<string, ComponentHealth> Components
);

public record ComponentHealth(
    string Status,
    string? Message,
    Dictionary<string, object>? Details
);

/// <summary>
/// Available VM images
/// </summary>
public class VmImage
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OsFamily { get; set; } = string.Empty;        // linux, windows
    public string OsName { get; set; } = string.Empty;          // ubuntu, debian, etc.
    public string Version { get; set; } = string.Empty;
    public string Architecture { get; set; } = "x86_64";
    public long SizeGb { get; set; }
    public string? ChecksumSha256 { get; set; }
    public string? DownloadUrl { get; set; }
    public bool IsPublic { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Pricing tiers
/// </summary>
public class VmPricingTier
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;           // e.g., "small", "medium", "large"
    public int CpuCores { get; set; }
    public long MemoryMb { get; set; }
    public long DiskGb { get; set; }                           // FIXED: Changed from StorageGb to match DataStore usage
    public decimal HourlyPriceUsd { get; set; }
    public decimal HourlyRateCrypto { get; set; }              // FIXED: Changed from HourlyPriceCrypto to match DataStore usage
    public string CryptoSymbol { get; set; } = "USDC";
}