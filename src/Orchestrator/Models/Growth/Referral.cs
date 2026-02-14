using MongoDB.Bson.Serialization.Attributes;

namespace Orchestrator.Models.Growth;

/// <summary>
/// Tracks a referral relationship between users.
/// When a referred user deploys VMs, the referrer earns credit.
/// </summary>
public class Referral
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// The user who made the referral (wallet address)
    /// </summary>
    public string ReferrerUserId { get; set; } = string.Empty;

    /// <summary>
    /// The user who was referred (wallet address)
    /// </summary>
    public string ReferredUserId { get; set; } = string.Empty;

    /// <summary>
    /// The referral code used
    /// </summary>
    public string ReferralCode { get; set; } = string.Empty;

    /// <summary>
    /// Status of the referral
    /// </summary>
    public ReferralStatus Status { get; set; } = ReferralStatus.Pending;

    /// <summary>
    /// Total credits earned by the referrer from this referral
    /// </summary>
    public decimal ReferrerCreditsEarned { get; set; }

    /// <summary>
    /// Total credits given to the referred user
    /// </summary>
    public decimal ReferredCreditsGiven { get; set; }

    /// <summary>
    /// Whether the referred user has deployed their first VM (activation)
    /// </summary>
    public bool IsActivated { get; set; }
    public DateTime? ActivatedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum ReferralStatus
{
    /// <summary>Referred user signed up but hasn't deployed a VM yet</summary>
    Pending,
    /// <summary>Referred user deployed first VM — credits awarded</summary>
    Activated,
    /// <summary>Referral expired (user never activated within window)</summary>
    Expired
}

/// <summary>
/// Referral program configuration
/// </summary>
public class ReferralConfig
{
    /// <summary>
    /// Credit amount given to the referrer when their referral activates (USDC)
    /// </summary>
    public decimal ReferrerReward { get; set; } = 5.00m;

    /// <summary>
    /// Credit amount given to the referred user on signup (USDC)
    /// </summary>
    public decimal ReferredBonus { get; set; } = 10.00m;

    /// <summary>
    /// Percentage of referred user's spending that the referrer earns (ongoing)
    /// </summary>
    public decimal OngoingCommissionPercent { get; set; } = 5.0m;

    /// <summary>
    /// How long the ongoing commission lasts
    /// </summary>
    public TimeSpan CommissionDuration { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// How long a referral code stays valid before expiring
    /// </summary>
    public TimeSpan ReferralExpiryDays { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Maximum number of active referrals per user
    /// </summary>
    public int MaxReferralsPerUser { get; set; } = 100;
}

/// <summary>
/// Referral stats for a user's dashboard
/// </summary>
public class ReferralStats
{
    public string UserId { get; set; } = string.Empty;
    public string ReferralCode { get; set; } = string.Empty;
    public string ReferralLink { get; set; } = string.Empty;

    public int TotalReferrals { get; set; }
    public int ActivatedReferrals { get; set; }
    public int PendingReferrals { get; set; }

    public decimal TotalCreditsEarned { get; set; }
    public decimal OngoingCommissionEarned { get; set; }

    public List<ReferralSummary> RecentReferrals { get; set; } = new();
}

public class ReferralSummary
{
    /// <summary>
    /// Truncated wallet address of referred user (e.g., "0x1234...abcd")
    /// </summary>
    public string ReferredUser { get; set; } = string.Empty;
    public ReferralStatus Status { get; set; }
    public decimal CreditsEarned { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
}
