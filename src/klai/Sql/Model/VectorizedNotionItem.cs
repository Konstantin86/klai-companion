using System;

namespace klai.Sql.Model;

public class VectorizedNotionItem
{
    // The internal SQLite ID
    public int Id { get; set; }
    
    // The string ID from Notion (e.g., "4b6df265-...")
    public string NotionId { get; set; } = string.Empty;
    
    // What type of item it was (Goal, Project, or Task)
    public string ItemType { get; set; } = string.Empty; 
    
    // When we processed it
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}