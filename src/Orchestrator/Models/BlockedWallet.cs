namespace Orchestrator.Models;

/// <summary>
/// Provenance of a denylist entry. A wallet may be blocked by more than one source
/// at once; removal is source-scoped, so clearing (say) an internal block does not
/// lift a sanctions block. <see cref="IWalletBlocklistService.IsWalletBlockedAsync"/>
/// returns true if ANY source blocks the wallet.
/// </summary>
public enum BlockSource
{
    /// <summary>OFAC / sanctions screening (e.g. SDN wallet list).</summary>
    Sanctions,
    /// <summary>Block requested under valid legal process.</summary>
    LawEnforcement,
    /// <summary>Imported from a cross-platform abuse feed.</summary>
    CrossPlatform,
    /// <summary>Manual, platform-internal block.</summary>
    Internal
}

/// <summary>
/// A proactive/imported block on a wallet. Distinct from <c>User.Status =
/// Suspended</c> (platform-originated suspensions): this denylist carries provenance,
/// is bulk-importable, and never creates a User row — a sanctioned wallet that never
/// signed up is still blocked. Id is "{walletChecksum}:{source}" so the same wallet
/// can hold one entry per source and removal is a direct, source-scoped delete.
/// </summary>
public class BlockedWallet
{
    /// <summary>"{walletChecksum}:{source}" — also the Mongo _id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Checksum-normalized wallet address.</summary>
    public string WalletAddress { get; set; } = string.Empty;

    public BlockSource Source { get; set; }

    /// <summary>Human-readable reason. Never surfaced to the blocked tenant.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>External reference (case number, SDN list id, feed id).</summary>
    public string? Reference { get; set; }

    /// <summary>Admin wallet that added it, or "system" for imports.</summary>
    public string AddedBy { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
