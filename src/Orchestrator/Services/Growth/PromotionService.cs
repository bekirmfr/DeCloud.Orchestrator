using Orchestrator.Models.Growth;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Growth;

public interface IPromotionService
{
    /// <summary>Grant promotional credits to a user</summary>
    Task<CreditGrant> GrantCreditAsync(string userId, CreditType type, decimal amount, string description, string? sourceId = null, TimeSpan? validFor = null);

    /// <summary>Grant free trial credits on first signup</summary>
    Task GrantFreeTrialAsync(string userId);

    /// <summary>Redeem a promo code</summary>
    Task<(bool Success, string Message, decimal Amount)> RedeemPromoCodeAsync(string userId, string promoCode);

    /// <summary>Get user's total available credits</summary>
    Task<UserCreditBalance> GetCreditBalanceAsync(string userId);

    /// <summary>Consume credits for VM usage (called before touching escrow balance)</summary>
    Task<decimal> ConsumeCreditsAsync(string userId, decimal amount);

    /// <summary>Get volume discount tier for a user based on monthly spend</summary>
    Task<(string TierName, decimal DiscountPercent)> GetVolumeDiscountAsync(string userId);

    /// <summary>Create a new promotional campaign</summary>
    Task<PromoCampaign> CreateCampaignAsync(PromoCampaign campaign);

    /// <summary>Get all active campaigns (admin)</summary>
    Task<List<PromoCampaign>> GetActiveCampaignsAsync();
}

