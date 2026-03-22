using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Settlement;

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
        var chunkTotal = chunk.Sum(b => b.TotalAmount);

        _logger.LogInformation(
            "Executing settlement: {Count} settlements, {Total} USDC",
            chunk.Length, chunkTotal);

        // Use settleCycle() when any VM in the chunk has replication enabled.
        // settleCycle() is strictly better — it atomically handles both compute
        // billing and storage pool distribution in one transaction.
        // Fall back to batchReportUsage() only for pure-ephemeral batches.
        string txHash;

        using var scope = _serviceProvider.CreateScope();
        var dataStore = scope.ServiceProvider.GetRequiredService<DataStore>();

        var cycleRequest = await BuildSettleCycleRequestAsync(chunk, dataStore, ct);

        if (cycleRequest.VmIds.Count > 0)
        {
            txHash = await blockchainService.ExecuteSettleCycleAsync(cycleRequest);
        }
        else
        {
            // Fallback: all VMs ephemeral or manifest data unavailable
            var transactions = chunk.Select(b => new SettlementTransaction
            {
                UserWallet = b.UserWallet,
                NodeWallet = b.NodeWallet,
                Amount = b.TotalAmount,
                VmId = b.VmId
            }).ToList();
            txHash = await blockchainService.ExecuteBatchSettlementAsync(transactions);
        }

        _logger.LogInformation(
            "✓ Batch settlement executed: tx={TxHash}, {Count} settlements",
            txHash[..16] + "...", chunk.Length);

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

    private async Task<SettleCycleRequest> BuildSettleCycleRequestAsync(
        SettlementBatch[] chunk,
        DataStore dataStore,
        CancellationToken ct)
    {
        var request = new SettleCycleRequest
        {
            CycleId = DateTime.UtcNow.ToString("O")
        };

        foreach (var batch in chunk)
        {
            var manifest = await dataStore.GetManifestAsync(batch.VmId);

            request.UserWallets.Add(batch.UserWallet);
            request.ComputeNodeWallets.Add(batch.NodeWallet);
            request.ComputeAmounts.Add(batch.TotalAmount);
            request.VmIds.Add(batch.VmId);

            // Block-based storage data — zero for ephemeral VMs
            request.BlockCounts.Add(manifest?.BlockCount ?? 0);
            request.BlockSizeKbs.Add(manifest?.BlockSizeKb ?? 1024);
            request.ReplicationFactors.Add(manifest?.ReplicationFactor ?? 0);
        }

        // Per-storage-node arrays — active BlockStore nodes with used bytes
        var allNodes = await dataStore.GetAllNodesAsync();
        foreach (var node in allNodes.Where(n =>
            n.BlockStoreInfo?.Status == BlockStoreStatus.Active &&
            n.BlockStoreInfo.UsedBytes > 0 &&
            !string.IsNullOrEmpty(n.WalletAddress)))
        {
            request.StorageNodeWallets.Add(node.WalletAddress!);
            request.StorageNodeUsedBytes.Add(node.BlockStoreInfo!.UsedBytes);
        }

        return request;
    }
}