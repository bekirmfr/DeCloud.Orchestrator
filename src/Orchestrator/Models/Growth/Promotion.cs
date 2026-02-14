using MongoDB.Bson.Serialization.Attributes;

namespace Orchestrator.Models.Growth;

/// <summary>
/// Promotional credit grant (free trial, referral bonus, campaign credit, etc.)
/// Credits are tracked separately from on-chain deposits.
/// They are consumed first before touching user's escrow balance.
/// </summary>
public class CreditGrant
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User wallet address who receives the credit
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Type of credit (FreeTrial, ReferralBonus, ReferrerReward, PromoCampaign, VolumeDiscount)
    /// </summary>
    public CreditType Type { get; set; }

    /// <summary>
    /// Original amount granted (USDC equivalent)
    /// </summary>
    public decimal OriginalAmount { get; set; }

    /// <summary>
    /// Remaining balance (decremented as usage occurs)
    /// </summary>
    public decimal RemainingAmount { get; set; }

    /// <summary>
    /// Source identifier (referral ID, promo code, campaign name)
    /// </summary>
    public string? SourceId { get; set; }

    /// <summary>
    /// Human-readable description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// When this credit expires (null = never)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum CreditType
{
    /// <summary>First-time signup bonus</summary>
    FreeTrial,
    /// <summary>Bonus for being referred by another user</summary>
    ReferralBonus,
    /// <summary>Reward for referring a new user</summary>
    ReferrerReward,
    /// <summary>Ongoing referral commission</summary>
    ReferralCommission,
    /// <summary>Promotional campaign credit</summary>
    PromoCampaign,
    /// <summary>Volume usage discount</summary>
    VolumeDiscount
}

/// <summary>
/// Promotional campaign (e.g., "Launch Week", "AI Summer", "Holiday Sale")
/// </summary>
public class PromoCampaign
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Campaign name (e.g., "launch-week-2026")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Promo code users enter (e.g., "DECLOUD10")
    /// </summary>
    public string PromoCode { get; set; } = string.Empty;

    /// <summary>
    /// Credit amount per redemption (USDC)
    /// </summary>
    public decimal CreditAmount { get; set; }

    /// <summary>
    /// Maximum total redemptions allowed
    /// </summary>
    public int MaxRedemptions { get; set; } = 1000;

    /// <summary>
    /// Current redemption count
    /// </summary>
    public int CurrentRedemptions { get; set; }

    /// <summary>
    /// Campaign start date
    /// </summary>
    public DateTime StartsAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Campaign end date
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// How long credits last after being granted
    /// </summary>
    public int CreditValidityDays { get; set; } = 30;

    /// <summary>
    /// Whether the campaign is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Volume discount tier — spend more, save more
/// </summary>
public class VolumeDiscountTier
{
    /// <summary>
    /// Minimum monthly spend (USDC) to qualify for this tier
    /// </summary>
    public decimal MinMonthlySpend { get; set; }

    /// <summary>
    /// Discount percentage applied to usage
    /// </summary>
    public decimal DiscountPercent { get; set; }

    /// <summary>
    /// Tier name (e.g., "Starter", "Growth", "Scale", "Enterprise")
    /// </summary>
    public string TierName { get; set; } = string.Empty;
}

/// <summary>
/// Complete credit balance info for a user
/// </summary>
public class UserCreditBalance
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Total available promotional credits (sum of all non-expired grants)
    /// </summary>
    public decimal TotalCredits { get; set; }

    /// <summary>
    /// Breakdown by credit type
    /// </summary>
    public List<CreditGrantSummary> ActiveGrants { get; set; } = new();

    /// <summary>
    /// Current volume discount tier (if any)
    /// </summary>
    public string? VolumeDiscountTier { get; set; }
    public decimal VolumeDiscountPercent { get; set; }
}

public class CreditGrantSummary
{
    public string Id { get; set; } = string.Empty;
    public CreditType Type { get; set; }
    public decimal RemainingAmount { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
}
