using MongoDB.Driver;
using Orchestrator.Interfaces;
using System.Collections.Concurrent;

namespace Orchestrator.Services;

public class JwtRevocationService : IJwtRevocationService
{
    private readonly IMongoCollection<RevokedJwt>? _collection;
    private readonly ConcurrentDictionary<string, bool> _cache = new();
    private readonly ILogger<JwtRevocationService> _logger;

    public JwtRevocationService(
        IMongoDatabase? database,
        ILogger<JwtRevocationService> logger)
    {
        _collection = database?.GetCollection<RevokedJwt>("revoked_jwts");
        _logger = logger;
    }

    public bool IsRevoked(string jti)
    {
        if (string.IsNullOrEmpty(jti))
            return false;

        return _cache.ContainsKey(jti);
    }

    public async Task RevokeAsync(string jti, string nodeId, string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jti))
            return;

        // Cache first (immediately effective)
        _cache.TryAdd(jti, true);

        // Persist to MongoDB
        if (_collection != null)
        {
            var record = new RevokedJwt
            {
                Jti = jti,
                NodeId = nodeId,
                Reason = reason,
                RevokedAt = DateTime.UtcNow
            };

            try
            {
                await _collection.ReplaceOneAsync(
                    r => r.Jti == jti,
                    record,
                    new ReplaceOptions { IsUpsert = true },
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to persist JWT revocation for {Jti} (node {NodeId}). " +
                    "Token is revoked in memory but may reappear after restart.",
                    jti, nodeId);
            }
        }

        _logger.LogInformation(
            "JWT revoked: jti={Jti}, node={NodeId}, reason={Reason}",
            jti, nodeId, reason);
    }

    public async Task LoadFromStoreAsync(CancellationToken ct = default)
    {
        if (_collection == null)
        {
            _logger.LogWarning("JWT revocation running in-memory only (no MongoDB)");
            return;
        }

        try
        {
            var records = await _collection
                .Find(_ => true)
                .ToListAsync(ct);

            foreach (var record in records)
            {
                _cache.TryAdd(record.Jti, true);
            }

            _logger.LogInformation(
                "Loaded {Count} revoked JWTs from MongoDB",
                records.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load revoked JWTs from MongoDB");
        }
    }
}

public class RevokedJwt
{
    public string Jti { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime RevokedAt { get; set; }
}