public class PromotionService : IPromotionService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<PromotionService> _logger;

    // Free trial amount for new users
    private const decimal FreeTrialAmount = 5.00m;
    private const int FreeTrialValidDays = 14;

    // Volume discount tiers
    private static readonly List<VolumeDiscountTier> VolumeTiers = new()
    {
        new VolumeDiscountTier { MinMonthlySpend = 500m, DiscountPercent = 15m, TierName = "Enterprise" },
        new VolumeDiscountTier { MinMonthlySpend = 200m, DiscountPercent = 10m, TierName = "Scale" },
        new VolumeDiscountTier { MinMonthlySpend = 50m,  DiscountPercent = 5m,  TierName = "Growth" },
        new VolumeDiscountTier { MinMonthlySpend = 0m,   DiscountPercent = 0m,  TierName = "Starter" }
    };

    public PromotionService(DataStore dataStore, ILogger<PromotionService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    public async Task<CreditGrant> GrantCreditAsync(
        string userId, CreditType type, decimal amount,
        string description, string? sourceId = null, TimeSpan? validFor = null)
    {
        var grant = new CreditGrant
        {
            UserId = userId,
            Type = type,
            OriginalAmount = amount,
            RemainingAmount = amount,
            Description = description,
            SourceId = sourceId,
            ExpiresAt = validFor.HasValue ? DateTime.UtcNow.Add(validFor.Value) : null
        };

        await _dataStore.SaveCreditGrantAsync(grant);

        _logger.LogInformation(
            "Credit granted: {Type} ${Amount} to {UserId} — {Description}",
            type, amount, userId, description);

        return grant;
    }

    public async Task GrantFreeTrialAsync(string userId)
    {
        // Check if user already received free trial
        var existingGrants = await _dataStore.GetCreditGrantsForUserAsync(userId);
        if (existingGrants.Any(g => g.Type == CreditType.FreeTrial))
        {
            _logger.LogDebug("User {UserId} already received free trial credits", userId);
            return;
        }

        await GrantCreditAsync(
            userId,
            CreditType.FreeTrial,
            FreeTrialAmount,
            $"Welcome to DeCloud! ${FreeTrialAmount} free trial credits.",
            "free-trial",
            TimeSpan.FromDays(FreeTrialValidDays));

        _logger.LogInformation("Free trial granted: ${Amount} to new user {UserId}", FreeTrialAmount, userId);
    }

    public async Task<(bool Success, string Message, decimal Amount)> RedeemPromoCodeAsync(
        string userId, string promoCode)
    {
        var campaign = await _dataStore.GetCampaignByCodeAsync(promoCode.Trim().ToUpper());
        if (campaign == null)
            return (false, "Invalid promo code", 0);

        if (!campaign.IsActive)
            return (false, "This promotion has ended", 0);

        if (DateTime.UtcNow > campaign.ExpiresAt)
            return (false, "This promo code has expired", 0);

        if (campaign.CurrentRedemptions >= campaign.MaxRedemptions)
            return (false, "This promo code has reached its redemption limit", 0);

        // Check if user already redeemed this campaign
        var existingGrants = await _dataStore.GetCreditGrantsForUserAsync(userId);
        if (existingGrants.Any(g => g.SourceId == campaign.Id))
            return (false, "You've already redeemed this promo code", 0);

        // Grant the credit
        await GrantCreditAsync(
            userId,
            CreditType.PromoCampaign,
            campaign.CreditAmount,
            $"Promo: {campaign.Name}",
            campaign.Id,
            TimeSpan.FromDays(campaign.CreditValidityDays));

        // Increment redemption count
        campaign.CurrentRedemptions++;
        await _dataStore.SaveCampaignAsync(campaign);

        _logger.LogInformation(
            "Promo code {Code} redeemed by {UserId}: ${Amount}",
            promoCode, userId, campaign.CreditAmount);

        return (true, $"Promo code applied! ${campaign.CreditAmount} USDC credits added.", campaign.CreditAmount);
    }

    public async Task<UserCreditBalance> GetCreditBalanceAsync(string userId)
    {
        var grants = await _dataStore.GetCreditGrantsForUserAsync(userId);
        var activeGrants = grants
            .Where(g => g.RemainingAmount > 0 && (g.ExpiresAt == null || g.ExpiresAt > DateTime.UtcNow))
            .ToList();

        var (tierName, discountPercent) = await GetVolumeDiscountAsync(userId);

        return new UserCreditBalance
        {
            UserId = userId,
            TotalCredits = activeGrants.Sum(g => g.RemainingAmount),
            ActiveGrants = activeGrants.Select(g => new CreditGrantSummary
            {
                Id = g.Id,
                Type = g.Type,
                RemainingAmount = g.RemainingAmount,
                Description = g.Description,
                ExpiresAt = g.ExpiresAt
            }).ToList(),
            VolumeDiscountTier = tierName,
            VolumeDiscountPercent = discountPercent
        };
    }

    public async Task<decimal> ConsumeCreditsAsync(string userId, decimal amount)
    {
        var grants = await _dataStore.GetCreditGrantsForUserAsync(userId);
        var activeGrants = grants
            .Where(g => g.RemainingAmount > 0 && (g.ExpiresAt == null || g.ExpiresAt > DateTime.UtcNow))
            .OrderBy(g => g.ExpiresAt ?? DateTime.MaxValue) // Use expiring credits first
            .ThenBy(g => g.CreatedAt)
            .ToList();

        decimal consumed = 0;
        foreach (var grant in activeGrants)
        {
            if (consumed >= amount) break;

            var toConsume = Math.Min(grant.RemainingAmount, amount - consumed);
            grant.RemainingAmount -= toConsume;
            consumed += toConsume;

            await _dataStore.SaveCreditGrantAsync(grant);
        }

        if (consumed > 0)
        {
            _logger.LogInformation(
                "Credits consumed: ${Consumed} of ${Requested} from {UserId}",
                consumed, amount, userId);
        }

        return consumed;
    }

    public Task<(string TierName, decimal DiscountPercent)> GetVolumeDiscountAsync(string userId)
    {
        // Calculate monthly spend from usage records
        var monthlySpend = _dataStore.UnsettledUsage.Values
            .Where(u => u.UserId == userId && u.CreatedAt >= DateTime.UtcNow.AddDays(-30))
            .Sum(u => u.TotalCost);

        var tier = VolumeTiers.First(t => monthlySpend >= t.MinMonthlySpend);
        return Task.FromResult((tier.TierName, tier.DiscountPercent));
    }

    public async Task<PromoCampaign> CreateCampaignAsync(PromoCampaign campaign)
    {
        campaign.PromoCode = campaign.PromoCode.Trim().ToUpper();
        await _dataStore.SaveCampaignAsync(campaign);

        _logger.LogInformation(
            "Campaign created: {Name} (code: {Code}, ${Amount} x {Max})",
            campaign.Name, campaign.PromoCode, campaign.CreditAmount, campaign.MaxRedemptions);

        return campaign;
    }

    public async Task<List<PromoCampaign>> GetActiveCampaignsAsync()
    {
        return await _dataStore.GetActiveCampaignsAsync();
    }
}
