using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Notion.Client;
using klai.Notion;
using klai.Notion.Model;

namespace klai.Notion;

public class NotionPlannerPlugin
{
    private readonly INotionClient _notionClient;
    private readonly NotionSyncWorker _syncWorker;
    private readonly string _tasksDbId;

    public NotionPlannerPlugin(INotionClient notionClient, NotionSyncWorker syncWorker, IConfiguration config)
    {
        _notionClient = notionClient;
        _syncWorker = syncWorker;
        _tasksDbId = config["AiAgentConfig:Notion:TasksDbId"] ?? throw new ArgumentNullException("TasksDbId is missing");
    }

    [KernelFunction("CreateNotionTask")]
    [Description("Creates a new actionable task in Notion. Use this to add items to the user's to-do list.")]
    public async Task<string> CreateTaskAsync(
        [Description("The title of the new task")] string title,
        [Description("The exact name of the parent Project. If unknown or not applicable, leave this empty.")] string projectName = "",
        [Description("The target completion date (yyyy-MM-dd). Leave empty if no date is specified.")] string targetDate = "")
    {
        try
        {
            var cache = _syncWorker.CurrentState;
            string? targetProjectId = null;
            NotionProject? targetProjectNode = null;

            // 1. Resolve Project Name to Notion UUID (Your brilliant idea!)
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                // Search the nested graph for a matching project name
                targetProjectNode = cache.Values
                    .SelectMany(v => v.Goals)
                    .SelectMany(g => g.Projects)
                    .FirstOrDefault(p => p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));

                if (targetProjectNode != null)
                {
                    targetProjectId = targetProjectNode.Id;
                }
                else
                {
                    // Optional: Return early if they specified a project but we can't find it
                    return $"Error: Could not find an active project named '{projectName}'. Please check the active state context.";
                }
            }

            // 2. Build the complex Notion SDK Payload
            var properties = new Dictionary<string, PropertyValue>
            {
                { "Name", new TitlePropertyValue { Title = new List<RichTextBase> { new RichTextTextInput { Text = new Text { Content = title } } } } }
            };

            if (!string.IsNullOrEmpty(targetProjectId))
            {
                properties.Add("Projects", new RelationPropertyValue { Relation = new List<ObjectId> { new ObjectId { Id = targetProjectId } } });
            }

            DateTime? parsedDate = null;
            if (DateTime.TryParse(targetDate, out var tempDate))
            {
                parsedDate = tempDate;
                properties.Add("Date", new DatePropertyValue { Date = new Date { Start = parsedDate } });
            }

            var newPageParams = new PagesCreateParameters
            {
                Parent = new DatabaseParentInput { DatabaseId = _tasksDbId },
                Properties = properties
            };

            // 3. Send to Notion API
            var createdPage = await _notionClient.Pages.CreateAsync(newPageParams);

            // 4. Optimistic Caching: Inject into local state so the LLM sees it immediately!
            if (targetProjectNode != null)
            {
                targetProjectNode.Tasks ??= new List<NotionTask>();
                targetProjectNode.Tasks.Add(new NotionTask
                {
                    Id = createdPage.Id,
                    Name = title,
                    Date = parsedDate,
                    IsCompleted = false
                });
            }

            return $"Success! Task '{title}' created successfully" + 
                   (!string.IsNullOrEmpty(projectName) ? $" under project '{projectName}'." : ".");
        }
        catch (Exception ex)
        {
            return $"Failed to create task in Notion. Error: {ex.Message}";
        }
    }

    [KernelFunction("LinkTaskToProject")]
    [Description("Updates an existing Notion task by linking it to an existing Project. Use this when the user asks to move, attach, or link a task to a project.")]
    public async Task<string> LinkTaskToProjectAsync(
        [Description("The exact title (or part of the title) of the task to link")] string taskName,
        [Description("The exact name of the parent Project to link it to")] string projectName)
    {
        try
        {
            var cache = _syncWorker.CurrentState;

            // 1. Resolve Project Name to Notion UUID (from our fast local cache)
            var targetProjectNode = cache.Values
                .SelectMany(v => v.Goals)
                .SelectMany(g => g.Projects)
                .FirstOrDefault(p => p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));

            if (targetProjectNode == null)
            {
                return $"Error: Could not find an active project named '{projectName}'. Check the active context.";
            }

            // 2. Query Notion API to find the floating task by name

            var floatingTask = cache.FloatingTasks.FirstOrDefault(t => t.Name.Contains(taskName, StringComparison.OrdinalIgnoreCase));

            if (floatingTask == null) return "Error: Could not find that floating task.";

            //var queryParams = new DatabasesQueryParameters
            //{
            //    Filter = new RichTextFilter("Name", contains: taskName)
            //};
            
            //var searchResult = await _notionClient.Databases.QueryAsync(_tasksDbId, queryParams);
            //var taskPage = searchResult.Results.OfType<Page>().FirstOrDefault();

            // if (taskPage == null)
            // {
            //     return $"Error: Could not find a task containing the name '{taskName}' in the database.";
            // }

            // 3. Update the task in Notion to link it to the Project
            var updateProperties = new Dictionary<string, PropertyValue>
            {
                { "Projects", new RelationPropertyValue { Relation = new List<ObjectId> { new ObjectId { Id = targetProjectNode.Id } } } }
            };

            await _notionClient.Pages.UpdatePropertiesAsync(floatingTask.Id, updateProperties);

            // 4. Optimistic Caching: Attach it to the local project tree so the LLM sees it immediately
            targetProjectNode.Tasks ??= new List<NotionTask>();
            
            // Check if it's already there to prevent duplicates in the UI
            if (!targetProjectNode.Tasks.Any(t => t.Id == floatingTask.Id))
            {
                // We extract the date safely if it has one
                //DateTime? taskDate = null;
                // if (floatingTask.Date.Properties.TryGetValue("Date", out var prop) && prop is DatePropertyValue dateProp)
                // {
                //     taskDate = dateProp.Date.Start.HasValue ? dateProp.Date.Start.Value.LocalDateTime : null;
                // }

                targetProjectNode.Tasks.Add(new NotionTask
                {
                    Id = floatingTask.Id,
                    Name = floatingTask.Name,
                    Date = floatingTask.Date,
                    IsCompleted = false
                });
            }

            return $"Success! Task '{taskName}' has been linked to the project '{projectName}'.";
        }
        catch (Exception ex)
        {
            return $"Failed to link task. Error: {ex.Message}";
        }
    }
}