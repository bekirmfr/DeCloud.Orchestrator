// ApiResponse<T> and ApiError moved to DeCloud.Shared.Contracts.
// All existing ApiResponse<T>.Ok(...) / ApiResponse<T>.Fail(...) callsites
// continue to compile unchanged — the static factory methods are identical.
global using DeCloud.Shared.Contracts;

namespace Orchestrator.Models;

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
    VmDeleting,
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
    public int MaintenanceNodes { get; set; }

    // VM statistics
    public int TotalVms { get; set; }
    public int PendingVms { get; set; }
    public int RunningVms { get; set; }
    public int StoppedVms { get; set; }

    // User statistics
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }

    // ========================================
    // CPU RESOURCE STATISTICS
    // ========================================

    public long TotalCpuCores { get; set; }

    // Point-based CPU tracking
    public long TotalComputePoints { get; set; }
    public long AvailableComputePoints { get; set; }
    public long UsedComputePoints { get; set; }
    public double ComputePointUtilizationPercent { get; set; }

    // ========================================
    // MEMORY & STORAGE STATISTICS
    // ========================================
    public long TotalMemoryBytes { get; set; }
    public double TotalMemoryGb => TotalMemoryBytes / (1024.0 * 1024.0 * 1024.0);
    public long AvailableMemoryBytes { get; set; }
    public double AvailableMemoryGb => AvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
    public long UsedMemoryBytes { get; set; }
    public double UsedMemoryGb => UsedMemoryBytes / (1024.0 * 1024.0 * 1024.0);
    public double MemoryUtilizationPercent { get; set; }

    public long TotalStorageBytes { get; set; }
    public double TotalStorageGb => TotalStorageBytes / (1024.0 * 1024.0 * 1024.0);
    public long AvailableStorageBytes { get; set; }
    public double AvailableStorageGb => AvailableStorageBytes / (1024.0 * 1024.0 * 1024.0);
    public long UsedStorageBytes { get; set; }
    public double UsedStorageGb => UsedStorageBytes / (1024.0 * 1024.0 * 1024.0);
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
/// Descriptor for an orchestrator-curated base image: the URL the node
/// should download from, and the SHA256 the downloaded bytes must hash to.
///
/// CONTENT ADDRESSING
/// The hash is the authoritative identity of the image. Two nodes that
/// resolve the same (imageId, arch) receive identical (Url, Sha256) and
/// must converge to byte-identical cached files. The URL is a fetch hint;
/// the hash is the contract.
///
/// EMPTY HASH IS PERMISSIVE
/// When <see cref="Sha256"/> is empty, the node downloads from
/// <see cref="Url"/>, computes the SHA256, records it into VmSpec, and
/// reports it back to the orchestrator via heartbeat. Subsequent
/// migrations of that VM carry the discovered hash and enforce strictly.
/// </summary>
/// <param name="Url">HTTPS download URL. Should be a stable, versioned
///   URL — avoid "latest" / "current" tags that drift between builds.</param>
/// <param name="Sha256">Lower-case hex SHA256 (64 chars), or empty for
///   permissive download-and-record mode.</param>
public sealed record BaseImageDescriptor(string Url, string Sha256);

/// <summary>
/// Available VM image — single authoritative registry entry.
///
/// CARRIES BOTH catalog display metadata (Name, OsFamily, etc.) and
/// deployment-time resolution (<see cref="ByArchitecture"/>). The
/// catalog and the deployment table cannot diverge because they ARE
/// the same record. See BASE_IMAGE_DESIGN.md §7.
/// </summary>
public class VmImage
{
    // ── Identity ─────────────────────────────────────────────────────
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OsFamily { get; set; } = string.Empty;        // linux, windows
    public string OsName { get; set; } = string.Empty;          // ubuntu, debian, etc.
    public string Version { get; set; } = string.Empty;
    public long SizeGb { get; set; }                            // informational; rough upstream size
    public bool IsPublic { get; set; } = true;                  // marketplace visibility
    public DateTime CreatedAt { get; set; }

    // ── Deployment data — per-arch (URL, SHA256) ─────────────────────
    /// <summary>
    /// Outer key: normalized arch tag from <see cref="NormaliseArchTag"/>
    /// — "amd64" | "arm64". Value: the descriptor the node uses to
    /// download and verify. Populated for every architecture this image
    /// supports.
    /// </summary>
    public Dictionary<string, BaseImageDescriptor> ByArchitecture { get; set; } = new();

    // ── Helpers ──────────────────────────────────────────────────────
    /// <summary>
    /// Normalise a raw architecture string (as reported by the node
    /// agent's ResourceDiscoveryService) to the short tag used as the
    /// key in <see cref="ByArchitecture"/>.
    ///
    /// Returns <c>null</c> for unrecognised architectures — callers
    /// should fall back to <c>"amd64"</c> for nodes that pre-date the
    /// Architecture field (null Architecture on CpuInfo).
    /// </summary>
    public static string? NormaliseArchTag(string? rawArchitecture) =>
        rawArchitecture?.Trim().ToLowerInvariant() switch
        {
            "x86_64" or "amd64" or "x64" => "amd64",
            "aarch64" or "arm64" => "arm64",
            _ => null,
        };
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
    public long DiskGb { get; set; }
    public decimal HourlyPriceUsd { get; set; }
    public decimal HourlyRateCrypto { get; set; }
    public string CryptoSymbol { get; set; } = "USDC";
}