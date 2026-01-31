using MongoDB.Bson.Serialization.Attributes;

namespace Orchestrator.Models;

/// <summary>
/// Template category for organizing marketplace
/// </summary>
public class TemplateCategory
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Display name (e.g., "AI & Machine Learning")
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// URL-friendly slug (e.g., "ai-ml")
    /// </summary>
    public string Slug { get; set; } = string.Empty;
    
    /// <summary>
    /// Category description
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Icon emoji or identifier
    /// </summary>
    public string IconEmoji { get; set; } = "📦";
    
    /// <summary>
    /// Display order (lower = higher priority)
    /// </summary>
    public int DisplayOrder { get; set; }
    
    /// <summary>
    /// Cached count of templates in this category
    /// </summary>
    public int TemplateCount { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
