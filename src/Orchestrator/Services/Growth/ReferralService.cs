using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Orchestrator.Models.Growth;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Growth;

public interface IReferralService
{
    /// <summary>Generate or retrieve a user's referral code</summary>
    Task<string> GetOrCreateReferralCodeAsync(string userId);

    /// <summary>Apply a referral code during signup</summary>
    Task<(bool Success, string Message)> ApplyReferralCodeAsync(string newUserId, string referralCode);

    /// <summary>Mark a referral as activated when user deploys their first VM</summary>
    Task ActivateReferralAsync(string userId);

    /// <summary>Get referral stats for a user's dashboard</summary>
    Task<ReferralStats> GetReferralStatsAsync(string userId);

    /// <summary>Record ongoing commission from referred user's spending</summary>
    Task RecordCommissionAsync(string spendingUserId, decimal amount);
}

public class ReferralService : IReferralService
{
    private readonly DataStore _dataStore;
    private readonly IPromotionService _promotionService;
    private readonly ReferralConfig _config;
    private readonly ILogger<ReferralService> _logger;

    public ReferralService(
        DataStore dataStore,
        IPromotionService promotionService,
        IOptions<ReferralConfig> config,
        ILogger<ReferralService> logger)
    {
        _dataStore = dataStore;
        _promotionService = promotionService;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<string> GetOrCreateReferralCodeAsync(string userId)
    {
        // Check if user already has a referral code
        var existingCode = await _dataStore.GetReferralCodeForUserAsync(userId);
        if (existingCode != null)
            return existingCode;

        // Generate a short, memorable code from wallet address
        // Format: first 4 chars of wallet + random 4 chars
        var walletPart = userId.Length > 6 ? userId[2..6].ToUpper() : "DCLD";
        var randomPart = Guid.NewGuid().ToString("N")[..4].ToUpper();
        var code = $"{walletPart}{randomPart}";

        await _dataStore.SaveReferralCodeAsync(userId, code);

        _logger.LogInformation("Generated referral code {Code} for user {UserId}", code, userId);
        return code;
    }

    public async Task<(bool Success, string Message)> ApplyReferralCodeAsync(string newUserId, string referralCode)
    {
        // Look up the referral code
        var referrerId = await _dataStore.GetUserByReferralCodeAsync(referralCode);
        if (referrerId == null)
            return (false, "Invalid referral code");

        // Can't refer yourself
        if (referrerId == newUserId)
            return (false, "Cannot use your own referral code");

        // Check if user was already referred
        var existingReferral = await _dataStore.GetReferralByReferredUserAsync(newUserId);
        if (existingReferral != null)
            return (false, "Account already has a referral");

        // Check referrer's limits
        var referrerStats = await GetReferralStatsAsync(referrerId);
        if (referrerStats.TotalReferrals >= _config.MaxReferralsPerUser)
            return (false, "Referrer has reached maximum referrals");

        // Create the referral
        var referral = new Referral
        {
            ReferrerUserId = referrerId,
            ReferredUserId = newUserId,
            ReferralCode = referralCode,
            Status = ReferralStatus.Pending
        };

        await _dataStore.SaveReferralAsync(referral);

        // Grant signup bonus to the new user immediately
        await _promotionService.GrantCreditAsync(
            newUserId,
            CreditType.ReferralBonus,
            _config.ReferredBonus,
            $"Welcome bonus from referral by {TruncateWallet(referrerId)}",
            referral.Id,
            TimeSpan.FromDays(30));

        _logger.LogInformation(
            "Referral created: {ReferrerId} referred {ReferredId} with code {Code}. Bonus: ${Bonus}",
            referrerId, newUserId, referralCode, _config.ReferredBonus);

        return (true, $"Referral applied! You received ${_config.ReferredBonus} USDC in credits.");
    }

    public async Task ActivateReferralAsync(string userId)
    {
        var referral = await _dataStore.GetReferralByReferredUserAsync(userId);
        if (referral == null || referral.Status != ReferralStatus.Pending)
            return;

        referral.Status = ReferralStatus.Activated;
        referral.IsActivated = true;
        referral.ActivatedAt = DateTime.UtcNow;
        referral.UpdatedAt = DateTime.UtcNow;

        await _dataStore.SaveReferralAsync(referral);

        // Grant reward to the referrer
        await _promotionService.GrantCreditAsync(
            referral.ReferrerUserId,
            CreditType.ReferrerReward,
            _config.ReferrerReward,
            $"Referral reward: {TruncateWallet(userId)} deployed their first VM!",
            referral.Id,
            TimeSpan.FromDays(90));

        referral.ReferrerCreditsEarned += _config.ReferrerReward;
        await _dataStore.SaveReferralAsync(referral);

        _logger.LogInformation(
            "Referral activated: {ReferredId} deployed first VM. Referrer {ReferrerId} earned ${Reward}",
            userId, referral.ReferrerUserId, _config.ReferrerReward);
    }

    public async Task RecordCommissionAsync(string spendingUserId, decimal amount)
    {
        var referral = await _dataStore.GetReferralByReferredUserAsync(spendingUserId);
        if (referral == null || referral.Status != ReferralStatus.Activated)
            return;

        // Check if commission period is still active
        if (referral.ActivatedAt.HasValue &&
            DateTime.UtcNow - referral.ActivatedAt.Value > _config.CommissionDuration)
            return;

        var commission = amount * (_config.OngoingCommissionPercent / 100m);
        if (commission < 0.001m) return; // Too small to bother

        await _promotionService.GrantCreditAsync(
            referral.ReferrerUserId,
            CreditType.ReferralCommission,
            commission,
            $"Commission from {TruncateWallet(spendingUserId)} spending",
            referral.Id,
            TimeSpan.FromDays(90));

        referral.ReferrerCreditsEarned += commission;
        referral.UpdatedAt = DateTime.UtcNow;
        await _dataStore.SaveReferralAsync(referral);
    }

    public async Task<ReferralStats> GetReferralStatsAsync(string userId)
    {
        var code = await GetOrCreateReferralCodeAsync(userId);
        var referrals = await _dataStore.GetReferralsByReferrerAsync(userId);

        return new ReferralStats
        {
            UserId = userId,
            ReferralCode = code,
            ReferralLink = $"https://decloud.app/?ref={code}",
            TotalReferrals = referrals.Count,
            ActivatedReferrals = referrals.Count(r => r.Status == ReferralStatus.Activated),
            PendingReferrals = referrals.Count(r => r.Status == ReferralStatus.Pending),
            TotalCreditsEarned = referrals.Sum(r => r.ReferrerCreditsEarned),
            RecentReferrals = referrals
                .OrderByDescending(r => r.CreatedAt)
                .Take(20)
                .Select(r => new ReferralSummary
                {
                    ReferredUser = TruncateWallet(r.ReferredUserId),
                    Status = r.Status,
                    CreditsEarned = r.ReferrerCreditsEarned,
                    CreatedAt = r.CreatedAt,
                    ActivatedAt = r.ActivatedAt
                })
                .ToList()
        };
    }

    private static string TruncateWallet(string wallet)
    {
        if (wallet.Length <= 10) return wallet;
        return $"{wallet[..6]}...{wallet[^4..]}";
    }
}
