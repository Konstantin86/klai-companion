public class AiAgentConfig
{
    public ModelsConfig Models { get; set; } = new();
    public string[] RoutingTriggers { get; set; } = [];
    public TokenBudgetsConfig TokenBudgets { get; set; } = new();
    public Dictionary<string, string> Notion { get; set; } = new();
    public Dictionary<string, string> TopicMappings { get; set; } = new();
    public Dictionary<string, string> GoalPersonas { get; set; } = new();
    public TimersConfig Timers { get; set; } = new();
}

public class ModelsConfig
{
    public string Fast { get; set; } = string.Empty;
    public string Advanced { get; set; } = string.Empty;
}

public class TokenBudgetsConfig
{
    public int ChatHistoryMax { get; set; }
}

public class TimersConfig
{
    // Default to 5 if someone forgets to add it to the JSON
    public int NotionSyncIntervalMinutes { get; set; } = 5; 
}