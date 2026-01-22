// src/Orchestrator/Interfaces/Blockchain/IBlockchainService.cs
// Interface for all blockchain interactions including settlement execution

using Orchestrator.Models;
using Orchestrator.Models.Payment;

namespace Orchestrator.Interfaces.Blockchain;

/// <summary>
/// Service for all blockchain interactions
/// Handles Web3 communication, contract calls, event queries, and settlement transactions
/// </summary>
public interface IBlockchainService
{
    // ═══════════════════════════════════════════════════════════════════════════
    // READ OPERATIONS (Stateless Queries)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get confirmed balance from escrow contract
    /// Source of truth for user balance on-chain
    /// </summary>
    /// <param name="walletAddress">User's wallet address</param>
    /// <returns>Balance in USDC (decimal format)</returns>
    Task<decimal> GetEscrowBalanceAsync(string walletAddress);

    /// <summary>
    /// Get pending deposits from blockchain events
    /// Queries recent Deposited events and filters by confirmations
    /// </summary>
    /// <param name="walletAddress">User's wallet address</param>
    /// <param name="lookbackBlocks">How many blocks to scan (default: 100)</param>
    /// <returns>List of pending deposits with confirmation counts</returns>
    Task<List<PendingDepositInfo>> GetPendingDepositsAsync(
        string walletAddress,
        int lookbackBlocks = 100);

    /// <summary>
    /// Get current block number
    /// </summary>
    Task<long> GetCurrentBlockAsync();

    /// <summary>
    /// Check if transaction has been mined
    /// </summary>
    Task<bool> IsTransactionMinedAsync(string txHash);

    /// <summary>
    /// Get transaction confirmation count
    /// </summary>
    Task<int> GetTransactionConfirmationsAsync(string txHash);

    // ═══════════════════════════════════════════════════════════════════════════
    // WRITE OPERATIONS (Settlement Transactions)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Execute settlement transaction on escrow contract
    /// Settles usage charges: deducts from user balance, pays node operator
    /// </summary>
    /// <param name="userWallet">User's wallet address</param>
    /// <param name="nodeWallet">Node operator's wallet address</param>
    /// <param name="amount">Total amount to settle (USDC)</param>
    /// <param name="nodeShare">Amount for node operator (85%)</param>
    /// <param name="platformFee">Platform fee (15%)</param>
    /// <returns>Transaction hash</returns>
    Task<string> ExecuteSettlementAsync(
        string userWallet,
        string nodeWallet,
        decimal amount,
        string vmId);

    /// <summary>
    /// Execute batch settlements in single transaction (gas optimization)
    /// Future enhancement for reducing gas costs
    /// </summary>
    /// <param name="settlements">List of settlements to execute</param>
    /// <returns>Transaction hash</returns>
    Task<string> ExecuteBatchSettlementAsync(List<SettlementTransaction> settlements);
}