using System.Text;
using System.Linq;

namespace klai.Notion.Model;

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
                    foreach (var task in proj.Tasks)
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