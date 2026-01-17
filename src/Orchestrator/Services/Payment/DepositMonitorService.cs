// src/Orchestrator/Services/Payment/DepositMonitorService.cs
// Monitors blockchain for deposits and tracks them until confirmed
// Once confirmed (≥20 blocks), deposits are deleted - blockchain becomes source of truth

using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Numerics;

namespace Orchestrator.Services.Payment;

/// <summary>
/// Monitors blockchain for new deposits
/// Tracks unconfirmed deposits temporarily
/// Deletes confirmed deposits (blockchain becomes source of truth)
/// </summary>
public class DepositMonitorService : BackgroundService
{
    private readonly ILogger<DepositMonitorService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaymentConfig _config;
    private Web3? _web3;
    private long _lastProcessedBlock;

    public DepositMonitorService(
        ILogger<DepositMonitorService> logger,
        IServiceProvider serviceProvider,
        IOptions<PaymentConfig> config)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Deposit Monitor Service starting...");

        try
        {
            // Initialize Web3
            _web3 = new Web3(_config.RpcUrl);

            // Get current block
            var currentBlock = await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            _lastProcessedBlock = (long)currentBlock.Value - 1;

            _logger.LogInformation("Monitoring deposits from block {Block} on chain {ChainId}",
                _lastProcessedBlock, _config.ChainId);

            // Monitor deposits every 30 seconds
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ProcessNewBlocksAsync(ct);
                    await CleanupConfirmedDepositsAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing deposits");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deposit Monitor Service failed to start");
        }
    }

    private async Task ProcessNewBlocksAsync(CancellationToken ct)
    {
        var currentBlock = await _web3!.Eth.Blocks.GetBlockNumber.SendRequestAsync();
        var latestBlock = (long)currentBlock.Value;

        if (latestBlock <= _lastProcessedBlock)
            return;

        // Process in batches of 100 blocks
        var fromBlock = _lastProcessedBlock + 1;
        var toBlock = Math.Min(latestBlock, fromBlock + 100);

        _logger.LogDebug("Scanning blocks {From} to {To}", fromBlock, toBlock);

        // Get Deposited events from escrow contract
        var depositEvent = _web3.Eth.GetEvent<DepositedEventDTO>(_config.EscrowContractAddress);
        var filter = depositEvent.CreateFilterInput(
            new BlockParameter((ulong)fromBlock),
            new BlockParameter((ulong)toBlock));

        var events = await depositEvent.GetAllChangesAsync(filter);

        foreach (var evt in events)
        {
            await ProcessDepositEventAsync(evt, latestBlock, ct);
        }

        _lastProcessedBlock = toBlock;
    }

    private async Task ProcessDepositEventAsync(
        EventLog<DepositedEventDTO> evt,
        long currentBlock,
        CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dataStore = scope.ServiceProvider.GetRequiredService<DataStore>();

        var walletAddress = evt.Event.User.ToLowerInvariant();
        var amount = Web3.Convert.FromWei(evt.Event.Amount, 6); // USDC has 6 decimals
        var txHash = evt.Log.TransactionHash;
        var blockNumber = (long)evt.Log.BlockNumber.Value;
        var confirmations = (int)(currentBlock - blockNumber);

        // ✅ If already confirmed, DON'T track it - just read from blockchain
        if (confirmations >= _config.RequiredConfirmations)
        {
            _logger.LogDebug(
                "Deposit {TxHash} already confirmed ({Confirmations} blocks), skipping DB tracking",
                txHash[..16], confirmations);
            return;
        }

        // Check if already tracking this pending deposit
        var existingDeposit = dataStore.PendingDeposits.Values
            .FirstOrDefault(d => d.TxHash.Equals(txHash, StringComparison.OrdinalIgnoreCase));

        if (existingDeposit != null)
        {
            // Update confirmations
            existingDeposit.Confirmations = confirmations;
            existingDeposit.UpdatedAt = DateTime.UtcNow;

            // If NOW confirmed, DELETE from DB
            if (confirmations >= _config.RequiredConfirmations)
            {
                await dataStore.DeletePendingDepositAsync(txHash);

                _logger.LogInformation(
                    "✓ Deposit confirmed and removed from pending: {Amount} USDC, tx={TxHash}",
                    amount, txHash[..16]);
            }
            else
            {
                await dataStore.SavePendingDepositAsync(existingDeposit);

                _logger.LogDebug(
                    "Pending deposit updated: {Amount} USDC, {Confirmations}/{Required} confirmations",
                    amount, confirmations, _config.RequiredConfirmations);
            }
        }
        else
        {
            // New pending deposit - track it temporarily
            var pendingDeposit = new PendingDeposit
            {
                TxHash = txHash,
                WalletAddress = walletAddress,
                Amount = amount,
                BlockNumber = blockNumber,
                Confirmations = confirmations,
                ChainId = _config.ChainId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await dataStore.SavePendingDepositAsync(pendingDeposit);

            _logger.LogInformation(
                "⏳ Tracking pending deposit: {Amount} USDC from {Wallet}, {Confirmations}/{Required} confirmations",
                amount, walletAddress[..10], confirmations, _config.RequiredConfirmations);
        }
    }

    /// <summary>
    /// Cleanup any confirmed deposits that might have been missed
    /// This is a safety mechanism for edge cases
    /// </summary>
    private async Task CleanupConfirmedDepositsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dataStore = scope.ServiceProvider.GetRequiredService<DataStore>();

        var currentBlock = await _web3!.Eth.Blocks.GetBlockNumber.SendRequestAsync();
        var latestBlock = (long)currentBlock.Value;

        var toRemove = new List<string>();

        foreach (var deposit in dataStore.PendingDeposits.Values)
        {
            var confirmations = (int)(latestBlock - deposit.BlockNumber);

            if (confirmations >= _config.RequiredConfirmations)
            {
                toRemove.Add(deposit.TxHash);
            }
        }

        if (toRemove.Count > 0)
        {
            foreach (var txHash in toRemove)
            {
                await dataStore.DeletePendingDepositAsync(txHash);
            }

            _logger.LogInformation(
                "Cleaned up {Count} confirmed deposits from pending tracking",
                toRemove.Count);
        }
    }
}

/// <summary>
/// Event DTO for Deposited event from smart contract
/// </summary>
[Event("Deposited")]
public class DepositedEventDTO : IEventDTO
{
    [Parameter("address", "user", 1, true)]
    public string User { get; set; } = string.Empty;

    [Parameter("uint256", "amount", 2, false)]
    public BigInteger Amount { get; set; }

    [Parameter("uint256", "newBalance", 3, false)]
    public BigInteger NewBalance { get; set; }

    [Parameter("uint256", "timestamp", 4, false)]
    public BigInteger Timestamp { get; set; }
}