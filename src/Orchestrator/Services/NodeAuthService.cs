using System.Security.Cryptography;
using MongoDB.Driver;
using Orchestrator.Persistence;
using Orchestrator.Models;

namespace Orchestrator.Background;

/// <summary>
/// Service interface for node authentication operations
/// </summary>
public interface INodeAuthService
{
    /// <summary>
    /// Generate a new auth token for a node and persist it to MongoDB
    /// </summary>
    Task<string> CreateTokenAsync(string nodeId, string? createdFromIp = null);

    /// <summary>
    /// Validate a node's auth token
    /// </summary>
    Task<bool> ValidateTokenAsync(string nodeId, string token);

    /// <summary>
    /// Revoke a node's auth token
    /// </summary>
    Task<bool> RevokeTokenAsync(string nodeId);

    /// <summary>
    /// Check if a token is about to expire (within warning threshold)
    /// </summary>
    Task<bool> IsTokenExpiringAsync(string nodeId, TimeSpan warningThreshold);

    /// <summary>
    /// Clean up expired tokens from database
    /// </summary>
    Task<int> CleanupExpiredTokensAsync();

    /// <summary>
    /// Get token information for a node
    /// </summary>
    Task<NodeAuthToken?> GetTokenInfoAsync(string nodeId);
}

/// <summary>
/// Manages node authentication tokens with MongoDB persistence.
/// Provides security-first token management with expiration and validation.
/// </summary>
public class NodeAuthService : INodeAuthService
{
    private readonly DataStore _dataStore;
    private readonly IMongoCollection<NodeAuthToken>? _tokensCollection;
    private readonly ILogger<NodeAuthService> _logger;

    // Token lifetime configuration
    private readonly TimeSpan _tokenLifetime = TimeSpan.FromDays(90);
    private readonly TimeSpan _expirationWarningThreshold = TimeSpan.FromDays(7);

    public NodeAuthService(
        DataStore dataStore,
        IMongoDatabase? database,
        ILogger<NodeAuthService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;

        if (database != null)
        {
            _tokensCollection = database.GetCollection<NodeAuthToken>("nodeAuthTokens");

            // Create indexes for efficient queries
            CreateIndexesAsync().ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning(
                "MongoDB not configured - node auth tokens will only be stored in memory. " +
                "Tokens will be lost on orchestrator restart!");
        }
    }

    private async Task CreateIndexesAsync()
    {
        if (_tokensCollection == null) return;

        try
        {
            // Index on nodeId for fast lookups (unique)
            var nodeIdIndex = Builders<NodeAuthToken>.IndexKeys.Ascending(t => t.NodeId);
            await _tokensCollection.Indexes.CreateOneAsync(
                new CreateIndexModel<NodeAuthToken>(
                    nodeIdIndex,
                    new CreateIndexOptions { Unique = true, Name = "idx_nodeId" }));

            // Index on expiresAt for cleanup queries
            var expiresAtIndex = Builders<NodeAuthToken>.IndexKeys.Ascending(t => t.ExpiresAt);
            await _tokensCollection.Indexes.CreateOneAsync(
                new CreateIndexModel<NodeAuthToken>(
                    expiresAtIndex,
                    new CreateIndexOptions { Name = "idx_expiresAt" }));

            // TTL index to automatically delete expired tokens
            var ttlIndex = Builders<NodeAuthToken>.IndexKeys.Ascending(t => t.ExpiresAt);
            await _tokensCollection.Indexes.CreateOneAsync(
                new CreateIndexModel<NodeAuthToken>(
                    ttlIndex,
                    new CreateIndexOptions
                    {
                        ExpireAfter = TimeSpan.Zero, // Delete immediately when expiresAt is reached
                        Name = "idx_ttl_expiresAt"
                    }));

            _logger.LogInformation("✓ Node auth token indexes created successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create node auth token indexes");
        }
    }

