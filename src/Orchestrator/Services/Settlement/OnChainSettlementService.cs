using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Services.Settlement;

namespace Orchestrator.Services;

/// <summary>
/// Automated settlement background service
/// Batches unpaid usage and settles to blockchain periodically
/// 
/// Configuration:
/// - Runs every {SettlementInterval} (e.g., every 1 hour)
/// - Only settles batches >= {MinSettlementAmount} (e.g., 1 USDC)
/// - Processes up to 10 batches per transaction (gas limit)
/// </summary>
public class OnChainSettlementService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly PaymentConfig _config;
    private readonly ILogger<OnChainSettlementService> _logger;

    public OnChainSettlementService(
        IServiceProvider serviceProvider,
        PaymentConfig config,
        ILogger<OnChainSettlementService> logger)
    {
        _serviceProvider = serviceProvider;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Automated Settlement Service started - interval: {Interval}, min amount: {MinAmount} USDC",
            _config.SettlementInterval, _config.MinSettlementAmount);

        // Wait 1 minute before first run (let system stabilize)
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessSettlementsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Settlement processing failed");
            }

            await Task.Delay(_config.SettlementInterval, stoppingToken);
        }

        _logger.LogInformation("Automated Settlement Service stopped");
    }

    private async Task ProcessSettlementsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var settlementService = scope.ServiceProvider.GetRequiredService<ISettlementService>();
        var blockchainService = scope.ServiceProvider.GetRequiredService<IBlockchainService>();

        // Get batches ready for settlement
        var batches = await settlementService.GetPendingSettlementsAsync(_config.MinSettlementAmount);

        if (batches.Count == 0)
        {
            _logger.LogDebug("No settlements ready (min amount: {MinAmount} USDC)", _config.MinSettlementAmount);
            return;
        }

        var totalAmount = batches.Sum(b => b.TotalAmount);
        var totalNodeShare = batches.Sum(b => b.NodeShare);
        var totalPlatformFee = batches.Sum(b => b.PlatformFee);

        _logger.LogInformation(
            "📦 Processing {Count} settlement batches: Total={Total} USDC, Nodes={NodeShare} USDC, Platform={PlatformFee} USDC",
            batches.Count, totalAmount, totalNodeShare, totalPlatformFee);

        var settledCount = 0;
        var failedCount = 0;

        // Process in chunks of 10 to avoid gas limits
        // Each blockchain transaction can handle ~10 settlements
        foreach (var chunk in batches.Chunk(_config.MaxSettlementsPerBatch))
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessBatchChunkAsync(
                    chunk,
                    settlementService,
                    blockchainService,
                    ct);

                settledCount += chunk.Length;

                // Wait 5 seconds between batches to avoid rate limiting
                if (batches.Count > 10)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch settlement failed for chunk of {Count} settlements", chunk.Length);
                failedCount += chunk.Length;

                // Continue with next chunk (don't fail entire settlement)
            }
        }

        _logger.LogInformation(
            "✓ Settlement cycle complete: {Settled} settled, {Failed} failed",
            settledCount, failedCount);
    }

    private async Task ProcessBatchChunkAsync(
        SettlementBatch[] chunk,
        ISettlementService settlementService,
        IBlockchainService blockchainService,
        CancellationToken ct)
    {
        // Convert to settlement transactions
        var transactions = chunk.Select(b => new SettlementTransaction
        {
            UserWallet = b.UserWallet,
            NodeWallet = b.NodeWallet,
            Amount = b.TotalAmount,
            VmId = b.VmId
        }).ToList();

        var chunkTotal = transactions.Sum(t => t.Amount);

        _logger.LogInformation(
            "Executing batch settlement: {Count} settlements, {Total} USDC",
            transactions.Count, chunkTotal);

        // Execute on blockchain
        var txHash = await blockchainService.ExecuteBatchSettlementAsync(transactions);

        _logger.LogInformation(
            "✓ Batch settlement executed: tx={TxHash}, {Count} settlements",
            txHash[..16] + "...", transactions.Count);

        // Mark all usage records in this batch as settled
        var allRecordIds = chunk.SelectMany(b => b.UsageRecordIds).ToList();
        await settlementService.MarkUsageAsSettledAsync(allRecordIds, txHash);

        _logger.LogDebug(
            "Marked {Count} usage records as settled",
            allRecordIds.Count);

        // Log per-node breakdown for accounting
        foreach (var batch in chunk)
        {
            _logger.LogInformation(
                "📊 Settled for node {NodeId}: {Amount} USDC ({NodeShare} to node, {PlatformFee} platform fee)",
                batch.NodeId, batch.TotalAmount, batch.NodeShare, batch.PlatformFee);
        }
    }
}