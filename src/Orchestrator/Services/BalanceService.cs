// src/Orchestrator/Services/Balance/BalanceService.cs
// Balance calculation implementation - completely stateless

using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Models;
using Orchestrator.Models.Balance;
using Orchestrator.Services.Settlement;

namespace Orchestrator.Services.Balance;

/// <summary>
/// Balance calculation service (stateless)
/// Orchestrates data from multiple sources:
/// - BlockchainService: on-chain balances and deposits
/// - SettlementService: unpaid usage
/// - UserService: wallet address lookup
/// 
/// NO state storage - all calculations done on-demand
/// </summary>
public class BalanceService : IBalanceService
{
    private readonly ILogger<BalanceService> _logger;
    private readonly IBlockchainService _blockchainService;
    private readonly ISettlementService _settlementService;
    private readonly IUserService _userService;

    public BalanceService(
        ILogger<BalanceService> logger,
        IBlockchainService blockchainService,
        ISettlementService settlementService,
        IUserService userService)
    {
        _logger = logger;
        _blockchainService = blockchainService;
        _settlementService = settlementService;
        _userService = userService;
    }

    /// <summary>
    /// Get complete balance information (stateless calculation)
    /// </summary>
    public async Task<BalanceInfo> GetBalanceInfoAsync(string userId)
    {
        _logger.LogDebug("Calculating balance for user {UserId}", userId);

        // 1. Get user wallet address
        var user = await _userService.GetUserAsync(userId);
        if (user == null)
        {
            throw new Exception($"User {userId} not found");
        }

        // 2. Get confirmed balance from blockchain
        var confirmedBalance = await _blockchainService.GetEscrowBalanceAsync(user.WalletAddress);

        // 3. Get pending deposits from blockchain events
        var pendingDeposits = await _blockchainService.GetPendingDepositsAsync(user.WalletAddress);
        var pendingAmount = pendingDeposits.Sum(d => d.Amount);

        // 4. Get unpaid usage from settlement service
        var unpaidUsage = await _settlementService.GetUnpaidUsageAsync(userId);

        // 5. Calculate balances
        var availableBalance = confirmedBalance - unpaidUsage;
        var totalBalance = confirmedBalance + pendingAmount - unpaidUsage;

        _logger.LogDebug(
            "Balance calculated for {UserId}: Confirmed={Confirmed}, Pending={Pending}, Unpaid={Unpaid}, Available={Available}",
            userId, confirmedBalance, pendingAmount, unpaidUsage, availableBalance);

        return new BalanceInfo
        {
            ConfirmedBalance = confirmedBalance,
            PendingDeposits = pendingAmount,
            UnpaidUsage = unpaidUsage,
            AvailableBalance = Math.Max(0, availableBalance), // Can't be negative
            TotalBalance = totalBalance,
            TokenSymbol = "USDC",
            PendingDepositsList = pendingDeposits
        };
    }

    /// <summary>
    /// Check if user has sufficient available balance
    /// Used by BillingService before recording usage
    /// </summary>
    public async Task<bool> HasSufficientBalanceAsync(string userId, decimal requiredAmount)
    {
        try
        {
            var balanceInfo = await GetBalanceInfoAsync(userId);
            var hasSufficient = balanceInfo.AvailableBalance >= requiredAmount;

            if (!hasSufficient)
            {
                _logger.LogWarning(
                    "Insufficient balance for user {UserId}: available={Available}, required={Required}",
                    userId, balanceInfo.AvailableBalance, requiredAmount);
            }

            return hasSufficient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check balance for user {UserId}", userId);
            return false;
        }
    }

    /// <summary>
    /// Get available balance only (shortcut)
    /// </summary>
    public async Task<decimal> GetAvailableBalanceAsync(string userId)
    {
        var balanceInfo = await GetBalanceInfoAsync(userId);
        return balanceInfo.AvailableBalance;
    }

    /// <summary>
    /// Get balance breakdown for debugging/analytics
    /// </summary>
    public async Task<BalanceBreakdown> GetBalanceBreakdownAsync(string userId)
    {
        var user = await _userService.GetUserAsync(userId);
        if (user == null)
        {
            throw new Exception($"User {userId} not found");
        }

        // Get all components
        var confirmedBalance = await _blockchainService.GetEscrowBalanceAsync(user.WalletAddress);
        var pendingDeposits = await _blockchainService.GetPendingDepositsAsync(user.WalletAddress);
        var unpaidUsage = await _settlementService.GetUnpaidUsageAsync(userId);
        var unpaidRecords = await _settlementService.GetUnpaidUsageRecordsAsync(userId);
        var currentBlock = await _blockchainService.GetCurrentBlockAsync();

        var pendingAmount = pendingDeposits.Sum(d => d.Amount);
        var availableBalance = confirmedBalance - unpaidUsage;
        var totalBalance = confirmedBalance + pendingAmount - unpaidUsage;

        return new BalanceBreakdown
        {
            UserId = userId,
            WalletAddress = user.WalletAddress,
            ConfirmedBalance = confirmedBalance,
            PendingDeposits = pendingDeposits,
            PendingDepositsTotal = pendingAmount,
            UnpaidUsage = unpaidUsage,
            UnpaidUsageRecordCount = unpaidRecords.Count,
            AvailableBalance = Math.Max(0, availableBalance),
            TotalBalance = totalBalance,
            CalculatedAt = DateTime.UtcNow,
            BlockNumber = currentBlock
        };
    }
}