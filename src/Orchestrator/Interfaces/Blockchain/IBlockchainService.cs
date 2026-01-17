// src/Orchestrator/Services/Blockchain/IBlockchainService.cs
// Interface for all blockchain interactions
// Abstracts Web3/Nethereum from rest of application

using Orchestrator.Models;

namespace Orchestrator.Interfaces.Blockchain;

/// <summary>
/// Service for all blockchain interactions
/// Handles Web3 communication, contract calls, event queries
/// </summary>
public interface IBlockchainService
{
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
    /// <param name="requiredConfirmations">Minimum confirmations required</param>
    /// <param name="lookbackBlocks">How many blocks to scan (default: 100)</param>
    /// <returns>List of pending deposits with confirmation counts</returns>
    Task<List<PendingDepositInfo>> GetPendingDepositsAsync(
        string walletAddress, int lookbackBlocks = 100);

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
}