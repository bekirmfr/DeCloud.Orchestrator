// src/Orchestrator/Models/Payment.cs
// Payment-related models for DeCloud

namespace Orchestrator.Models;

/// <summary>
/// Tracks a user deposit from blockchain
/// </summary>
public class Deposit
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string WalletAddress { get; set; } = string.Empty;
    
    // Blockchain data
    public string TxHash { get; set; } = string.Empty;
    public string ChainId { get; set; } = "80002"; // Polygon Amoy
    public long BlockNumber { get; set; }
    public int Confirmations { get; set; }
    
    // Amount
    public decimal Amount { get; set; }
    public string TokenSymbol { get; set; } = "USDC";
    
    // Status
    public DepositStatus Status { get; set; } = DepositStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum DepositStatus
{
    Pending,      // Detected, waiting for confirmations
    Confirming,   // Has some confirmations
    Confirmed,    // Fully confirmed, balance credited
    Failed        // Transaction failed or reverted
}

/// <summary>
/// Tracks pending payouts to node operators
/// </summary>
public class NodePayout
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string NodeId { get; set; } = string.Empty;
    public string WalletAddress { get; set; } = string.Empty;
    
    // Balances
    public decimal PendingAmount { get; set; }      // Accumulated, not yet settled
    public decimal SettledAmount { get; set; }      // Settled on-chain
    public decimal WithdrawnAmount { get; set; }    // Withdrawn by node
    
    // Last settlement
    public DateTime? LastSettlementAt { get; set; }
    public string? LastSettlementTxHash { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Records individual usage for billing
/// </summary>
public class UsageRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    // References
    public string VmId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    
    // Time period
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public TimeSpan Duration => PeriodEnd - PeriodStart;
    
    // Billing
    public decimal TotalCost { get; set; }
    public decimal NodeShare { get; set; }       // 85%
    public decimal PlatformFee { get; set; }     // 15%
    
    // Attestation
    public bool AttestationVerified { get; set; }
    public string? AttestationId { get; set; }
    
    // Settlement status
    public bool SettledOnChain { get; set; }
    public string? SettlementTxHash { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration for payment system
/// </summary>
public class PaymentConfig
{
    // Blockchain
    public string ChainId { get; set; } = "80002";  // Polygon Amoy
    public string RpcUrl { get; set; } = "https://rpc-amoy.polygon.technology";
    public string EscrowContractAddress { get; set; } = string.Empty;
    public string UsdcTokenAddress { get; set; } = string.Empty;
    
    // Orchestrator wallet (for signing settlement txs)
    public string OrchestratorWalletAddress { get; set; } = string.Empty;
    public string OrchestratorPrivateKey { get; set; } = string.Empty; // Store securely!
    
    // Confirmation requirements
    public int RequiredConfirmations { get; set; } = 12;
    
    // Settlement
    public decimal MinSettlementAmount { get; set; } = 10.0m; // Min $10 to settle
    public TimeSpan SettlementInterval { get; set; } = TimeSpan.FromHours(24);
    
    // Fee structure
    public decimal PlatformFeePercent { get; set; } = 15.0m;
    public decimal NodeSharePercent => 100.0m - PlatformFeePercent;
}

/// <summary>
/// Extended billing info for VMs
/// </summary>
public class ExtendedBillingInfo
{
    // Rates
    public decimal HourlyRateCrypto { get; set; }
    public string CryptoSymbol { get; set; } = "USDC";
    
    // Totals
    public decimal TotalBilled { get; set; }
    public decimal TotalPaid { get; set; }
    public TimeSpan TotalRuntime { get; set; }
    public TimeSpan TotalBillableTime { get; set; }
    
    // Current billing period
    public DateTime? LastBillingAt { get; set; }
    public DateTime? CurrentPeriodStart { get; set; }
    
    // Reserved funds (soft lock)
    public decimal ReservedAmount { get; set; }
    
    // Billing state
    public bool IsPaused { get; set; }
    public DateTime? PausedAt { get; set; }
    public string? PauseReason { get; set; }
    
    // Attestation stats
    public int TotalAttestations { get; set; }
    public int SuccessfulAttestations { get; set; }
    public int FailedAttestations { get; set; }
    
    public decimal AttestationSuccessRate => 
        TotalAttestations > 0 
            ? (decimal)SuccessfulAttestations / TotalAttestations * 100 
            : 100m;
}

/// <summary>
/// Summary of pending settlements for on-chain batch
/// </summary>
public class SettlementBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    // Items to settle
    public List<SettlementItem> Items { get; set; } = new();
    
    // Totals
    public decimal TotalAmount { get; set; }
    public decimal TotalNodePayouts { get; set; }
    public decimal TotalPlatformFees { get; set; }
    
    // Status
    public SettlementStatus Status { get; set; } = SettlementStatus.Pending;
    public string? TxHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SettledAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SettlementItem
{
    public string UserId { get; set; } = string.Empty;
    public string UserWallet { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string NodeWallet { get; set; } = string.Empty;
    public string VmId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal NodeShare { get; set; }
    public decimal PlatformFee { get; set; }
}

public enum SettlementStatus
{
    Pending,      // Not yet submitted
    Submitted,    // Tx submitted, waiting confirmation
    Confirmed,    // Tx confirmed
    Failed        // Tx failed
}

/// <summary>
/// Temporary tracking for deposits with insufficient confirmations
/// Once confirmed, this record is DELETED and balance is read from blockchain
/// </summary>
public class PendingDeposit
{
    /// <summary>
    /// Transaction hash - primary key
    /// </summary>
    public string TxHash { get; set; } = string.Empty;

    /// <summary>
    /// Wallet address that made the deposit
    /// Note: Stored lowercase for case-insensitive matching
    /// </summary>
    public string WalletAddress { get; set; } = string.Empty;

    /// <summary>
    /// Deposit amount in USDC
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Block number where deposit occurred
    /// </summary>
    public long BlockNumber { get; set; }

    /// <summary>
    /// Current number of confirmations
    /// </summary>
    public int Confirmations { get; set; }

    /// <summary>
    /// Chain ID (80002 for Polygon Amoy)
    /// </summary>
    public string ChainId { get; set; } = "80002";

    /// <summary>
    /// When this pending deposit was first detected
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Complete balance information for a user
/// </summary>
public class BalanceInfo
{
    /// <summary>
    /// Confirmed balance from blockchain (≥20 confirmations)
    /// Source of truth: escrow.userBalances(wallet)
    /// </summary>
    public decimal ConfirmedBalance { get; set; }

    /// <summary>
    /// Deposits awaiting confirmation (< 20 blocks)
    /// Temporary state, will be deleted once confirmed
    /// </summary>
    public decimal PendingDeposits { get; set; }

    /// <summary>
    /// Usage charges not yet settled on-chain
    /// These will be deducted from balance when settled
    /// </summary>
    public decimal UnpaidUsage { get; set; }

    /// <summary>
    /// Available balance for VM usage (confirmed - unpaid)
    /// This is what user can actually spend right now
    /// </summary>
    public decimal AvailableBalance { get; set; }

    /// <summary>
    /// Total balance including pending deposits
    /// = Confirmed + Pending - Unpaid
    /// </summary>
    public decimal TotalBalance { get; set; }

    /// <summary>
    /// Token symbol (USDC)
    /// </summary>
    public string TokenSymbol { get; set; } = "USDC";

    /// <summary>
    /// List of pending deposits (for UI display)
    /// </summary>
    public List<PendingDepositInfo> PendingDepositsList { get; set; } = new();
}

/// <summary>
/// Pending deposit info for UI display
/// </summary>
public class PendingDepositInfo
{
    public string TxHash { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int Confirmations { get; set; }
    public int RequiredConfirmations { get; set; }
    public DateTime CreatedAt { get; set; }
}