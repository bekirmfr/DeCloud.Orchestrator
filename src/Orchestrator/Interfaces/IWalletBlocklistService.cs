using Orchestrator.Models;

namespace Orchestrator.Interfaces;

/// <summary>
/// The enforcement gate's storage + predicate layer. Self-contained (owns the
/// "blocked_wallets" and "enforcement_actions" collections) and dependency-light so
/// it can be a singleton injected into the singleton VmService without a captive
/// dependency or a VmService → service cycle. It does NOT suspend users or stop VMs
/// (those need scoped services) — see IEnforcementService for the admin facade.
/// </summary>
public interface IWalletBlocklistService
{
    /// <summary>
    /// One predicate over two boundaries: an internal suspension (User.Status ==
    /// Suspended) OR a denylist entry from any source. Read at the VM-creation gate
    /// at action time — never from a JWT claim — so a block takes effect immediately.
    /// </summary>
    Task<bool> IsWalletBlockedAsync(string walletAddress, CancellationToken ct = default);

    /// <summary>Add/replace a denylist entry for one source and audit it.</summary>
    Task AddBlockAsync(string walletAddress, BlockSource source, string reason,
        string? reference, string addedBy, CancellationToken ct = default);

    /// <summary>Bulk add/replace denylist entries (e.g. an OFAC SDN list). Malformed
    /// addresses are skipped. Returns the number written. Records one audit row.</summary>
    Task<int> BulkImportAsync(IEnumerable<string> wallets, BlockSource source,
        string reason, string addedBy, CancellationToken ct = default);

    /// <summary>Remove a wallet's entry for ONE source (source-scoped) and audit it.
    /// Other sources still blocking the wallet are unaffected. Returns false if no
    /// such entry existed.</summary>
    Task<bool> RemoveBlockAsync(string walletAddress, BlockSource source,
        string actor, string reason, CancellationToken ct = default);

    /// <summary>List denylist entries (optionally for one wallet).</summary>
    Task<List<BlockedWallet>> ListBlocksAsync(string? walletAddress = null, CancellationToken ct = default);

    /// <summary>Append an enforcement action to the audit log.</summary>
    Task RecordActionAsync(EnforcementAction action, CancellationToken ct = default);

    /// <summary>Read the audit log, newest first (optionally for one wallet).</summary>
    Task<List<EnforcementAction>> GetActionsAsync(string? walletAddress = null,
        int limit = 200, CancellationToken ct = default);
}
