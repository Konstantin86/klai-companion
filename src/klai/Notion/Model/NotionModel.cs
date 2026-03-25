using System.Text;
using System.Linq;
using Newtonsoft.Json;
using Notion.Client;
using System.Collections.Generic;

namespace klai.Notion.Model;



public class SafeNotionPage
{
    // Reusing the SDK's properties, but omitting the Icon entirely
    public string Id { get; set; }
    
    [Newtonsoft.Json.JsonProperty("url")]
    public string Url { get; set; }
    
    [Newtonsoft.Json.JsonProperty("properties")]
    public IDictionary<string, PropertyValue> Properties { get; set; }
    
    [Newtonsoft.Json.JsonProperty("created_time")]
    public DateTime CreatedTime { get; set; }
    
    [Newtonsoft.Json.JsonProperty("last_edited_time")]
    public DateTime LastEditedTime { get; set; }
}

public class SafeQueryResponse
{
    [Newtonsoft.Json.JsonProperty("results")]
    public List<SafeNotionPage> Results { get; set; }
    
    [Newtonsoft.Json.JsonProperty("next_cursor")]
    public string NextCursor { get; set; }
    
    [Newtonsoft.Json.JsonProperty("has_more")]
    public bool HasMore { get; set; }
}

public class NotionActiveContext
{
    public NotionValue Value { get; set; }
    public List<NotionTask> FloatingTasks { get; set; }

    public string ToTokenOptimizedString()
    {
        var valueStr = Value.ToTokenOptimizedString();

        string floatingTasksInboxStr = "";

        if (FloatingTasks.Any(t => !t.IsCompleted))
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n--- FLOATING TASKS (INBOX) ---");
            foreach (var task in FloatingTasks.OrderBy(m => m.Date).Where(t => !t.IsCompleted))
            {
                string dateStr = task.Date.HasValue ? $" [Due: {task.Date:yyyy-MM-dd}]" : "";
                sb.AppendLine($"- {task.Name}{dateStr}");
            }
            floatingTasksInboxStr = sb.ToString().TrimEnd();
        }

        string floatingTasksRecentlyDoneStr = "";

        if (FloatingTasks.Any(t => t.IsCompleted))
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n--- FLOATING TASKS (RECENTLY DONE) ---");
            foreach (var task in FloatingTasks.OrderByDescending(m => m.Date).Where(t => t.IsCompleted))
            {
                string dateStr = task.Date.HasValue ? $" [Completed On: {task.Date:yyyy-MM-dd}]" : "";
                sb.AppendLine($"- {task.Name}{dateStr}");
            }
            floatingTasksRecentlyDoneStr = sb.ToString().TrimEnd();
        }

        return valueStr + "\n" + floatingTasksInboxStr + '\n' + floatingTasksRecentlyDoneStr;
    }
}

public class NotionValue
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public List<NotionGoal> Goals { get; set; } = new();

    public string ToTokenOptimizedString()
    {
        var sb = new StringBuilder();

        foreach (var goal in Goals)
        {
            sb.AppendLine($"Goal: {goal.Name}");

            // Only append dates if they exist to save space
            if (goal.StartDate != default) sb.AppendLine($"Start: {goal.StartDate:yyyy-MM-dd}");
            if (goal.EndDate != default) sb.AppendLine($"End: {goal.EndDate:yyyy-MM-dd}");
            sb.AppendLine($"Status: {goal.Status}");

            if (goal.Projects != null && goal.Projects.Any())
            {
                sb.AppendLine("Projects:");
                foreach (var proj in goal.Projects)
                {
                    sb.AppendLine($"  - {proj.Name}");
                    if (proj.Start != default) sb.AppendLine($"    Start: {proj.Start:yyyy-MM-dd}");
                    if (proj.End != default) sb.AppendLine($"    End: {proj.End:yyyy-MM-dd}");
                    sb.AppendLine($"    Status: {proj.Status}");

                    if (proj.Tasks != null && proj.Tasks.Any())
                    {
                        sb.AppendLine("    Tasks:");
                        foreach (var task in proj.Tasks.OrderBy(m => m.Date))
                        {
                            // Format tasks nicely: [x] for completed, [ ] for open
                            string checkbox = task.IsCompleted ? "[x]" : "[ ]";
                            string dateStr = task.Date.HasValue ? $" (Due: {task.Date.Value:yyyy-MM-dd})" : "";

                            sb.AppendLine($"      {checkbox} {task.Name}{dateStr}");
                        }
                    }
                }
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}

public class NotionGoal
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public List<NotionProject> Projects { get; set; } = new();
}

public class NotionProject
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public List<NotionTask> Tasks { get; set; } = new();
}

public class NotionTask
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTime? Date { get; set; }
}

public class NotionNote
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    // Relational pointers
    public List<string> ProjectIds { get; set; } = new();
    public List<string> GoalIds { get; set; } = new();
    public List<string> ValueIds { get; set; } = new();

    public bool IsArchived { get; set; }
    public bool IsFavourite { get; set; }
    public DateTime? CreatedTime { get; set; }
    public DateTime? LastEditedTime { get; set; }
    public string Type { get; set; } = string.Empty;

    // The actual text inside the Notion page
    public string Content { get; set; } = string.Empty;
}