namespace Orchestrator.Models.Balance
{
    /// <summary>
    /// Detailed balance breakdown for debugging
    /// </summary>
    public class BalanceBreakdown
    {
        public string UserId { get; set; } = string.Empty;
        public string WalletAddress { get; set; } = string.Empty;

        // On-chain data
        public decimal ConfirmedBalance { get; set; }
        public List<PendingDepositInfo> PendingDeposits { get; set; } = new();
        public decimal PendingDepositsTotal { get; set; }

        // Off-chain data
        public decimal UnpaidUsage { get; set; }
        public int UnpaidUsageRecordCount { get; set; }

        // Calculated
        public decimal AvailableBalance { get; set; }
        public decimal TotalBalance { get; set; }

        // Metadata
        public DateTime CalculatedAt { get; set; }
        public long BlockNumber { get; set; }
    }
}
