namespace Orchestrator.Models.Payment;

/// <summary>
/// Per-VM billing state: rate, accumulated cost, runtime, and pause status.
/// </summary>
public class VmBillingInfo
{
    // =====================================================
    // EXISTING PROPERTIES (you already have these)
    // =====================================================

    /// <summary>
    /// Hourly rate in cryptocurrency
    /// </summary>
    public decimal HourlyRateCrypto { get; set; }

    /// <summary>
    /// Cryptocurrency symbol (e.g., "USDC")
    /// </summary>
    public string CryptoSymbol { get; set; } = "USDC";

    /// <summary>
    /// Total amount billed to user
    /// </summary>
    public decimal TotalBilled { get; set; }

    /// <summary>
    /// Total runtime duration
    /// </summary>
    public TimeSpan TotalRuntime { get; set; }

    /// <summary>
    /// Last time billing was processed
    /// </summary>
    public DateTime? LastBillingAt { get; set; }
    public DateTime? CurrentPeriodStart { get; set; }
    public bool IsPaused { get; set; }
    public DateTime? PausedAt { get; set; }
    public string? PauseReason { get; set; }

    // =====================================================
    // PROPERTIES FOR ATTESTATION TRACKING
    // =====================================================

    /// <summary>
    /// Total runtime — accrued every billing cycle while the node heartbeat
    /// is fresh. Renamed conceptually from "VerifiedRuntime" but the field
    /// name is preserved for MongoDB document compatibility.
    /// </summary>
    public TimeSpan VerifiedRuntime { get; set; }
}

public class BillingEvent
{
    public string VmId { get; set; } = string.Empty;
    public BillingTrigger Trigger { get; set; }
    public string? Reason { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum BillingTrigger
{
    Periodic,           // Periodic timer (every 5 min)
    VmStop,             // VM stopped - bill final usage
    VmStart,   // VM entered Running — open a fresh billing period, bill nothing
    Manual,             // Admin trigger
    BalanceAdded,       // User added balance - resume billing
    HeartbeatResumed    // Node heartbeat returned after staleness - resume billing
}