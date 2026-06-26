using Orchestrator.Models;

namespace Orchestrator.Interfaces;

/// <summary>Outcome of an enforcement action.</summary>
public record EnforcementResult(bool Success, string? Error, string? Message, int AffectedVms)
{
    public static EnforcementResult Ok(int affectedVms = 0) => new(true, null, null, affectedVms);
    public static EnforcementResult Fail(string error, string message) => new(false, error, message, 0);
}

/// <summary>
/// Scoped admin facade for all enforcement actions. Orchestrates the parts that need
/// scoped/heavier services (IUserService to flip User.Status, IVmService to stop VMs)
/// and delegates denylist + audit storage to the singleton IWalletBlocklistService.
/// Enforcement is withhold-of-service only: suspend stops VMs (reversible, disk state
/// preserved) and the gate refuses new deploys — escrow funds are never touched.
/// </summary>
public interface IEnforcementService
{
    /// <summary>Suspend a wallet (User.Status = Suspended) and stop its running VMs.</summary>
    Task<EnforcementResult> SuspendAsync(string walletAddress, string reason, string actor, CancellationToken ct = default);

    /// <summary>Lift a suspension (User.Status = Active). Does not auto-restart VMs.</summary>
    Task<EnforcementResult> UnsuspendAsync(string walletAddress, string reason, string actor, CancellationToken ct = default);

    Task BlockAsync(string walletAddress, BlockSource source, string reason, string? reference, string actor, CancellationToken ct = default);

    Task<bool> UnblockAsync(string walletAddress, BlockSource source, string reason, string actor, CancellationToken ct = default);

    Task<int> BulkImportBlocksAsync(IEnumerable<string> wallets, BlockSource source, string reason, string actor, CancellationToken ct = default);

    Task<List<BlockedWallet>> ListBlocksAsync(string? walletAddress = null, CancellationToken ct = default);

    Task<List<EnforcementAction>> GetActionsAsync(string? walletAddress = null, CancellationToken ct = default);
}
