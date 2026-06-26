namespace Orchestrator.Interfaces;

/// <summary>
/// Current Terms of Service document, its version, and the SHA-256 of its bytes.
/// </summary>
public record TosDocument(string Version, string Hash, string Text);

/// <summary>
/// Manages the current Terms of Service and wallet-signed acceptances.
///
/// Self-contained (owns its Mongo collection + a positive-result cache) following
/// the JwtRevocationService pattern, so it can be injected anywhere the acceptance
/// gate is needed without growing DataStore. Registered as a singleton: the
/// document text and hash are loaded once at startup.
/// </summary>
public interface ITosService
{
    /// <summary>The current ToS document, version, and hash (loaded at startup).</summary>
    TosDocument GetCurrent();

    /// <summary>
    /// The canonical message a wallet must sign to accept the current ToS.
    /// Built server-side from the current version + hash + the supplied timestamp
    /// so the signature is bound to the exact document in effect — a stale or
    /// attacker-substituted document cannot be silently accepted.
    /// </summary>
    string BuildAcceptanceMessage(string walletAddress, long timestamp);

    /// <summary>
    /// True if the wallet has a recorded acceptance of the CURRENT version+hash.
    /// Checked server-side at the VM-creation gate — never via a JWT claim.
    /// </summary>
    Task<bool> HasAcceptedCurrentAsync(string walletAddress, CancellationToken ct = default);

    /// <summary>Persist a verified acceptance of the current ToS for this wallet.</summary>
    Task RecordAcceptanceAsync(string walletAddress, string signature, long timestamp, CancellationToken ct = default);
}
