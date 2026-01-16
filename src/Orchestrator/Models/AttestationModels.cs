namespace Orchestrator.Models;

/// <summary>
/// Attestation challenge sent to VM
/// Generated fresh for each challenge - no persistent keys needed
/// </summary>
public class AttestationChallenge
{
    public string ChallengeId { get; set; } = Guid.NewGuid().ToString();
    public string VmId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;

    /// <summary>
    /// Cryptographic nonce - prevents replay attacks
    /// </summary>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>
    /// Unix timestamp in milliseconds
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Expected resources from VM spec
    /// </summary>
    public int ExpectedCores { get; set; }
    public long ExpectedMemoryMb { get; set; }

    /// <summary>
    /// When the challenge was sent (for timing verification)
    /// </summary>
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Challenge expires after this time
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Response from VM attestation agent
/// Contains ephemeral public key - private key only exists in VM memory for milliseconds
/// </summary>
public class AttestationResponse
{
    /// <summary>
    /// Must match the challenge nonce
    /// </summary>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>
    /// Ephemeral Ed25519 public key (hex encoded)
    /// Generated fresh for this challenge only
    /// </summary>
    public string EphemeralPubKey { get; set; } = string.Empty;

    /// <summary>
    /// System metrics collected from VM
    /// </summary>
    public AttestationMetrics Metrics { get; set; } = new();

    /// <summary>
    /// Memory touch test results - proves real RAM exists
    /// </summary>
    public MemoryTouchResult MemoryTouch { get; set; } = new();

    /// <summary>
    /// Timing breakdown inside VM
    /// </summary>
    public AttestationTiming Timing { get; set; } = new();

    /// <summary>
    /// Ed25519 signature over canonical message (hex encoded)
    /// Signed with ephemeral private key that's zeroed immediately after
    /// </summary>
    public string Signature { get; set; } = string.Empty;
}

/// <summary>
/// Metrics collected from /proc/* inside VM
/// </summary>
public class AttestationMetrics
{
    public int CpuCores { get; set; }
    public long MemoryKb { get; set; }
    public long MemoryFreeKb { get; set; }
    public double LoadAvg1 { get; set; }
    public double LoadAvg5 { get; set; }
    public double LoadAvg15 { get; set; }
    public double UptimeSeconds { get; set; }

    /// <summary>
    /// Changes on each boot - detects VM swaps mid-session
    /// </summary>
    public string BootId { get; set; } = string.Empty;

    /// <summary>
    /// Persistent machine ID - detects VM replacement
    /// </summary>
    public string MachineId { get; set; } = string.Empty;
}

/// <summary>
/// Memory touch test results
/// Proves VM has real RAM (not swap) by measuring access timing
/// </summary>
public class MemoryTouchResult
{
    /// <summary>
    /// Size of memory allocated (typically 16MB)
    /// </summary>
    public int AllocatedKb { get; set; }

    /// <summary>
    /// Number of random pages touched (typically 64)
    /// </summary>
    public int PagesTouched { get; set; }

    /// <summary>
    /// Total time for all page touches
    /// Real RAM: <20ms, Swap: >100ms
    /// </summary>
    public double TotalMs { get; set; }

    /// <summary>
    /// Slowest single page access
    /// Real RAM: <2ms, Swap thrashing: >10ms
    /// </summary>
    public double MaxPageMs { get; set; }

    /// <summary>
    /// SHA256 hash of touched memory content
    /// Proves actual memory operations occurred
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;
}

/// <summary>
/// Timing breakdown of attestation processing inside VM
/// </summary>
public class AttestationTiming
{
    public double KeyGenMs { get; set; }
    public double MetricsMs { get; set; }
    public double MemoryTouchMs { get; set; }
    public double SigningMs { get; set; }
    public double TotalMs { get; set; }
}

/// <summary>
/// Result of attestation verification
/// </summary>
public class AttestationVerificationResult
{
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    // Individual check results
    public bool TimingValid { get; set; }
    public bool SignatureValid { get; set; }
    public bool NonceValid { get; set; }
    public bool CpuValid { get; set; }
    public bool MemoryValid { get; set; }
    public bool MemoryTouchValid { get; set; }
    public bool IdentityValid { get; set; }

    /// <summary>
    /// Round-trip time from challenge send to response receive
    /// </summary>
    public double ResponseTimeMs { get; set; }

