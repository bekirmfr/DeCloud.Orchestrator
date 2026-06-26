namespace Orchestrator.Models;

/// <summary>
/// A wallet-signed acceptance of a specific Terms of Service version.
///
/// The signature is verifiable (EIP-191): recovering the signer from the
/// canonical acceptance message must yield <see cref="WalletAddress"/>. This is
/// stronger than a checkbox — it is cryptographically bound to the wallet's key
/// and to the exact document (version + hash) accepted.
///
/// Id is "{walletChecksum}:{version}" so re-accepting the same version upserts
/// idempotently and an acceptance lookup is a direct primary-key read.
/// </summary>
public class TosAcceptance
{
    /// <summary>"{walletChecksum}:{version}" — also the Mongo _id (convention).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Checksum-normalized wallet address that signed the acceptance.</summary>
    public string WalletAddress { get; set; } = string.Empty;

    /// <summary>ToS version string in effect at acceptance time.</summary>
    public string TosVersion { get; set; } = string.Empty;

    /// <summary>SHA-256 (lowercase hex) of the exact ToS document bytes accepted.</summary>
    public string TosHash { get; set; } = string.Empty;

    /// <summary>The raw signature over the canonical acceptance message.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>When the acceptance was signed (from the signed timestamp).</summary>
    public DateTime SignedAt { get; set; }
}
