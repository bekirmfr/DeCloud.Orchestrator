// src/Orchestrator/Controllers/PaymentController.cs
// Payment API endpoints

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Background;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Security.Claims;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentController : ControllerBase
{
    private readonly DataStore _dataStore;
    private readonly PaymentConfig _paymentConfig;
    private readonly IUserService _userService;
    private readonly ILogger<PaymentController> _logger;

    public PaymentController(
        DataStore dataStore,
        PaymentConfig paymentConfig,
        IUserService userService,
        ILogger<PaymentController> logger)
    {
        _dataStore = dataStore;
        _paymentConfig = paymentConfig;
        _userService = userService;
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
            ChainName = "Polygon Amoy Testnet",
            MinDeposit = 1.0m,
            RequiredConfirmations = _paymentConfig.RequiredConfirmations
        };
        
        return Ok(ApiResponse<DepositInfoResponse>.Ok(response));
    }

    /// <summary>
    /// Get user's balance and payment history
    /// </summary>
    [HttpGet("balance")]
    public async Task<ActionResult<ApiResponse<BalanceResponse>>> GetBalance()
    {
        var userId = GetUserId();
        var user = await _userService.GetUserAsync(userId);
        
        if (user == null)
            return NotFound(ApiResponse<BalanceResponse>.Fail("NOT_FOUND", "User not found"));
        
        // Get recent deposits
        var deposits = _dataStore.Deposits.Values
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(10)
            .ToList();
        
        // Get recent usage
        var usage = _dataStore.UsageRecords.Values
            .Where(u => u.UserId == userId)
            .OrderByDescending(u => u.CreatedAt)
            .Take(10)
            .ToList();
        
        var response = new BalanceResponse
        {
            Balance = user.CryptoBalance,
            TokenSymbol = user.BalanceToken,
            RecentDeposits = deposits.Select(d => new DepositSummary
            {
                TxHash = d.TxHash,
                Amount = d.Amount,
                Status = d.Status.ToString(),
                Confirmations = d.Confirmations,
                CreatedAt = d.CreatedAt,
                ConfirmedAt = d.ConfirmedAt
            }).ToList(),
            RecentUsage = usage.Select(u => new UsageSummary
            {
                VmId = u.VmId,
                Cost = u.TotalCost,
                Duration = u.Duration,
                CreatedAt = u.CreatedAt
            }).ToList()
        };
        
        return Ok(ApiResponse<BalanceResponse>.Ok(response));
    }

    /// <summary>
    /// Get user's deposit history
    /// </summary>
    [HttpGet("deposits")]
    public ActionResult<ApiResponse<List<DepositSummary>>> GetDeposits(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = GetUserId();
        
        var deposits = _dataStore.Deposits.Values
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new DepositSummary
            {
                TxHash = d.TxHash,
                Amount = d.Amount,
                Status = d.Status.ToString(),
                Confirmations = d.Confirmations,
                CreatedAt = d.CreatedAt,
                ConfirmedAt = d.ConfirmedAt
            })
            .ToList();
        
        return Ok(ApiResponse<List<DepositSummary>>.Ok(deposits));
    }

    /// <summary>
    /// Get usage history for user's VMs
    /// </summary>
    [HttpGet("usage")]
    public ActionResult<ApiResponse<List<UsageSummary>>> GetUsage(
        [FromQuery] string? vmId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = GetUserId();
        
        var query = _dataStore.UsageRecords.Values
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
    public decimal MinDeposit { get; init; }
    public int RequiredConfirmations { get; init; }
}

public record BalanceResponse
{
    public decimal Balance { get; init; }
    public string TokenSymbol { get; init; } = "USDC";
    public List<DepositSummary> RecentDeposits { get; init; } = new();
    public List<UsageSummary> RecentUsage { get; init; } = new();
}

public record DepositSummary
{
    public string TxHash { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string Status { get; init; } = string.Empty;
    public int Confirmations { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ConfirmedAt { get; init; }
}

public record UsageSummary
{
    public string VmId { get; init; } = string.Empty;
    public decimal Cost { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime CreatedAt { get; init; }
}
