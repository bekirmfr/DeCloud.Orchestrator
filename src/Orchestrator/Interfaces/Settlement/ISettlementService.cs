// src/Orchestrator/Services/Settlement/ISettlementService.cs
// Interface for settlement and usage tracking operations

using Orchestrator.Models;
using Orchestrator.Models.Payment;

namespace Orchestrator.Services.Settlement;

/// <summary>
/// Service for managing VM usage tracking and on-chain settlements
/// Handles the lifecycle: usage recording → accumulation → settlement to blockchain
/// </summary>
public interface ISettlementService
{
    // ═══════════════════════════════════════════════════════════════════════════
    // USAGE RECORDING
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Record VM usage for billing
    /// Creates an unpaid usage record that will be settled on-chain later
    /// </summary>
    /// <param name="userId">User ID</param>
    /// <param name="vmId">VM ID</param>
    /// <param name="nodeId">Node operator ID</param>
    /// <param name="amount">Cost in USDC</param>
    /// <param name="periodStart">Usage period start</param>
    /// <param name="periodEnd">Usage period end</param>
    /// <param name="attestationVerified">Whether usage was verified by attestation</param>
    /// <returns>True if usage was recorded, false if insufficient balance</returns>
    Task<bool> RecordUsageAsync(
        string userId,
        string vmId,
        string nodeId,
        decimal amount,
        DateTime periodStart,
        DateTime periodEnd,
        bool attestationVerified = true);

    /// <summary>
    /// Record batch usage for multiple VMs
    /// </summary>
    Task<int> RecordBatchUsageAsync(IEnumerable<UsageRecordRequest> usageRequests);

    // ═══════════════════════════════════════════════════════════════════════════
    // USAGE QUERIES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get total unpaid usage for a user
    /// This is usage that has been recorded but not yet settled on-chain
    /// </summary>
    Task<decimal> GetUnpaidUsageAsync(string userId);

    /// <summary>
    /// Get unpaid usage records for a user
    /// </summary>
    Task<List<UsageRecord>> GetUnpaidUsageRecordsAsync(string userId);

    /// <summary>
    /// Get all usage records for a user (paid and unpaid)
    /// </summary>
    Task<List<UsageRecord>> GetUsageHistoryAsync(
        string userId,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int skip = 0,
        int take = 100);

    /// <summary>
    /// Get unpaid usage for a specific VM
    /// </summary>
    Task<decimal> GetVmUnpaidUsageAsync(string vmId);

    // ═══════════════════════════════════════════════════════════════════════════
    // SETTLEMENT OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mark usage records as settled on-chain
    /// Called after successful on-chain settlement transaction
    /// </summary>
    /// <param name="usageRecordIds">Usage record IDs that were settled</param>
    /// <param name="settlementTxHash">Blockchain transaction hash</param>
    Task MarkUsageAsSettledAsync(IEnumerable<string> usageRecordIds, string settlementTxHash);

    /// <summary>
    /// Get usage records ready for settlement
    /// Returns unpaid usage grouped by user/node for batch settlement
    /// </summary>
    /// <param name="minAmount">Minimum amount to settle (default: 1 USDC)</param>
    Task<List<SettlementBatch>> GetPendingSettlementsAsync(decimal minAmount = 1.0m);

    /// <summary>
    /// Validate that user has sufficient balance for usage
    /// Checks: on-chain balance - unpaid usage >= required amount
    /// </summary>
    Task<bool> ValidateUserBalanceAsync(string userId, decimal requiredAmount);

    // ═══════════════════════════════════════════════════════════════════════════
    // NODE PAYOUTS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get total pending payout for a node operator
    /// This is their share of usage that has been settled but not yet withdrawn
    /// </summary>
    Task<decimal> GetNodePendingPayoutAsync(string nodeId);

    /// <summary>
    /// Get settlement history for a node
    /// </summary>
    Task<List<UsageRecord>> GetNodeSettlementHistoryAsync(
        string nodeId,
        DateTime? fromDate = null,
        DateTime? toDate = null);
}