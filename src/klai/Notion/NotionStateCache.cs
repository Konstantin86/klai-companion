using klai.Notion.Model;
using Microsoft.Extensions.Configuration;

namespace klai.Notion;

public class NotionStateCache
{
    public DateTime LastSyncedAt { get; set; }
    public List<NotionValue> Values { get; set; } = new();
    public List<NotionNote> Notes { get; set; } = new();
    public List<NotionTask> FloatingTasks { get; set; } = new();
    
    // Helper method for the webhook to find context instantly
    public NotionValue? GetValueByTopicId(int topicId, IConfiguration config)
    {
        var valueName = config[$"AiAgentConfig:TopicMappings:{topicId}"];
        return Values.FirstOrDefault(v => v.Name == valueName);
    }

    public NotionTask? GetTaskByName(string taskName)
    {
        var floatingTask = FloatingTasks.FirstOrDefault(t => t.Name.Contains(taskName, StringComparison.OrdinalIgnoreCase));
        if (floatingTask != null) return floatingTask;

        var projectTask = Values
            .SelectMany(v => v.Goals)
            .SelectMany(g => g.Projects)
            .SelectMany(p => p.Tasks ?? new List<NotionTask>())
            .FirstOrDefault(t => t.Name.Contains(taskName, StringComparison.OrdinalIgnoreCase));

        return projectTask;
    }

    public NotionActiveContext? GetActiveContextForTopic(int topicId, IConfiguration config)
    {
        // 1. Map the Telegram Topic ID to the Notion Value Name
        var valueName = config[$"AiAgentConfig:TopicMappings:{topicId}"];
        
        if (string.IsNullOrEmpty(valueName)) return null;

        // 2. Find the full Value tree in our RAM cache
        var fullValue = Values.FirstOrDefault(v => v.Name == valueName);
        if (fullValue == null) return null;

        // 3. Clone the root object so we don't accidentally delete data from the main RAM cache
        var leanValue = new NotionValue 
        { 
            Id = fullValue.Id, 
            Name = fullValue.Name, 
            SystemPrompt = fullValue.SystemPrompt 
        };

        var oneWeekAgo = DateTime.UtcNow.AddDays(-7);

        // 4. The Smart Filter: Only keep active goals, active projects, and recent/open tasks
        foreach (var goal in fullValue.Goals.Where(g => g.Status != "Done" && g.Status != "Archived"))
        {
            var leanGoal = new NotionGoal 
            { 
                Id = goal.Id, 
                Name = goal.Name, 
                Status = goal.Status, 
                StartDate = goal.StartDate, 
                EndDate = goal.EndDate 
            };

            foreach (var project in goal.Projects.Where(p => p.Status != "Completed" && p.Status != "Archived"))
            {
                var leanProject = new NotionProject 
                { 
                    Id = project.Id, 
                    Name = project.Name, 
                    Status = project.Status, 
                    Start = project.Start, 
                    End = project.End 
                };

                // Filter Tasks: Only open tasks, or tasks completed in the last 7 days
                leanProject.Tasks = project.Tasks.Where(t => 
                    !t.IsCompleted || 
                    (t.IsCompleted && t.Date >= oneWeekAgo)
                ).ToList();

                leanGoal.Projects.Add(leanProject);
            }
            
            leanValue.Goals.Add(leanGoal);
        }

        var notionActiveContext = new NotionActiveContext
        {
            Value = leanValue,
            FloatingTasks = FloatingTasks.Where(t => // Filter Tasks: Only open tasks, or tasks completed in the last 7 days
                    (t.Date.HasValue &&
                    (!t.IsCompleted || 
                    (t.IsCompleted && t.Date >= oneWeekAgo)))
                ).ToList()
        };

        return notionActiveContext;
    }

    internal IEnumerable<NotionGoal> GetAllGoals()
    {
        return Values.SelectMany(v => v.Goals);
    }
}