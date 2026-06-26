namespace Orchestrator.Interfaces;

/// <summary>
/// Durable, single-use refresh-token store. MongoDB-backed with an in-memory
/// fallback, following the same pattern as <see cref="IJwtRevocationService"/>
/// and ITosService so it survives orchestrator restarts and works across
/// instances.
///
/// WHY THIS EXISTS: the previous implementation kept refresh tokens in a
/// <c>static Dictionary</c> inside UserService. That dictionary was wiped on
/// every restart/redeploy (so every user was forced to re-sign) and was not
/// shared across instances. This store is the durable boundary that removes
/// both failure modes.
///
/// SINGLE-USE / ROTATION: <see cref="ConsumeAsync"/> is atomic
/// (find-and-delete). Two concurrent refreshes presenting the same token race
/// at the storage boundary — exactly one wins, the other gets null. That is
/// the correct place to enforce one-time use, not a client-side lock.
///
/// AT-REST: only a SHA-256 hash of the token is stored, never the raw token
/// (same approach the codebase already uses for API keys), so a database leak
/// does not yield usable refresh tokens.
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>Persist a freshly issued refresh token for a user.</summary>
    Task StoreAsync(string token, string userId, DateTime expiresAt, CancellationToken ct = default);

    /// <summary>
    /// Atomically consume (delete) a refresh token. Returns the owning userId
    /// if the token existed and had not expired; otherwise null. Single-use:
    /// a second call with the same token returns null.
    /// </summary>
    Task<string?> ConsumeAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Non-destructive check used by the SIWE getSession endpoint. Returns the
    /// owning userId if a valid (unexpired) token exists, without rotating it.
    /// </summary>
    Task<string?> PeekAsync(string token, CancellationToken ct = default);

    /// <summary>Revoke a single refresh token (logout on one device).</summary>
    Task RevokeAsync(string token, CancellationToken ct = default);

    /// <summary>Revoke every refresh token for a user (logout everywhere).</summary>
    Task RevokeAllForUserAsync(string userId, CancellationToken ct = default);
}
