using Microsoft.Extensions.Options;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using System.Numerics;

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

    // ═══════════════════════════════════════════════════════════════════════════
    // SETTLEMENT EXECUTION
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Execute settlement transaction on escrow contract
    /// Deducts from user balance, pays node operator
    /// </summary>
    public async Task<string> ExecuteSettlementAsync(
        string userWallet,
        string nodeWallet,
        decimal amount,
		string vmId)
    {
        try
        {
            _logger.LogInformation(
                "Executing settlement: User={User}, Node={Node}, Amount={Amount} USDC",
                userWallet[..10] + "...", nodeWallet[..10] + "...", amount);

            // FIXED: Correct Account constructor
            // Parse chain ID to BigInteger
            var chainId = BigInteger.Parse(_config.ChainId);
            var account = new Account(_config.OrchestratorPrivateKey, chainId);
            var web3 = new Web3(account, _config.RpcUrl);

            // Get escrow contract
            var contract = web3.Eth.GetContract(ESCROW_ABI, _config.EscrowContractAddress);
            var settleFunction = contract.GetFunction("reportUsage");

            // Convert to wei (6 decimals for USDC)
            var amountWei = Web3.Convert.ToWei(amount, 6);

            // Estimate gas (important for cost prediction)
            var gas = await settleFunction.EstimateGasAsync(
                from: account.Address,
                gas: null,
                value: null,
                userWallet,
                nodeWallet,
                amountWei,
                vmId);

            _logger.LogDebug(
                "Estimated gas for settlement: {Gas} units",
                gas.Value);

            // Execute settlement transaction
            var receipt = await settleFunction.SendTransactionAndWaitForReceiptAsync(
                from: account.Address,
                gas: gas,
                gasPrice: null,  // Use network gas price
                value: null,
                receiptRequestCancellationToken: null,
                userWallet,
                nodeWallet,
                amountWei,
                vmId);

            // Check transaction status
            if (receipt.Status?.Value != 1)
            {
                throw new Exception($"reportUsage transaction reverted: {receipt.TransactionHash}");
            }

            _logger.LogInformation(
				"✓ Usage reported: tx={TxHash}, user={User}, node={Node}, amount={Amount} USDC, vmId={VmId}",
				receipt.TransactionHash, userWallet[..10], nodeWallet[..10], amount, vmId);

            return receipt.TransactionHash;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Settlement transaction failed for user {User}, node {Node}",
                userWallet[..10], nodeWallet[..10]);
            throw new Exception($"Settlement failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Execute batch settlements in single transaction
    /// Future optimization for reducing gas costs
    /// </summary>
    public async Task<string> ExecuteBatchSettlementAsync(List<SettlementTransaction> settlements)
    {
        try
        {
            _logger.LogInformation(
                "Executing batch settlement: {Count} settlements, total {Total} USDC",
                settlements.Count, settlements.Sum(s => s.Amount));

            // FIXED: Correct Account constructor
            var chainId = BigInteger.Parse(_config.ChainId);
            var account = new Account(_config.OrchestratorPrivateKey, chainId);
            var web3 = new Web3(account, _config.RpcUrl);

            var contract = web3.Eth.GetContract(ESCROW_ABI, _config.EscrowContractAddress);
            var batchSettleFunction = contract.GetFunction("batchReportUsage");

            // Prepare arrays for batch settlement
            var userWallets = settlements.Select(s => s.UserWallet).ToArray();
            var nodeWallets = settlements.Select(s => s.NodeWallet).ToArray();
            var amountsWei = settlements.Select(s => Web3.Convert.ToWei(s.Amount, 6)).ToArray();
            var vmIds = settlements.Select(s => s.VmId ?? "").ToArray();

            var gas = await batchSettleFunction.EstimateGasAsync(
				from: account.Address,
				gas: null,
				value: null,
				userWallets,
				nodeWallets,
				amountsWei,
				vmIds);

            // Execute batch settlement
            var receipt = await batchSettleFunction.SendTransactionAndWaitForReceiptAsync(
                from: account.Address,
                gas: gas,
                gasPrice: null,
                value: null,
                receiptRequestCancellationToken: null,
                userWallets,  // address[] users
				nodeWallets,  // address[] nodes
				amountsWei,      // uint256[] amounts
				vmIds);       // string[] vmIds

            if (receipt.Status?.Value != 1)
            {
                throw new Exception($"batchReportUsage settlement transaction reverted: {receipt.TransactionHash}");
            }

            _logger.LogInformation(
                "✓ Batch usage reported: tx={TxHash}, gasUsed={GasUsed}",
                receipt.TransactionHash, receipt.GasUsed);

            return receipt.TransactionHash;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch settlement transaction failed");
            throw new Exception($"Batch settlement failed: {ex.Message}", ex);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ESCROW CONTRACT ABI (with settlement methods)
    // ═══════════════════════════════════════════════════════════════════════════

    private const string ESCROW_ABI = @"[
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""owner"",
				""type"": ""address""
			}
		],
		""name"": ""OwnableInvalidOwner"",
		""type"": ""error""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""account"",
				""type"": ""address""
			}
		],
		""name"": ""OwnableUnauthorizedAccount"",
		""type"": ""error""
	},
	{
		""inputs"": [],
		""name"": ""ReentrancyGuardReentrantCall"",
		""type"": ""error""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""token"",
				""type"": ""address""
			}
		],
		""name"": ""SafeERC20FailedOperation"",
		""type"": ""error""
	},
	{
		""anonymous"": false,
		""inputs"": [
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""user"",
				""type"": ""address""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""newBalance"",
				""type"": ""uint256""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""timestamp"",
				""type"": ""uint256""
			}
		],
		""name"": ""Deposited"",
		""type"": ""event""
	},
	{
		""anonymous"": false,
		""inputs"": [
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""node"",
				""type"": ""address""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""timestamp"",
				""type"": ""uint256""
			}
		],
		""name"": ""NodeWithdrawal"",
		""type"": ""event""
	},
	{
		""anonymous"": false,
		""inputs"": [
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""oldOrchestrator"",
				""type"": ""address""
			},
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""newOrchestrator"",
				""type"": ""address""
			}
		],
		""name"": ""OrchestratorUpdated"",
		""type"": ""event""
	},
	{
		""anonymous"": false,
		""inputs"": [
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""previousOwner"",
				""type"": ""address""
			},
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""newOwner"",
				""type"": ""address""
			}
		],
		""name"": ""OwnershipTransferred"",
		""type"": ""event""
	},
	{
		""anonymous"": false,
		""inputs"": [
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""to"",
				""type"": ""address""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			}
		],
		""name"": ""PlatformWithdrawal"",
		""type"": ""event""
	},
	{
		""anonymous"": false,
		""inputs"": [
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""user"",
				""type"": ""address""
			},
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""node"",
				""type"": ""address""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""nodeShare"",
				""type"": ""uint256""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""platformFee"",
				""type"": ""uint256""
			},
			{
				""indexed"": false,
				""internalType"": ""string"",
				""name"": ""vmId"",
				""type"": ""string""
			}
		],
		""name"": ""UsageReported"",
		""type"": ""event""
	},
	{
		""anonymous"": false,
		""inputs"": [
			{
				""indexed"": true,
				""internalType"": ""address"",
				""name"": ""user"",
				""type"": ""address""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			},
			{
				""indexed"": false,
				""internalType"": ""uint256"",
				""name"": ""timestamp"",
				""type"": ""uint256""
			}
		],
		""name"": ""UserWithdrawal"",
		""type"": ""event""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address[]"",
				""name"": ""users"",
				""type"": ""address[]""
			},
			{
				""internalType"": ""address[]"",
				""name"": ""nodes"",
				""type"": ""address[]""
			},
			{
				""internalType"": ""uint256[]"",
				""name"": ""amounts"",
				""type"": ""uint256[]""
			},
			{
				""internalType"": ""string[]"",
				""name"": ""vmIds"",
				""type"": ""string[]""
			}
		],
		""name"": ""batchReportUsage"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			}
		],
		""name"": ""deposit"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""nodeWithdraw"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			}
		],
		""name"": ""nodeWithdrawAmount"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""renounceOwnership"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""user"",
				""type"": ""address""
			},
			{
				""internalType"": ""address"",
				""name"": ""node"",
				""type"": ""address""
			},
			{
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			},
			{
				""internalType"": ""string"",
				""name"": ""vmId"",
				""type"": ""string""
			}
		],
		""name"": ""reportUsage"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""newOrchestrator"",
				""type"": ""address""
			}
		],
		""name"": ""setOrchestrator"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""newOwner"",
				""type"": ""address""
			}
		],
		""name"": ""transferOwnership"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			}
		],
		""name"": ""withdrawBalance"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""to"",
				""type"": ""address""
			},
			{
				""internalType"": ""uint256"",
				""name"": ""amount"",
				""type"": ""uint256""
			}
		],
		""name"": ""withdrawPlatformFees"",
		""outputs"": [],
		""stateMutability"": ""nonpayable"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""_paymentToken"",
				""type"": ""address""
			},
			{
				""internalType"": ""address"",
				""name"": ""_orchestrator"",
				""type"": ""address""
			}
		],
		""stateMutability"": ""nonpayable"",
		""type"": ""constructor""
	},
	{
		""inputs"": [],
		""name"": ""BPS_DENOMINATOR"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""user"",
				""type"": ""address""
			}
		],
		""name"": ""getBalance"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": ""node"",
				""type"": ""address""
			}
		],
		""name"": ""getNodePayout"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""getStats"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": ""_totalDeposited"",
				""type"": ""uint256""
			},
			{
				""internalType"": ""uint256"",
				""name"": ""_totalWithdrawn"",
				""type"": ""uint256""
			},
			{
				""internalType"": ""uint256"",
				""name"": ""_totalUsageReported"",
				""type"": ""uint256""
			},
			{
				""internalType"": ""uint256"",
				""name"": ""_platformFees"",
				""type"": ""uint256""
			},
			{
				""internalType"": ""uint256"",
				""name"": ""_contractBalance"",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""MIN_DEPOSIT"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [
			{
				""internalType"": ""address"",
				""name"": """",
				""type"": ""address""
			}
		],
		""name"": ""nodePendingPayouts"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""orchestrator"",
		""outputs"": [
			{
				""internalType"": ""address"",
				""name"": """",
				""type"": ""address""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""owner"",
		""outputs"": [
			{
				""internalType"": ""address"",
				""name"": """",
				""type"": ""address""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""paymentToken"",
		""outputs"": [
			{
				""internalType"": ""contract IERC20"",
				""name"": """",
				""type"": ""address""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""PLATFORM_FEE_BPS"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""platformFees"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""totalDeposited"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""totalUsageReported"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
	{
		""inputs"": [],
		""name"": ""totalWithdrawn"",
		""outputs"": [
			{
				""internalType"": ""uint256"",
				""name"": """",
				""type"": ""uint256""
			}
		],
		""stateMutability"": ""view"",
		""type"": ""function""
	},
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