    /// <summary>
    /// Generate a new cryptographically secure auth token for a node
    /// </summary>
    public async Task<string> CreateTokenAsync(string nodeId, string? createdFromIp = null)
    {
        try
        {
            // Generate cryptographically secure random token
            var token = GenerateSecureToken();
            var tokenHash = HashToken(token);

            var authToken = new NodeAuthToken
            {
                NodeId = nodeId,
                TokenHash = tokenHash,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_tokenLifetime),
                LastUsedAt = DateTime.UtcNow,
                CreatedFromIp = createdFromIp,
                IsRevoked = false
            };

            // Persist to MongoDB if available
            if (_tokensCollection != null)
            {
                // Use ReplaceOne with upsert to handle re-registration
                var filter = Builders<NodeAuthToken>.Filter.Eq(t => t.NodeId, nodeId);
                await _tokensCollection.ReplaceOneAsync(
                    filter,
                    authToken,
                    new ReplaceOptions { IsUpsert = true });

                _logger.LogInformation(
                    "✓ Created new auth token for node {NodeId}, expires at {ExpiresAt:u}",
                    nodeId, authToken.ExpiresAt);
            }
            else
            {
                _logger.LogWarning(
                    "Token created in memory only for node {NodeId} - will be lost on restart",
                    nodeId);
            }

            // Also store in memory for fast validation
            _dataStore.NodeAuthTokens[nodeId] = tokenHash;

            return token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create auth token for node {NodeId}", nodeId);
            throw;
        }
    }

    /// <summary>
    /// Validate a node's authentication token
    /// </summary>
    public async Task<bool> ValidateTokenAsync(string nodeId, string token)
    {
        try
        {
            var tokenHash = HashToken(token);

            // First check in-memory cache for fast path
            if (_dataStore.NodeAuthTokens.TryGetValue(nodeId, out var cachedHash))
            {
                if (cachedHash == tokenHash)
                {
                    // Update last used timestamp asynchronously (don't block validation)
                    _ = UpdateLastUsedAsync(nodeId);
                    return true;
                }
            }

            // If not in cache, check MongoDB
            if (_tokensCollection != null)
            {
                var filter = Builders<NodeAuthToken>.Filter.And(
                    Builders<NodeAuthToken>.Filter.Eq(t => t.NodeId, nodeId),
                    Builders<NodeAuthToken>.Filter.Eq(t => t.TokenHash, tokenHash));

                var authToken = await _tokensCollection.Find(filter).FirstOrDefaultAsync();

                if (authToken != null && authToken.IsValid)
                {
                    // Restore to in-memory cache
                    _dataStore.NodeAuthTokens[nodeId] = tokenHash;

                    // Update last used timestamp
                    _ = UpdateLastUsedAsync(nodeId);

                    _logger.LogDebug("✓ Token validated for node {NodeId} (loaded from MongoDB)", nodeId);
                    return true;
                }

                if (authToken != null && !authToken.IsValid)
                {
                    if (authToken.IsRevoked)
                    {
                        _logger.LogWarning("Token validation failed for node {NodeId}: Token revoked", nodeId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Token validation failed for node {NodeId}: Token expired at {ExpiresAt:u}",
                            nodeId, authToken.ExpiresAt);
                    }
                }
            }

            _logger.LogWarning("Token validation failed for node {NodeId}: Token not found or invalid", nodeId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating token for node {NodeId}", nodeId);
            return false;
        }
    }

    /// <summary>
    /// Update the last used timestamp for a token
    /// </summary>
    private async Task UpdateLastUsedAsync(string nodeId)
    {
        if (_tokensCollection == null) return;

        try
        {
            var filter = Builders<NodeAuthToken>.Filter.Eq(t => t.NodeId, nodeId);
            var update = Builders<NodeAuthToken>.Update.Set(t => t.LastUsedAt, DateTime.UtcNow);

            await _tokensCollection.UpdateOneAsync(filter, update);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update last used timestamp for node {NodeId}", nodeId);
        }
    }

    /// <summary>
    /// Revoke a node's authentication token
    /// </summary>
    public async Task<bool> RevokeTokenAsync(string nodeId)
    {
        try
        {
            // Remove from in-memory cache
            _dataStore.NodeAuthTokens.TryRemove(nodeId, out _);

            // Mark as revoked in MongoDB
            if (_tokensCollection != null)
            {
                var filter = Builders<NodeAuthToken>.Filter.Eq(t => t.NodeId, nodeId);
                var update = Builders<NodeAuthToken>.Update.Set(t => t.IsRevoked, true);

                var result = await _tokensCollection.UpdateOneAsync(filter, update);

                if (result.ModifiedCount > 0)
                {
                    _logger.LogInformation("✓ Revoked auth token for node {NodeId}", nodeId);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke token for node {NodeId}", nodeId);
            return false;
        }
    }

    /// <summary>
    /// Check if a token is about to expire
    /// </summary>
    public async Task<bool> IsTokenExpiringAsync(string nodeId, TimeSpan warningThreshold)
    {
        try
        {
            if (_tokensCollection == null) return false;

            var filter = Builders<NodeAuthToken>.Filter.Eq(t => t.NodeId, nodeId);
            var authToken = await _tokensCollection.Find(filter).FirstOrDefaultAsync();

            if (authToken == null) return false;

            var timeUntilExpiration = authToken.ExpiresAt - DateTime.UtcNow;
            return timeUntilExpiration <= warningThreshold;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking token expiration for node {NodeId}", nodeId);
            return false;
        }
    }

    /// <summary>
    /// Clean up expired and revoked tokens from database
    /// </summary>
    public async Task<int> CleanupExpiredTokensAsync()
    {
        try
        {
            if (_tokensCollection == null) return 0;

            var filter = Builders<NodeAuthToken>.Filter.Or(
                Builders<NodeAuthToken>.Filter.Lt(t => t.ExpiresAt, DateTime.UtcNow),
                Builders<NodeAuthToken>.Filter.Eq(t => t.IsRevoked, true));

            var result = await _tokensCollection.DeleteManyAsync(filter);

            if (result.DeletedCount > 0)
            {
                _logger.LogInformation(
                    "✓ Cleaned up {Count} expired/revoked node auth tokens",
                    result.DeletedCount);
            }

            return (int)result.DeletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup expired tokens");
            return 0;
        }
    }

    /// <summary>
    /// Get token information for a node
    /// </summary>
    public async Task<NodeAuthToken?> GetTokenInfoAsync(string nodeId)
    {
        try
        {
            if (_tokensCollection == null) return null;

            var filter = Builders<NodeAuthToken>.Filter.Eq(t => t.NodeId, nodeId);
            return await _tokensCollection.Find(filter).FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get token info for node {NodeId}", nodeId);
            return null;
        }
    }

    // ============================================================================
    // Token Generation and Hashing Utilities
    // ============================================================================

    /// <summary>
    /// Generate a cryptographically secure random token
    /// </summary>
    private static string GenerateSecureToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Hash a token using SHA256 for secure storage
    /// </summary>
    private static string HashToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}