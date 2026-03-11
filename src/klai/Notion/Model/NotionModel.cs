namespace klai.Notion.Model;

public class NotionValue
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public List<NotionGoal> Goals { get; set; } = new();
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