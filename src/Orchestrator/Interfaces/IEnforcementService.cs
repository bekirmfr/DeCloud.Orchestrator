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
    /// <summary>Suspend a wallet (User.Status = Suspended), stop its running VMs, suspend
    /// any nodes it operates, and archive its Published community templates.
    /// <paramref name="reference"/> is an optional external reference (e.g. an abuse report
    /// id) recorded on the enforcement action so the takedown is traceable to its cause.</summary>
    Task<EnforcementResult> SuspendAsync(string walletAddress, string reason, string actor, string? reference = null, CancellationToken ct = default);

    /// <summary>Lift a suspension (User.Status = Active). Does not auto-restart VMs, and does
    /// not un-archive templates — the author republishes, which re-enters review.</summary>
    Task<EnforcementResult> UnsuspendAsync(string walletAddress, string reason, string actor, CancellationToken ct = default);

    /// <summary>Suspend a single VM by id: stop it and hold it so the owner cannot restart it.
    /// <paramref name="reference"/> as in SuspendAsync.</summary>
    Task<EnforcementResult> SuspendVmAsync(string vmId, string reason, string actor, string? reference = null, CancellationToken ct = default);

    /// <summary>Lift a single VM's compliance hold (leaves it stopped; owner may start).</summary>
    Task<EnforcementResult> ResumeVmAsync(string vmId, string reason, string actor, CancellationToken ct = default);

    /// <summary>
    /// Immediate-cutoff override: for each of the wallet's already-suspended nodes, collapse
    /// the graceful drain — kill ephemeral/unconfirmed VMs now and migrate confirmed ones from
    /// their replica. The nodes are deregistered once drained. The wallet must already be
    /// suspended/blocked (its nodes Suspended); otherwise this is a no-op failure.
    /// </summary>
    Task<EnforcementResult> CutoffOperatorNodesNowAsync(string walletAddress, string reason, string actor, CancellationToken ct = default);

    Task BlockAsync(string walletAddress, BlockSource source, string reason, string? reference, string actor, CancellationToken ct = default);

    Task<bool> UnblockAsync(string walletAddress, BlockSource source, string reason, string actor, CancellationToken ct = default);

    Task<int> BulkImportBlocksAsync(IEnumerable<string> wallets, BlockSource source, string reason, string actor, CancellationToken ct = default);

    Task<List<BlockedWallet>> ListBlocksAsync(string? walletAddress = null, CancellationToken ct = default);

    Task<List<EnforcementAction>> GetActionsAsync(string? walletAddress = null, CancellationToken ct = default);
}
