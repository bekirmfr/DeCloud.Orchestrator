using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Orchestrator.Interfaces;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Orchestrator.Services;

/// <summary>
/// Single-use SIWE nonce store. See <see cref="INonceStore"/>. Same
/// self-contained, Mongo-or-memory pattern as <see cref="JwtRevocationService"/>.
/// </summary>
public class NonceStore : INonceStore, IDisposable
{
    // Nonces are valid for a short window between issue and signature.
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(5);

    private readonly IMongoCollection<NonceRecord>? _collection;
    private readonly ILogger<NonceStore> _logger;

    private readonly ConcurrentDictionary<string, DateTime> _memory = new();
    private readonly Timer? _sweepTimer;

    public NonceStore(IMongoDatabase? database, ILogger<NonceStore> logger)
    {
        _logger = logger;

        if (database != null)
        {
            _collection = database.GetCollection<NonceRecord>("auth_nonces");
            try
            {
                _collection.Indexes.CreateOne(new CreateIndexModel<NonceRecord>(
                    Builders<NonceRecord>.IndexKeys.Ascending(r => r.ExpiresAt),
                    new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
                _collection.Indexes.CreateOne(new CreateIndexModel<NonceRecord>(
                    Builders<NonceRecord>.IndexKeys.Ascending(r => r.Nonce),
                    new CreateIndexOptions { Unique = true }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not ensure auth_nonces indexes");
            }
        }
        else
        {
            _sweepTimer = new Timer(_ => SweepMemory(), null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
    }

    public async Task<string> IssueAsync(CancellationToken ct = default)
    {
        // URL-safe, high-entropy nonce.
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var expiresAt = DateTime.UtcNow.Add(NonceTtl);

        if (_collection != null)
            await _collection.InsertOneAsync(new NonceRecord { Nonce = nonce, ExpiresAt = expiresAt }, cancellationToken: ct);
        else
            _memory[nonce] = expiresAt;

        return nonce;
    }

    public async Task<bool> ConsumeAsync(string nonce, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nonce)) return false;

        if (_collection != null)
        {
            var deleted = await _collection.FindOneAndDeleteAsync(r => r.Nonce == nonce, cancellationToken: ct);
            return deleted != null && deleted.ExpiresAt >= DateTime.UtcNow;
        }

        if (_memory.TryRemove(nonce, out var expiresAt))
            return expiresAt >= DateTime.UtcNow;
        return false;
    }

    private void SweepMemory()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _memory.Where(kv => kv.Value < now).ToList())
            _memory.TryRemove(kv.Key, out _);
    }

    public void Dispose() => _sweepTimer?.Dispose();
}

public class NonceRecord
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string Nonce { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}
