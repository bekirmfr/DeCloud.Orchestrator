// src/Orchestrator/Services/DepositMonitorService.cs
// Monitors blockchain for user deposits

using System.Numerics;
using Nethereum.Web3;
using Nethereum.Contracts;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Orchestrator.Persistence;
using Orchestrator.Models;
using Orchestrator.Background;

namespace Orchestrator.Services;

/// <summary>
/// Background service that monitors the escrow contract for deposits
/// </summary>
public class DepositMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly PaymentConfig _config;
    private readonly ILogger<DepositMonitorService> _logger;
    
    private Web3? _web3;
    private long _lastProcessedBlock;
    
    public DepositMonitorService(
        IServiceProvider serviceProvider,
        PaymentConfig config,
        ILogger<DepositMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _config = config;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Deposit Monitor Service starting...");
        
        // Initialize Web3
        _web3 = new Web3(_config.RpcUrl);
        
        // Get current block as starting point
        var currentBlock = await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
        _lastProcessedBlock = (long)currentBlock.Value - 100; // Start 100 blocks back
        
        _logger.LogInformation(
            "Monitoring deposits from block {Block} on chain {ChainId}",
            _lastProcessedBlock, _config.ChainId);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNewBlocksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing deposits");
            }
            
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        
        var userAddress = evt.Event.User.ToLowerInvariant();
        var amount = Web3.Convert.FromWei(evt.Event.Amount, 6); // USDC has 6 decimals
        var txHash = evt.Log.TransactionHash;
        var blockNumber = (long)evt.Log.BlockNumber.Value;
        
        // Check if already processed
        var existingDeposit = dataStore.Deposits.Values
            .FirstOrDefault(d => d.TxHash.Equals(txHash, StringComparison.OrdinalIgnoreCase));
        
        if (existingDeposit != null)
        {
            // Update confirmations
            existingDeposit.Confirmations = (int)(currentBlock - blockNumber);
            
            if (existingDeposit.Status == DepositStatus.Pending && 
                existingDeposit.Confirmations >= _config.RequiredConfirmations)
            {
                await ConfirmDepositAsync(existingDeposit, userService, dataStore);
            }
            
            return;
        }
        
        // Find or create user by wallet
        var user = await userService.GetUserByWalletAsync(userAddress);
        
        // Create deposit record
        var deposit = new Deposit
        {
            UserId = user?.Id ?? "",
            WalletAddress = userAddress,
            TxHash = txHash,
            ChainId = _config.ChainId,
            BlockNumber = blockNumber,
            Confirmations = (int)(currentBlock - blockNumber),
            Amount = amount,
            TokenSymbol = "USDC",
            Status = DepositStatus.Pending
        };
        
        // Check if already confirmed
        if (deposit.Confirmations >= _config.RequiredConfirmations)
        {
            if (user != null)
            {
                await ConfirmDepositAsync(deposit, userService, dataStore);
            }
            else
            {
                // User doesn't exist yet - they'll get credited when they register
                deposit.Status = DepositStatus.Confirming;
                _logger.LogWarning(
                    "Deposit {TxHash} confirmed but wallet {Wallet} not registered",
                    txHash, userAddress);
            }
        }
        
        await dataStore.SaveDepositAsync(deposit);
        
        _logger.LogInformation(
            "Deposit detected: {Amount} USDC from {Wallet}, tx={TxHash}, confirmations={Confirmations}",
            amount, userAddress, txHash[..16], deposit.Confirmations);
    }
    
    private async Task ConfirmDepositAsync(
        Deposit deposit,
        IUserService userService,
        DataStore dataStore)
    {
        // Credit user balance
        var credited = await userService.AddBalanceAsync(deposit.UserId, deposit.Amount);
        
        if (credited)
        {
            deposit.Status = DepositStatus.Confirmed;
            deposit.ConfirmedAt = DateTime.UtcNow;
            
            _logger.LogInformation(
                "Deposit confirmed: {Amount} USDC credited to user {UserId}",
                deposit.Amount, deposit.UserId);
        }
        else
        {
            deposit.Status = DepositStatus.Failed;
            deposit.ErrorMessage = "Failed to credit user balance";
            
            _logger.LogError(
                "Failed to credit deposit {TxHash} to user {UserId}",
                deposit.TxHash, deposit.UserId);
        }
        
        await dataStore.SaveDepositAsync(deposit);
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
