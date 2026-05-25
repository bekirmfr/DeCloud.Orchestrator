namespace Orchestrator.Models.Payment;

/// <summary>
/// Extended billing information for VMs with attestation tracking
/// 
/// This extends the existing VmBillingInfo model.
/// Add these properties to your existing model.
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
    // NEW PROPERTIES FOR ATTESTATION TRACKING
    // Add these to your existing class
    // =====================================================

    /// <summary>
    /// Runtime where attestation was passing (verified resources)
    /// Only this time is billed to the user
    /// </summary>
    public TimeSpan VerifiedRuntime { get; set; }

    /// <summary>
    /// LEGACY — no longer incremented after the attestation system was removed.
    /// Retained on the model to preserve existing MongoDB document compatibility.
    /// All billable runtime now accrues to <see cref="VerifiedRuntime"/>.
    /// Scheduled for removal in a future schema migration.
    /// </summary>
    [Obsolete("No longer incremented. All runtime accrues to VerifiedRuntime.")]
    public TimeSpan UnverifiedRuntime { get; set; }

    // =====================================================
    // COMPUTED PROPERTIES (optional but useful)
    // =====================================================

    /// <summary>
    /// Percentage of runtime that was verified
    /// </summary>
    public double VerificationRate =>
        TotalRuntime.TotalMinutes > 0
            ? VerifiedRuntime.TotalMinutes / TotalRuntime.TotalMinutes * 100.0
            : 100.0;

    /// <summary>
    /// Is the VM currently in good standing (attestation passing)?
    /// </summary>
    public bool IsVerified => UnverifiedRuntime.TotalMinutes == 0 ||
        VerifiedRuntime > UnverifiedRuntime;
}

/// <summary>
/// If you need to keep the existing VmBillingInfo unchanged,
/// use this class instead and map between them.
/// </summary>
public class AttestationAwareBillingInfo
{
    // Core billing
    public decimal HourlyRateCrypto { get; set; }
    public string CryptoSymbol { get; set; } = "USDC";
    public decimal TotalChargedCrypto { get; set; }
    public DateTime? LastBillingAt { get; set; }

    // Attestation tracking (both intervals billed at full rate — unverified is
    // recorded for observability only, not discounted)
    public int VerifiedRuntimeMinutes { get; set; }
    public int UnverifiedRuntimeMinutes { get; set; }

    // Status
    public string? StoppedReason { get; set; }
    public string? StoppedAt { get; set; }

    // Computed
    public double VerificationRate =>
        (VerifiedRuntimeMinutes + UnverifiedRuntimeMinutes) > 0
            ? (double)VerifiedRuntimeMinutes / (VerifiedRuntimeMinutes + UnverifiedRuntimeMinutes) * 100.0
            : 100.0;

    public TimeSpan VerifiedRuntime => TimeSpan.FromMinutes(VerifiedRuntimeMinutes);
    public TimeSpan UnverifiedRuntime => TimeSpan.FromMinutes(UnverifiedRuntimeMinutes);
    public TimeSpan TotalRuntime => TimeSpan.FromMinutes(VerifiedRuntimeMinutes + UnverifiedRuntimeMinutes);
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
    Manual,             // Admin trigger
    BalanceAdded,       // User added balance - resume billing
    HeartbeatResumed    // Node heartbeat returned after staleness - resume billing
}