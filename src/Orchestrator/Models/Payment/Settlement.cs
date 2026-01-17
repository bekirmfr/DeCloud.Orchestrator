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
        public decimal NodeShare { get; set; }
        public decimal PlatformFee { get; set; }
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
    /// Batch of usage ready for settlement
    /// </summary>
    public class SettlementBatch
    {
        public string UserId { get; set; } = string.Empty;
        public string UserWallet { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public string NodeWallet { get; set; } = string.Empty;
        public List<string> UsageRecordIds { get; set; } = new();
        public decimal TotalAmount { get; set; }
        public decimal NodeShare { get; set; }
        public decimal PlatformFee { get; set; }
        public DateTime OldestUsage { get; set; }
        public DateTime LatestUsage { get; set; }
    }
}
