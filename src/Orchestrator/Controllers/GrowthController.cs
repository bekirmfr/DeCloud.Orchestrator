using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models.Growth;
using Orchestrator.Services.Growth;

namespace Orchestrator.Controllers;

/// <summary>
/// Growth engine API — Referrals, promotions, credits, and node earnings.
/// Powers the viral growth loop and user retention.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class GrowthController : ControllerBase
{
    private readonly IReferralService _referralService;
    private readonly IPromotionService _promotionService;
    private readonly ILogger<GrowthController> _logger;

    public GrowthController(
        IReferralService referralService,
        IPromotionService promotionService,
        ILogger<GrowthController> logger)
    {
        _referralService = referralService;
        _promotionService = promotionService;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════
    // REFERRALS
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get the current user's referral code and stats
    /// </summary>
    [HttpGet("referrals")]
    [Authorize]
    public async Task<ActionResult<ReferralStats>> GetReferralStats()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var stats = await _referralService.GetReferralStatsAsync(userId);
        return Ok(stats);
    }

    /// <summary>
    /// Apply a referral code (called during signup or first login)
    /// </summary>
    [HttpPost("referrals/apply")]
    [Authorize]
    public async Task<ActionResult> ApplyReferralCode([FromBody] ApplyReferralRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var (success, message) = await _referralService.ApplyReferralCodeAsync(userId, request.ReferralCode);

        if (!success)
            return BadRequest(new { message });

        return Ok(new { message });
    }

    // ════════════════════════════════════════════════════════════════
    // PROMOTIONAL CREDITS
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get current user's credit balance (free trial + referral + promo)
    /// </summary>
    [HttpGet("credits")]
    [Authorize]
    public async Task<ActionResult<UserCreditBalance>> GetCreditBalance()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var balance = await _promotionService.GetCreditBalanceAsync(userId);
        return Ok(balance);
    }

    /// <summary>
    /// Redeem a promo code
    /// </summary>
    [HttpPost("credits/redeem")]
    [Authorize]
    public async Task<ActionResult> RedeemPromoCode([FromBody] RedeemPromoRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var (success, message, amount) = await _promotionService.RedeemPromoCodeAsync(userId, request.PromoCode);

        if (!success)
            return BadRequest(new { message });

        return Ok(new { message, amount });
    }

    // ════════════════════════════════════════════════════════════════
    // VOLUME DISCOUNTS
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get user's current volume discount tier
    /// </summary>
    [HttpGet("volume-discount")]
    [Authorize]
    public async Task<ActionResult> GetVolumeDiscount()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var (tierName, discountPercent) = await _promotionService.GetVolumeDiscountAsync(userId);
        return Ok(new
        {
            tier = tierName,
            discountPercent,
            tiers = new[]
            {
                new { name = "Starter", minSpend = 0, discount = 0 },
                new { name = "Growth", minSpend = 50, discount = 5 },
                new { name = "Scale", minSpend = 200, discount = 10 },
                new { name = "Enterprise", minSpend = 500, discount = 15 }
            }
        });
    }

    // ════════════════════════════════════════════════════════════════
    // ADMIN — Campaign Management
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a promotional campaign (admin only)
    /// </summary>
    [HttpPost("admin/campaigns")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<PromoCampaign>> CreateCampaign([FromBody] CreateCampaignRequest request)
    {
        var campaign = new PromoCampaign
        {
            Name = request.Name,
            PromoCode = request.PromoCode.Trim().ToUpper(),
            CreditAmount = request.CreditAmount,
            MaxRedemptions = request.MaxRedemptions,
            ExpiresAt = request.ExpiresAt,
            CreditValidityDays = request.CreditValidityDays
        };

        var created = await _promotionService.CreateCampaignAsync(campaign);
        return Ok(created);
    }

    /// <summary>
    /// List all active promotional campaigns (admin only)
    /// </summary>
    [HttpGet("admin/campaigns")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<PromoCampaign>>> GetCampaigns()
    {
        var campaigns = await _promotionService.GetActiveCampaignsAsync();
        return Ok(campaigns);
    }

    // ════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════

    private string? GetUserId()
    {
        return User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }
}

// ════════════════════════════════════════════════════════════════
// DTOs
// ════════════════════════════════════════════════════════════════

public record ApplyReferralRequest(string ReferralCode);
public record RedeemPromoRequest(string PromoCode);
public record CreateCampaignRequest(
    string Name,
    string PromoCode,
    decimal CreditAmount,
    int MaxRedemptions = 1000,
    DateTime? ExpiresAt = null,
    int CreditValidityDays = 30
);
