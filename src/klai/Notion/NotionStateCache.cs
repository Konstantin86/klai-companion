using klai.Notion.Model;
using Microsoft.Extensions.Configuration;

namespace klai.Notion;

public class NotionStateCache
{
    public DateTime LastSyncedAt { get; set; }
    public List<NotionValue> Values { get; set; } = new();
    public List<NotionNote> Notes { get; set; } = new();
    
    // Helper method for the webhook to find context instantly
    public NotionValue? GetValueByTopicId(int topicId, IConfiguration config)
    {
        var valueName = config[$"AiAgentConfig:TopicMappings:{topicId}"];
        return Values.FirstOrDefault(v => v.Name == valueName);
    }
}