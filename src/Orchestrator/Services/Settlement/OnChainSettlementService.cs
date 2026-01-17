// src/Orchestrator/Background/OnChainSettlementService.cs
// Executes pending settlements to blockchain

using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Services.Settlement;

namespace Orchestrator.Services;

/// <summary>
/// Background service that executes pending settlements on the blockchain
/// 
/// Flow:
/// 1. Query pending settlements from SettlementService (grouped by user/node)
/// 2. Execute settlement transaction on escrow contract via BlockchainService
/// 3. Mark usage records as settled via SettlementService
/// 
/// Runs every hour (configurable)
/// </summary>
public class OnChainSettlementService : BackgroundService
{
    private readonly ISettlementService _settlementService;
    private readonly IBlockchainService _blockchainService;
    private readonly PaymentConfig _config;
    private readonly ILogger<OnChainSettlementService> _logger;

    public OnChainSettlementService(
        ISettlementService settlementService,
        IBlockchainService blockchainService,
        PaymentConfig config,
        ILogger<OnChainSettlementService> logger)
    {
        _settlementService = settlementService;
        _blockchainService = blockchainService;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "On-Chain Settlement Service started (interval: {Interval})",
            _config.SettlementInterval);

        // Wait 5 minutes before first settlement (let system stabilize)
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

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

        _logger.LogInformation("On-Chain Settlement Service stopped");
    }

    private async Task ProcessSettlementsAsync(CancellationToken ct)
    {
        var settledCount = 0;
        var failedCount = 0;
        var totalAmount = 0m;

        try
        {
            // Get pending settlements (grouped by user/node)
            var batches = await _settlementService.GetPendingSettlementsAsync(
                minAmount: _config.MinSettlementAmount);

            if (batches.Count == 0)
            {
                _logger.LogDebug("No settlements pending (min amount: {MinAmount} USDC)",
                    _config.MinSettlementAmount);
                return;
            }

            _logger.LogInformation(
                "Processing {Count} settlement batches totaling {Total} USDC",
                batches.Count, batches.Sum(b => b.TotalAmount));

            foreach (var batch in batches)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await ProcessSingleSettlementAsync(batch);
                    settledCount++;
                    totalAmount += batch.TotalAmount;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to settle batch for user {User}, node {Node}, amount {Amount} USDC",
                        batch.UserWallet[..10], batch.NodeWallet[..10], batch.TotalAmount);
                    failedCount++;
                }

                // Small delay between settlements to avoid overwhelming the RPC
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            if (settledCount > 0 || failedCount > 0)
            {
                _logger.LogInformation(
                    "Settlement cycle complete: {Settled} settled ({Total} USDC), {Failed} failed",
                    settledCount, totalAmount, failedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in settlement cycle");
        }
    }

    private async Task ProcessSingleSettlementAsync(SettlementBatch batch)
    {
        _logger.LogInformation(
            "Executing settlement: User={User}, Node={Node}, Amount={Amount} USDC " +
            "(NodeShare={NodeShare}, PlatformFee={PlatformFee})",
            batch.UserWallet[..10] + "...",
            batch.NodeWallet[..10] + "...",
            batch.TotalAmount,
            batch.NodeShare,
            batch.PlatformFee);

        // Execute settlement transaction on blockchain
        var txHash = await _blockchainService.ExecuteSettlementAsync(
            userWallet: batch.UserWallet,
            nodeWallet: batch.NodeWallet,
            amount: batch.TotalAmount,
            nodeShare: batch.NodeShare,
            platformFee: batch.PlatformFee);

        _logger.LogInformation(
            "✓ Settlement executed: {Amount} USDC, tx={TxHash}",
            batch.TotalAmount, txHash[..16] + "...");

        // Mark usage records as settled
        await _settlementService.MarkUsageAsSettledAsync(
            batch.UsageRecordIds,
            txHash);

        _logger.LogInformation(
            "✓ Marked {Count} usage records as settled",
            batch.UsageRecordIds.Count);
    }
}