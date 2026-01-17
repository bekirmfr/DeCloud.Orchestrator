// src/Orchestrator/Services/Balance/IBalanceService.cs
// Balance calculation service - completely stateless

using Orchestrator.Models;
using Orchestrator.Models.Balance;

namespace Orchestrator.Services.Balance;

/// <summary>
/// Service for calculating user balances (stateless)
/// Orchestrates: BlockchainService + SettlementService + UserService
/// NO state storage - pure calculation from source data
/// </summary>
public interface IBalanceService
{
    /// <summary>
    /// Get complete balance information for user (stateless calculation)
    /// Formula: Available = OnChain - Unpaid, Total = OnChain + Pending - Unpaid
    /// </summary>
    Task<BalanceInfo> GetBalanceInfoAsync(string userId);

    /// <summary>
    /// Check if user has sufficient available balance
    /// Used by BillingService before recording usage
    /// </summary>
    Task<bool> HasSufficientBalanceAsync(string userId, decimal requiredAmount);

    /// <summary>
    /// Get available balance only (shortcut method)
    /// </summary>
    Task<decimal> GetAvailableBalanceAsync(string userId);

    /// <summary>
    /// Get balance breakdown for debugging/analytics
    /// Shows component breakdown: confirmed, pending, unpaid
    /// </summary>
    Task<BalanceBreakdown> GetBalanceBreakdownAsync(string userId);
}