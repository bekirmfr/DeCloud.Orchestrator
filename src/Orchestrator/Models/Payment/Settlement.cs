namespace Orchestrator.Models.Payment
{
    /// <summary>
    /// Single settlement transaction for batch processing
    /// </summary>
    public class SettlementTransaction
    {
        public string UserWallet { get; set; } = string.Empty;
        public string NodeWallet { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string VmId { get; set; } = string.Empty;
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
    /// Request to record usage
    /// </summary>
    public class UsageRecordRequest
    {
        public string UserId { get; set; } = string.Empty;
        public string VmId { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public bool AttestationVerified { get; set; } = true;
    }

    /// <summary>
    /// Input for settleCycle() — atomic compute + storage settlement.
    /// Per-VM arrays must be parallel (same index = same VM).
    /// Per-storage-node arrays are independent.
    /// </summary>
    public class SettleCycleRequest
    {
        // Per-VM arrays (parallel)
        public List<string> UserWallets { get; set; } = [];
        public List<string> ComputeNodeWallets { get; set; } = [];
        public List<decimal> ComputeAmounts { get; set; } = [];
        public List<int> BlockCounts { get; set; } = [];
        public List<int> BlockSizeKbs { get; set; } = [];
        public List<int> ReplicationFactors { get; set; } = [];
        public List<string> VmIds { get; set; } = [];

        // Per-storage-node arrays (parallel)
        public List<string> StorageNodeWallets { get; set; } = [];
        public List<long> StorageNodeUsedBytes { get; set; } = [];

        // Cycle identifier for on-chain auditability
        public string CycleId { get; set; } = DateTime.UtcNow.ToString("O");
    }

    /// <summary>
    /// Batch of usage ready for settlement
    /// </summary>
    public class SettlementBatch
    {
        public string UserId { get; set; } = string.Empty;
        public string UserWallet { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public string NodeWallet { get; set; } = string.Empty;
        public string VmId { get; set; } = string.Empty;
        public List<string> UsageRecordIds { get; set; } = new();
        public decimal TotalAmount { get; set; }
        public decimal NodeShare { get; set; }
        public decimal PlatformFee { get; set; }
        public DateTime OldestUsage { get; set; }
        public DateTime LatestUsage { get; set; }
    }
}
