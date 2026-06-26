using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Orchestrator.Interfaces;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Services;

/// <summary>
/// Durable, single-use refresh-token store. See <see cref="IRefreshTokenStore"/>
/// for the rationale. Mirrors <see cref="JwtRevocationService"/>: owns its own
/// Mongo collection via an injected nullable <see cref="IMongoDatabase"/>, with
/// an in-memory fallback when Mongo is not configured.
///
/// Expiry is enforced by a Mongo TTL index on <c>ExpiresAt</c> (server-side
/// auto-purge) and re-checked on read. The in-memory fallback sweeps on a timer.
/// </summary>
public class RefreshTokenStore : IRefreshTokenStore, IDisposable
{
    private readonly IMongoCollection<RefreshTokenRecord>? _collection;
    private readonly ILogger<RefreshTokenStore> _logger;

    // In-memory fallback: hash -> (userId, expiresAt). Used only when Mongo is null.
    private readonly ConcurrentDictionary<string, (string UserId, DateTime ExpiresAt)> _memory = new();
    private readonly Timer? _sweepTimer;

    public RefreshTokenStore(IMongoDatabase? database, ILogger<RefreshTokenStore> logger)
    {
        _logger = logger;

        if (database != null)
        {
            _collection = database.GetCollection<RefreshTokenRecord>("refresh_tokens");

            // TTL index: Mongo deletes a document once ExpiresAt is in the past.
            try
            {
                _collection.Indexes.CreateOne(new CreateIndexModel<RefreshTokenRecord>(
                    Builders<RefreshTokenRecord>.IndexKeys.Ascending(r => r.ExpiresAt),
                    new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
                _collection.Indexes.CreateOne(new CreateIndexModel<RefreshTokenRecord>(
                    Builders<RefreshTokenRecord>.IndexKeys.Ascending(r => r.TokenHash),
                    new CreateIndexOptions { Unique = true }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not ensure refresh_tokens indexes");
            }
        }
        else
        {
            _logger.LogWarning("RefreshTokenStore running in-memory only (no MongoDB) — " +
                "tokens will not survive restart. Configure MongoDB for production.");
            // Sweep expired entries every 5 minutes in the in-memory fallback.
            _sweepTimer = new Timer(_ => SweepMemory(), null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
    }

    public async Task StoreAsync(string token, string userId, DateTime expiresAt, CancellationToken ct = default)
    {
        var hash = Hash(token);

        if (_collection != null)
        {
            await _collection.InsertOneAsync(new RefreshTokenRecord
            {
                TokenHash = hash,
                UserId = userId,
                ExpiresAt = expiresAt,
                CreatedAt = DateTime.UtcNow
            }, cancellationToken: ct);
        }
        else
        {
            _memory[hash] = (userId, expiresAt);
        }
    }

    public async Task<string?> ConsumeAsync(string token, CancellationToken ct = default)
    {
        var hash = Hash(token);

        if (_collection != null)
        {
            // Atomic single-use: find-and-delete. Concurrent callers presenting the
            // same token race here; exactly one receives the document.
            var deleted = await _collection.FindOneAndDeleteAsync(
                r => r.TokenHash == hash, cancellationToken: ct);

            if (deleted == null) return null;
            if (deleted.ExpiresAt < DateTime.UtcNow) return null; // expired but not yet TTL-purged
            return deleted.UserId;
        }

        if (_memory.TryRemove(hash, out var entry))
        {
            if (entry.ExpiresAt < DateTime.UtcNow) return null;
            return entry.UserId;
        }
        return null;
    }

    public async Task<string?> PeekAsync(string token, CancellationToken ct = default)
    {
        var hash = Hash(token);

        if (_collection != null)
        {
            var found = await _collection.Find(r => r.TokenHash == hash).FirstOrDefaultAsync(ct);
            if (found == null || found.ExpiresAt < DateTime.UtcNow) return null;
            return found.UserId;
        }

        if (_memory.TryGetValue(hash, out var entry) && entry.ExpiresAt >= DateTime.UtcNow)
            return entry.UserId;
        return null;
    }

    public async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        var hash = Hash(token);
        if (_collection != null)
            await _collection.DeleteOneAsync(r => r.TokenHash == hash, ct);
        else
            _memory.TryRemove(hash, out _);
    }

    public async Task RevokeAllForUserAsync(string userId, CancellationToken ct = default)
    {
        if (_collection != null)
        {
            await _collection.DeleteManyAsync(r => r.UserId == userId, ct);
        }
        else
        {
            foreach (var kv in _memory.Where(kv => kv.Value.UserId == userId).ToList())
                _memory.TryRemove(kv.Key, out _);
        }
    }

    private static string Hash(string token)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(token)));
    }

    private void SweepMemory()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _memory.Where(kv => kv.Value.ExpiresAt < now).ToList())
            _memory.TryRemove(kv.Key, out _);
    }

    public void Dispose() => _sweepTimer?.Dispose();
}

public class RefreshTokenRecord
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>SHA-256 hex of the refresh token. The raw token is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
