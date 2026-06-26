namespace Orchestrator.Interfaces;

/// <summary>
/// Issues and verifies single-use authentication nonces for Sign-In With
/// Ethereum (EIP-4361). MongoDB-backed with an in-memory fallback.
///
/// WHY THIS EXISTS: the previous flow embedded a fresh <c>Guid.NewGuid()</c>
/// nonce in the message but never stored or checked it — replay protection
/// rested entirely on a 5-minute timestamp window, so a captured
/// (message, signature) pair could be replayed within that window. A real
/// single-use nonce closes that gap and is required by the SIWE standard.
///
/// Nonces are short-lived (minutes) and public (they appear in the signed
/// message), so they are stored in clear and expire via a TTL index. Losing
/// the in-memory fallback on restart only forces a fresh nonce fetch on the
/// next sign-in attempt — an acceptable, rare cost — which is why nonces use a
/// lighter durability bar than refresh tokens.
/// </summary>
public interface INonceStore
{
    /// <summary>Generate, store, and return a fresh single-use nonce.</summary>
    Task<string> IssueAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically consume a nonce. Returns true exactly once for a valid,
    /// unexpired nonce; false if unknown, already used, or expired.
    /// </summary>
    Task<bool> ConsumeAsync(string nonce, CancellationToken ct = default);
}
