namespace Orchestrator.Models.Payment;

/// <summary>
/// Extended billing information for VMs with attestation tracking
/// 
/// This extends the existing VmBillingInfo model.
/// Add these properties to your existing model.
/// </summary>
public partial class VmBillingInfo
{
    // =====================================================
    // EXISTING PROPERTIES (keep these)
    // =====================================================
    // public decimal HourlyRateCrypto { get; set; }
    // public string CryptoSymbol { get; set; } = "USDC";
    // public decimal TotalChargedCrypto { get; set; }
    // public DateTime? LastBillingAt { get; set; }

    // =====================================================
    // NEW ATTESTATION-AWARE BILLING PROPERTIES
    // =====================================================

    /// <summary>
    /// Total minutes of verified runtime (attestation passing)
    /// Only billed time is counted here
    /// </summary>
    public int VerifiedRuntimeMinutes { get; set; }

    /// <summary>
    /// Total minutes of unverified runtime (attestation failing)
    /// This time is NOT billed to the user
    /// </summary>
    public int UnverifiedRuntimeMinutes { get; set; }

    /// <summary>
    /// Reason VM was stopped (if stopped)
    /// </summary>
    public string? StoppedReason { get; set; }

    /// <summary>
    /// When VM was stopped
    /// </summary>
    public DateTime? StoppedAt { get; set; }

    /// <summary>
    /// Billing verification rate (verified / total runtime)
    /// </summary>
    public double VerificationRate =>
        (VerifiedRuntimeMinutes + UnverifiedRuntimeMinutes) > 0
            ? (double)VerifiedRuntimeMinutes / (VerifiedRuntimeMinutes + UnverifiedRuntimeMinutes) * 100.0
            : 100.0;
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

    // Attestation tracking
    public int VerifiedRuntimeMinutes { get; set; }
    public int UnverifiedRuntimeMinutes { get; set; }

    // Status
    public string? StoppedReason { get; set; }
    public DateTime? StoppedAt { get; set; }

    // Computed
    public double VerificationRate =>
        (VerifiedRuntimeMinutes + UnverifiedRuntimeMinutes) > 0
            ? (double)VerifiedRuntimeMinutes / (VerifiedRuntimeMinutes + UnverifiedRuntimeMinutes) * 100.0
            : 100.0;

    public TimeSpan VerifiedRuntime => TimeSpan.FromMinutes(VerifiedRuntimeMinutes);
    public TimeSpan UnverifiedRuntime => TimeSpan.FromMinutes(UnverifiedRuntimeMinutes);
    public TimeSpan TotalRuntime => TimeSpan.FromMinutes(VerifiedRuntimeMinutes + UnverifiedRuntimeMinutes);
}