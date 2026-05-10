namespace Orchestrator.Interfaces;

/// <summary>
/// Tracks revoked JWTs. MongoDB-backed with in-memory cache for
/// fast hot-path checks. Survives orchestrator restarts.
///
/// Integration point: JwtBearerEvents.OnTokenValidated checks
/// IsRevoked() before accepting any JWT. Revocation is permanent
/// for a given token — there is no un-revoke.
/// </summary>
public interface IJwtRevocationService
{
    /// <summary>Check if a JWT (by its jti claim) is revoked.</summary>
    bool IsRevoked(string jti);

    /// <summary>Revoke a JWT. Persists to MongoDB and caches in memory.</summary>
    Task RevokeAsync(string jti, string nodeId, string reason,
        CancellationToken ct = default);

    /// <summary>Load all revoked JTIs from MongoDB into memory cache.</summary>
    Task LoadFromStoreAsync(CancellationToken ct = default);
}