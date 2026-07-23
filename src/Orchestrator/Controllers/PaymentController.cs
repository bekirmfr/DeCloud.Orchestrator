using DeCloud.Shared.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services.Balance;
using System.Security.Claims;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentController : ControllerBase
{
    private readonly DataStore _dataStore;
    private readonly IBalanceService _balanceService;
    private readonly PaymentConfig _paymentConfig;
    private readonly ILogger<PaymentController> _logger;

    public PaymentController(
        DataStore dataStore,
        IBalanceService balanceService,
        PaymentConfig paymentConfig,
        ILogger<PaymentController> logger)
    {
        _dataStore = dataStore;
        _balanceService = balanceService;
        _paymentConfig = paymentConfig;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    // ═══════════════════════════════════════════════════════════════════
    // USER ENDPOINTS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deployment constants for the payment system: contract addresses, chain,
    /// explorer, minimum deposit, confirmation depth.
    ///
    /// Anonymous by design. Nothing here depends on the caller — this method
    /// never touches User — and every field is already public on-chain (the
    /// escrow address is the destination of every deposit transaction). The
    /// class-level [Authorize] would only make the client's config load fail
    /// for an authentication reason it has no way to act on.
    /// </summary>
    [HttpGet("deposit-info")]
    [AllowAnonymous]
    public ActionResult<ApiResponse<DepositInfoResponse>> GetDepositInfo()
    {
        var response = new DepositInfoResponse
        {
            EscrowContractAddress = _paymentConfig.EscrowContractAddress,
            UsdcTokenAddress = _paymentConfig.UsdcTokenAddress,
            ChainId = _paymentConfig.ChainId,
            ChainName = GetChainName(_paymentConfig.ChainId),
            ExplorerUrl = GetExplorerUrl(_paymentConfig.ChainId),
            MinDeposit = 1.0m,
            RequiredConfirmations = _paymentConfig.RequiredConfirmations
        };

        return Ok(ApiResponse<DepositInfoResponse>.Ok(response));
    }

    /// <summary>
    /// Get user balance information
    /// Reads directly from blockchain - NO sync needed!
    /// </summary>
    [HttpGet("balance")]
    public async Task<ActionResult<ApiResponse<BalanceResponse>>> GetBalance()
    {
        try
        {
            var userId = GetUserId();

            // ✅ Read balance from blockchain + pending deposits
            var balanceInfo = await _balanceService.GetBalanceInfoAsync(userId);

            // Get recent usage for display
            var recentUsage = _dataStore.UnsettledUsage.Values
                .Where(u => u.UserId == userId)
                .OrderByDescending(u => u.CreatedAt)
                .Take(10)
                .Select(u => new UsageSummary
                {
                    VmId = u.VmId,
                    Cost = u.TotalCost,
                    Duration = u.Duration,
                    CreatedAt = u.CreatedAt
                })
                .ToList();

            // Current hourly burn — only VMs actually accruing cost. A VM paused
            // for insufficient balance or a stale heartbeat isn't being billed,
            // so counting it would overstate burn and understate runway.
            var userVms = await _dataStore.GetVmsByUserAsync(userId);
            var hourlyBurnRate = userVms
                .Where(v => v.Status == VmStatus.Running && v.BillingInfo?.IsPaused != true)
                .Sum(v => v.BillingInfo?.HourlyRateCrypto ?? 0m);

            var response = new BalanceResponse
            {
                Balance = balanceInfo.AvailableBalance,
                HourlyBurnRate = hourlyBurnRate,
                ConfirmedBalance = balanceInfo.ConfirmedBalance,
                PendingDeposits = balanceInfo.PendingDeposits,
                UnpaidUsage = balanceInfo.UnpaidUsage,
                TotalBalance = balanceInfo.TotalBalance,
                TokenSymbol = balanceInfo.TokenSymbol,
                PendingDepositsList = balanceInfo.PendingDepositsList.Select(p => new PendingDepositSummary
                {
                    TxHash = p.TxHash,
                    Amount = p.Amount,
                    Confirmations = p.Confirmations,
                    RequiredConfirmations = p.RequiredConfirmations,
                    CreatedAt = p.CreatedAt
                }).ToList(),
                RecentUsage = recentUsage
            };

            return Ok(ApiResponse<BalanceResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get balance for user {UserId}", GetUserId());
            return StatusCode(500, ApiResponse<BalanceResponse>.Fail("BALANCE_FETCH_ERROR", "Failed to fetch balance"));
        }
    }

    /// <summary>
    /// Get usage history
    /// </summary>
    [HttpGet("usage")]
    public ActionResult<ApiResponse<List<UsageSummary>>> GetUsageHistory(
        [FromQuery] string? vmId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = GetUserId();

        var query = _dataStore.UnsettledUsage.Values
            .Where(u => u.UserId == userId);

        if (!string.IsNullOrEmpty(vmId))
            query = query.Where(u => u.VmId == vmId);

        var usage = query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UsageSummary
            {
                VmId = u.VmId,
                Cost = u.TotalCost,
                Duration = u.Duration,
                CreatedAt = u.CreatedAt
            })
            .ToList();

        return Ok(ApiResponse<List<UsageSummary>>.Ok(usage));
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════

    private static string GetChainName(string chainId) => chainId switch
    {
        "80002" => "Polygon Amoy Testnet",
        "137" => "Polygon Mainnet",
        _ => $"Chain {chainId}"
    };

    private static string GetExplorerUrl(string chainId) => chainId switch
    {
        "80002" => "https://amoy.polygonscan.com",
        "137" => "https://polygonscan.com",
        _ => "https://polygonscan.com"
    };
}

// ═══════════════════════════════════════════════════════════════════════════
// DTOs
// ═══════════════════════════════════════════════════════════════════════════

public record DepositInfoResponse
{
    public string EscrowContractAddress { get; init; } = string.Empty;
    public string UsdcTokenAddress { get; init; } = string.Empty;
    public string ChainId { get; init; } = string.Empty;
    public string ChainName { get; init; } = string.Empty;
    public string ExplorerUrl { get; init; } = string.Empty;
    public decimal MinDeposit { get; init; }
    public int RequiredConfirmations { get; init; }
}

public record BalanceResponse
{
    /// <summary>
    /// Available balance for VM usage (confirmed - unpaid usage)
    /// </summary>
    public decimal Balance { get; init; }

    /// <summary>
    /// Current hourly burn: the sum of HourlyRateCrypto across this user's
    /// Running, non-paused VMs. Runway = Balance / HourlyBurnRate.
    ///
    /// Computed server-side because VmSummaryDto carries no billing info — a
    /// client could only re-derive it from specs, which would produce a
    /// DIFFERENT number than billing actually charges (platform-default rates
    /// instead of the host node's, and no knowledge of paused billing).
    /// </summary>
    public decimal HourlyBurnRate { get; init; }

    /// <summary>
    /// Confirmed balance from blockchain
    /// </summary>
    public decimal ConfirmedBalance { get; init; }

    /// <summary>
    /// Deposits awaiting confirmation
    /// </summary>
    public decimal PendingDeposits { get; init; }

    /// <summary>
    /// Usage not yet settled on-chain
    /// </summary>
    public decimal UnpaidUsage { get; init; }

    /// <summary>
    /// Total balance including pending
    /// </summary>
    public decimal TotalBalance { get; init; }

    public string TokenSymbol { get; init; } = "USDC";

    /// <summary>
    /// List of pending deposits
    /// </summary>
    public List<PendingDepositSummary> PendingDepositsList { get; init; } = new();

    public List<UsageSummary> RecentUsage { get; init; } = new();
}

public record PendingDepositSummary
{
    public string TxHash { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public int Confirmations { get; init; }
    public int RequiredConfirmations { get; init; }
    public DateTime CreatedAt { get; init; }
}

public record UsageSummary
{
    public string VmId { get; init; } = string.Empty;
    public decimal Cost { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime CreatedAt { get; init; }
}