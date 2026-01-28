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
    /// Get deposit information for user
    /// </summary>
    [HttpGet("deposit-info")]
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

            var response = new BalanceResponse
            {
                Balance = balanceInfo.AvailableBalance,
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
            return StatusCode(500, ApiResponse<BalanceResponse>.Fail("BALANCE_FETCH_ERROR","Failed to fetch balance"));
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