    /// <summary>
    /// When the verification was performed
    /// </summary>
    public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Tracks VM liveness state across attestations
/// </summary>
public class VmLivenessState
{
    public string VmId { get; set; } = string.Empty;

    /// <summary>
    /// Last seen boot ID - changes on VM reboot
    /// </summary>
    public string? LastBootId { get; set; }

    /// <summary>
    /// Last seen machine ID - should never change
    /// </summary>
    public string? LastMachineId { get; set; }

    /// <summary>
    /// When the last successful attestation occurred
    /// </summary>
    public DateTime? LastSuccessfulAttestation { get; set; }

    /// <summary>
    /// Current streak of consecutive failures
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Current streak of consecutive successes
    /// </summary>
    public int ConsecutiveSuccesses { get; set; }

    /// <summary>
    /// Total challenges sent to this VM
    /// </summary>
    public int TotalChallenges { get; set; }

    /// <summary>
    /// Total successful attestations
    /// </summary>
    public int TotalSuccesses { get; set; }

    /// <summary>
    /// Success rate (0-100)
    /// </summary>
    public double SuccessRate => TotalChallenges > 0
        ? (double)TotalSuccesses / TotalChallenges * 100.0
        : 0;

    /// <summary>
    /// Is billing currently paused due to failures?
    /// </summary>
    public bool BillingPaused { get; set; }

    /// <summary>
    /// Reason for billing pause
    /// </summary>
    public string? PauseReason { get; set; }

    /// <summary>
    /// When billing was paused
    /// </summary>
    public DateTime? PausedAt { get; set; }
}

/// <summary>
/// Attestation configuration
/// </summary>
public class AttestationConfig
{
    /// <summary>
    /// Maximum allowed response time in milliseconds
    /// THIS IS THE KEY SECURITY PARAMETER
    /// Must be short enough that node can't pause VM and extract ephemeral key
    /// </summary>
    public int MaxResponseTimeMs { get; set; } = 100;

    /// <summary>
    /// Challenge interval during startup period (more frequent to catch fraud early)
    /// </summary>
    public int StartupChallengeIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Challenge interval after startup period (hourly)
    /// </summary>
    public int NormalChallengeIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// How long the startup period lasts
    /// </summary>
    public int StartupPeriodMinutes { get; set; } = 5;

    /// <summary>
    /// Maximum allowed total memory touch time (detects swap)
    /// </summary>
    public double MaxMemoryTouchMs { get; set; } = 50;

    /// <summary>
    /// Maximum allowed single page touch time (detects swap thrashing)
    /// </summary>
    public double MaxSinglePageTouchMs { get; set; } = 5;

    /// <summary>
    /// Memory tolerance - allow some variance from expected
    /// </summary>
    public double MemoryToleranceLow { get; set; } = 0.85;
    public double MemoryToleranceHigh { get; set; } = 1.15;

    /// <summary>
    /// Consecutive failures before pausing billing
    /// </summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>
    /// Consecutive successes required to resume billing
    /// </summary>
    public int RecoveryThreshold { get; set; } = 2;
}

/// <summary>
/// Attestation record for audit trail
/// </summary>
public class Attestation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string VmId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ChallengeId { get; set; } = string.Empty;

    public bool Success { get; set; }
    public double ResponseTimeMs { get; set; }

    /// <summary>
    /// Metrics reported by VM (if any)
    /// </summary>
    public AttestationMetrics? ReportedMetrics { get; set; }

    /// <summary>
    /// Errors if attestation failed
    /// </summary>
    public List<string> Errors { get; set; } = new();

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Extension to VmBillingInfo for attestation-aware billing
/// </summary>
public class AttestationBillingInfo
{
    /// <summary>
    /// Last time billing was successfully processed
    /// </summary>
    public DateTime? LastBilledAt { get; set; }

    /// <summary>
    /// Total verified runtime (only when attestation passing)
    /// </summary>
    public TimeSpan VerifiedRuntime { get; set; }

    /// <summary>
    /// Total unverified runtime (when attestation failing)
    /// </summary>
    public TimeSpan UnverifiedRuntime { get; set; }

    /// <summary>
    /// Is billing currently active?
    /// </summary>
    public bool IsBillingActive { get; set; } = true;
}