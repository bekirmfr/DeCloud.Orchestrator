// src/Orchestrator/Services/Settlement/SettlementService.cs
// Handles all settlement and usage tracking operations

using Orchestrator.Background;
using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Settlement;

/// <summary>
/// Service for managing VM usage tracking and on-chain settlements
/// Clean separation: handles only settlement domain logic
/// </summary>
public class SettlementService : ISettlementService
{
    private readonly ILogger<SettlementService> _logger;
    private readonly DataStore _dataStore;
    private readonly IBlockchainService _blockchainService;
    private readonly IUserService _userService;
    private readonly PaymentConfig _paymentConfig;

    public SettlementService(
        ILogger<SettlementService> logger,
        DataStore dataStore,
        IBlockchainService blockchainService,
        IUserService userService,
        PaymentConfig paymentConfig)
    {
        _logger = logger;
        _dataStore = dataStore;
        _blockchainService = blockchainService;
        _userService = userService;
        _paymentConfig = paymentConfig;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // USAGE RECORDING
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Record VM usage for billing
    /// </summary>
    public async Task<bool> RecordUsageAsync(
        string userId,
        string vmId,
        string nodeId,
        decimal amount,
        DateTime periodStart,
        DateTime periodEnd,
        bool attestationVerified = true)
    {
        // Validate user has sufficient balance
        if (!await ValidateUserBalanceAsync(userId, amount))
        {
            _logger.LogWarning(
                "Insufficient balance for user {UserId}: needs {Amount} USDC",
                userId, amount);
            return false;
        }

        // Create usage record
        var usageRecord = new UsageRecord
        {
            Id = Guid.NewGuid().ToString(),
            VmId = vmId,
            UserId = userId,
            NodeId = nodeId,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            TotalCost = amount,
            NodeShare = amount * 0.85m, // 85% to node
            PlatformFee = amount * 0.15m, // 15% to platform
            AttestationVerified = attestationVerified,
            SettledOnChain = false,
            CreatedAt = DateTime.UtcNow
        };

        await _dataStore.SaveUsageRecordAsync(usageRecord);

        _logger.LogInformation(
            "Usage recorded: {Amount} USDC for VM {VmId} by user {UserId}, node {NodeId}",
            amount, vmId, userId, nodeId);

        return true;
    }

    /// <summary>
    /// Record batch usage for multiple VMs
    /// </summary>
    public async Task<int> RecordBatchUsageAsync(IEnumerable<UsageRecordRequest> usageRequests)
    {
        int successCount = 0;

        foreach (var request in usageRequests)
        {
            var success = await RecordUsageAsync(
                request.UserId,
                request.VmId,
                request.NodeId,
                request.Amount,
                request.PeriodStart,
                request.PeriodEnd,
                request.AttestationVerified);

            if (success)
                successCount++;
        }

        _logger.LogInformation(
            "Batch usage recorded: {Success}/{Total} records",
            successCount, usageRequests.Count());

        return successCount;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // USAGE QUERIES
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get total unpaid usage for a user
    /// </summary>
    public Task<decimal> GetUnpaidUsageAsync(string userId)
    {
        var unpaidUsage = _dataStore.UsageRecords.Values
            .Where(u => u.UserId == userId && !u.SettledOnChain)
            .Sum(u => u.TotalCost);

        return Task.FromResult(unpaidUsage);
    }

    /// <summary>
    /// Get unpaid usage records for a user
    /// </summary>
    public Task<List<UsageRecord>> GetUnpaidUsageRecordsAsync(string userId)
    {
        var records = _dataStore.UsageRecords.Values
            .Where(u => u.UserId == userId && !u.SettledOnChain)
            .OrderByDescending(u => u.CreatedAt)
            .ToList();

        return Task.FromResult(records);
    }

    /// <summary>
    /// Get all usage records for a user
    /// </summary>
    public Task<List<UsageRecord>> GetUsageHistoryAsync(
        string userId,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int skip = 0,
        int take = 100)
    {
        var query = _dataStore.UsageRecords.Values
            .Where(u => u.UserId == userId);

        if (fromDate.HasValue)
            query = query.Where(u => u.CreatedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(u => u.CreatedAt <= toDate.Value);

        var records = query
            .OrderByDescending(u => u.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToList();

        return Task.FromResult(records);
    }

    /// <summary>
    /// Get unpaid usage for a specific VM
    /// </summary>
    public Task<decimal> GetVmUnpaidUsageAsync(string vmId)
    {
        var unpaidUsage = _dataStore.UsageRecords.Values
            .Where(u => u.VmId == vmId && !u.SettledOnChain)
            .Sum(u => u.TotalCost);

        return Task.FromResult(unpaidUsage);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SETTLEMENT OPERATIONS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mark usage records as settled on-chain
    /// </summary>
    public async Task MarkUsageAsSettledAsync(IEnumerable<string> usageRecordIds, string settlementTxHash)
    {
        foreach (var usageRecordId in usageRecordIds)
        {
            var usageRecord = _dataStore.UsageRecords.Values
                .FirstOrDefault(u => u.Id == usageRecordId);

            if (usageRecord != null)
            {
                usageRecord.SettledOnChain = true;
                usageRecord.SettlementTxHash = settlementTxHash;
                await _dataStore.SaveUsageRecordAsync(usageRecord);
            }
        }

        _logger.LogInformation(
            "Marked {Count} usage records as settled, tx={TxHash}",
            usageRecordIds.Count(), settlementTxHash[..16] + "...");
    }

    /// <summary>
    /// Get usage records ready for settlement
    /// Groups by user/node for batch settlement
    /// </summary>
    public async Task<List<SettlementBatch>> GetPendingSettlementsAsync(decimal minAmount = 1.0m)
    {
        // Get all unpaid usage records
        var unpaidRecords = _dataStore.UsageRecords.Values
            .Where(u => !u.SettledOnChain)
            .ToList();

        // Group by user + node
        var grouped = unpaidRecords
            .GroupBy(u => new { u.UserId, u.NodeId })
            .Select(g => new
            {
                g.Key.UserId,
                g.Key.NodeId,
                Records = g.ToList(),
                TotalAmount = g.Sum(r => r.TotalCost),
                NodeShare = g.Sum(r => r.NodeShare),
                PlatformFee = g.Sum(r => r.PlatformFee)
            })
            .Where(g => g.TotalAmount >= minAmount) // Filter by minimum settlement amount
            .ToList();

        // Build settlement batches
        var batches = new List<SettlementBatch>();

        foreach (var group in grouped)
        {
            // Get user and node wallets
            var user = await _userService.GetUserAsync(group.UserId);
            var node = _dataStore.Nodes.Values.FirstOrDefault(n => n.Id == group.NodeId);

            if (user == null || node == null)
            {
                _logger.LogWarning(
                    "Skipping settlement: user {UserId} or node {NodeId} not found",
                    group.UserId, group.NodeId);
                continue;
            }

            batches.Add(new SettlementBatch
            {
                UserId = group.UserId,
                UserWallet = user.WalletAddress,
                NodeId = group.NodeId,
                NodeWallet = node.WalletAddress,
                UsageRecordIds = group.Records.Select(r => r.Id).ToList(),
                TotalAmount = group.TotalAmount,
                NodeShare = group.NodeShare,
                PlatformFee = group.PlatformFee,
                OldestUsage = group.Records.Min(r => r.CreatedAt),
                LatestUsage = group.Records.Max(r => r.CreatedAt)
            });
        }

        _logger.LogInformation(
            "Found {Count} settlement batches totaling {Total} USDC",
            batches.Count, batches.Sum(b => b.TotalAmount));

        return batches;
    }

    /// <summary>
    /// Validate that user has sufficient balance for usage
    /// </summary>
    public async Task<bool> ValidateUserBalanceAsync(string userId, decimal requiredAmount)
    {
        try
        {
            var user = await _userService.GetUserAsync(userId);
            if (user == null)
                return false;

            // Get on-chain balance
            var confirmedBalance = await _blockchainService.GetEscrowBalanceAsync(user.WalletAddress);

            // Get current unpaid usage
            var unpaidUsage = await GetUnpaidUsageAsync(userId);

            // Available = confirmed - unpaid
            var availableBalance = confirmedBalance - unpaidUsage;

            return availableBalance >= requiredAmount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate balance for user {UserId}", userId);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NODE PAYOUTS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get total pending payout for a node operator
    /// </summary>
    public Task<decimal> GetNodePendingPayoutAsync(string nodeId)
    {
        var pendingPayout = _dataStore.UsageRecords.Values
            .Where(u => u.NodeId == nodeId && u.SettledOnChain)
            .Sum(u => u.NodeShare);

        // TODO: Subtract already withdrawn amounts when withdrawal feature is implemented

        return Task.FromResult(pendingPayout);
    }

    /// <summary>
    /// Get settlement history for a node
    /// </summary>
    public Task<List<UsageRecord>> GetNodeSettlementHistoryAsync(
        string nodeId,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var query = _dataStore.UsageRecords.Values
            .Where(u => u.NodeId == nodeId && u.SettledOnChain);

        if (fromDate.HasValue)
            query = query.Where(u => u.CreatedAt >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(u => u.CreatedAt <= toDate.Value);

        var records = query
            .OrderByDescending(u => u.CreatedAt)
            .ToList();

        return Task.FromResult(records);
    }
}