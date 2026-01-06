using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Orchestrator.Models;

/// <summary>
/// Node authentication token stored in MongoDB with expiration support.
/// Enables persistent authentication across orchestrator restarts.
/// </summary>
public class NodeAuthToken
{
    /// <summary>
    /// MongoDB document ID
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

    /// <summary>
    /// Node ID this token belongs to
    /// </summary>
    [BsonElement("nodeId")]
    public required string NodeId { get; set; }

    /// <summary>
    /// SHA256 hash of the authentication token (never store plaintext)
    /// </summary>
    [BsonElement("tokenHash")]
    public required string TokenHash { get; set; }

    /// <summary>
    /// When this token was created
    /// </summary>
    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this token expires. Default is 90 days for node tokens.
    /// Nodes should re-register before expiration.
    /// </summary>
    [BsonElement("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When this token was last used (updated on each successful validation)
    /// </summary>
    [BsonElement("lastUsedAt")]
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this token has been revoked manually
    /// </summary>
    [BsonElement("isRevoked")]
    public bool IsRevoked { get; set; } = false;

    /// <summary>
    /// IP address of the node when token was created (for audit purposes)
    /// </summary>
    [BsonElement("createdFromIp")]
    public string? CreatedFromIp { get; set; }

    /// <summary>
    /// Check if token is currently valid (not expired and not revoked)
    /// </summary>
    [BsonIgnore]
    public bool IsValid => !IsRevoked && ExpiresAt > DateTime.UtcNow;
}