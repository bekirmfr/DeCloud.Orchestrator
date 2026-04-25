using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using System.Numerics;

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

    // ═══════════════════════════════════════════════════════════════════════════
    // settleCycle() FUNCTION MESSAGE DTOs
    // Strongly-typed Nethereum DTOs for correct ABI tuple encoding.
    // Anonymous C# objects are NOT reliably encoded as ABI tuples by GetFunction().
    // ═══════════════════════════════════════════════════════════════════════════

    [Function("settleCycle")]
    public class SettleCycleFunctionMessage : FunctionMessage
    {
        [Parameter("tuple", "vmData", 1)]
        public CycleVmDataDto VmData { get; set; } = new();

        [Parameter("tuple", "storageData", 2)]
        public CycleStorageDataDto StorageData { get; set; } = new();

        [Parameter("string", "cycleId", 3)]
        public string CycleId { get; set; } = string.Empty;
    }

    public class CycleVmDataDto
    {
        [Parameter("address[]", "users", 1)]
        public List<string> Users { get; set; } = [];

        [Parameter("address[]", "computeNodes", 2)]
        public List<string> ComputeNodes { get; set; } = [];

        [Parameter("uint256[]", "computeAmounts", 3)]
        public List<BigInteger> ComputeAmounts { get; set; } = [];

        [Parameter("uint256[]", "blockCounts", 4)]
        public List<BigInteger> BlockCounts { get; set; } = [];

        [Parameter("uint256[]", "blockSizeKbs", 5)]
        public List<BigInteger> BlockSizeKbs { get; set; } = [];

        [Parameter("uint256[]", "replicationFactors", 6)]
        public List<BigInteger> ReplicationFactors { get; set; } = [];

        [Parameter("string[]", "vmIds", 7)]
        public List<string> VmIds { get; set; } = [];
    }

    public class CycleStorageDataDto
    {
        [Parameter("address[]", "storageNodes", 1)]
        public List<string> StorageNodes { get; set; } = [];

        [Parameter("uint256[]", "storageBytes", 2)]
        public List<BigInteger> StorageBytes { get; set; } = [];
    }
}
