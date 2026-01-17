using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Models;
using System.Numerics;

namespace Orchestrator.Services.Blockchain
{
    public class BlockchainService
    {
    }
}

/// <summary>
/// Service for blockchain interactions
/// Encapsulates all Web3/Nethereum dependencies
/// </summary>
public class BlockchainService : IBlockchainService
{
    private readonly ILogger<BlockchainService> _logger;
    private readonly PaymentConfig _config;
    private readonly Web3 _web3;

    public BlockchainService(
        ILogger<BlockchainService> logger,
        IOptions<PaymentConfig> config)
    {
        _logger = logger;
        _config = config.Value;
        _web3 = new Web3(_config.RpcUrl);
    }

    /// <summary>
    /// Get confirmed balance from escrow contract
    /// </summary>
    public async Task<decimal> GetEscrowBalanceAsync(string walletAddress)
    {
        try
        {
            var contract = _web3.Eth.GetContract(ESCROW_ABI, _config.EscrowContractAddress);
            var userBalancesFunction = contract.GetFunction("userBalances");

            var balance = await userBalancesFunction.CallAsync<BigInteger>(walletAddress);

            // Convert from wei (6 decimals for USDC)
            var balanceDecimal = Web3.Convert.FromWei(balance, 6);

            _logger.LogDebug(
                "Escrow balance for {Wallet}: {Balance} USDC",
                walletAddress[..10] + "...", balanceDecimal);

            return balanceDecimal;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch escrow balance for {Wallet}", walletAddress);
            throw new Exception($"Failed to fetch on-chain balance: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Get pending deposits from blockchain events
    /// Queries Deposited events from recent blocks and filters by confirmations
    /// </summary>
    public async Task<List<PendingDepositInfo>> GetPendingDepositsAsync(
        string walletAddress,
        int lookbackBlocks = 100)
    {
        int requiredConfirmations = _config.RequiredConfirmations;
        try
        {
            var currentBlock = await GetCurrentBlockAsync();
            var fromBlock = Math.Max(0, currentBlock - lookbackBlocks);

            _logger.LogDebug(
                "Querying pending deposits for {Wallet} from block {From} to {To}",
                walletAddress[..10] + "...", fromBlock, currentBlock);

            // Get Deposited events from escrow contract
            var depositEvent = _web3.Eth.GetEvent<DepositedEventDTO>(_config.EscrowContractAddress);
            var filter = depositEvent.CreateFilterInput(
                new BlockParameter((ulong)fromBlock),
                new BlockParameter((ulong)currentBlock));

            var events = await depositEvent.GetAllChangesAsync(filter);

            // Filter for this wallet and unconfirmed deposits
            var pendingDeposits = new List<PendingDepositInfo>();

            foreach (var evt in events)
            {
                // Skip if not for this wallet
                if (!evt.Event.User.Equals(walletAddress, StringComparison.OrdinalIgnoreCase))
                    continue;

                var blockNumber = (long)evt.Log.BlockNumber.Value;
                var confirmations = (int)(currentBlock - blockNumber);

                // Only include if still pending (< required confirmations)
                if (confirmations < requiredConfirmations)
                {
                    var amount = Web3.Convert.FromWei(evt.Event.Amount, 6);

                    pendingDeposits.Add(new PendingDepositInfo
                    {
                        TxHash = evt.Log.TransactionHash,
                        Amount = amount,
                        Confirmations = confirmations,
                        RequiredConfirmations = requiredConfirmations,
                        CreatedAt = DateTimeOffset.FromUnixTimeSeconds((long)evt.Event.Timestamp).UtcDateTime
                    });

                    _logger.LogDebug(
                        "Pending deposit: {Amount} USDC, {Confirmations}/{Required} confirmations",
                        amount, confirmations, requiredConfirmations);
                }
            }

            _logger.LogInformation(
                "Found {Count} pending deposits for {Wallet}",
                pendingDeposits.Count, walletAddress[..10] + "...");

            return pendingDeposits;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query pending deposits for {Wallet}", walletAddress);
            // Return empty list rather than throwing - pending deposits are non-critical
            return new List<PendingDepositInfo>();
        }
    }

    /// <summary>
    /// Get current block number
    /// </summary>
    public async Task<long> GetCurrentBlockAsync()
    {
        try
        {
            var blockNumber = await _web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            return (long)blockNumber.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get current block number");
            throw new Exception($"Failed to get current block: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Check if transaction has been mined
    /// </summary>
    public async Task<bool> IsTransactionMinedAsync(string txHash)
    {
        try
        {
            var receipt = await _web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
            return receipt != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check transaction {TxHash}", txHash);
            return false;
        }
    }

    /// <summary>
    /// Get transaction confirmation count
    /// </summary>
    public async Task<int> GetTransactionConfirmationsAsync(string txHash)
    {
        try
        {
            var receipt = await _web3.Eth.Transactions.GetTransactionReceipt.SendRequestAsync(txHash);
            if (receipt == null)
                return 0;

            var currentBlock = await GetCurrentBlockAsync();
            var txBlock = (long)receipt.BlockNumber.Value;

            return (int)(currentBlock - txBlock);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get confirmations for {TxHash}", txHash);
            return 0;
        }
    }

    // Minimal ABI for escrow contract - only what we need
    private const string ESCROW_ABI = @"[
        {
            ""inputs"": [
                {
                    ""internalType"": ""address"",
                    ""name"": """",
                    ""type"": ""address""
                }
            ],
            ""name"": ""userBalances"",
            ""outputs"": [
                {
                    ""internalType"": ""uint256"",
                    ""name"": """",
                    ""type"": ""uint256""
                }
            ],
            ""stateMutability"": ""view"",
            ""type"": ""function""
        }
    ]";
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