using System;

namespace klai.Chat.Model;

public class ChatMessageEntity
{
    public int Id { get; set; }
    public int? TopicId { get; set; } // Maps to Telegram's MessageThreadId
    public string Role { get; set; } = string.Empty; // "User", "Assistant", or "System"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsVectorized { get; set; }
}