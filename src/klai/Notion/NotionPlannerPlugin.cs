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
    private readonly string _projectsDbId;

    public NotionPlannerPlugin(INotionClient notionClient, NotionSyncWorker syncWorker, IConfiguration config)
    {
        _notionClient = notionClient;
        _syncWorker = syncWorker;
        _tasksDbId = config["AiAgentConfig:Notion:TasksDbId"] ?? throw new ArgumentNullException("TasksDbId is missing");
        _projectsDbId = config["AiAgentConfig:Notion:ProjectsDbId"] ?? throw new ArgumentNullException("ProjectsDbId is missing");
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
                { "Name", new TitlePropertyValue { Title = new List<RichTextBase> { new RichTextTextInput { Text = new Text { Content = title } } } } },
                { "Type", new SelectPropertyValue { Select = new SelectOption { Name = "Regular" } } }
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
                Properties = properties,
                Icon = new EmojiObject { Emoji = "✔️" }
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

    [KernelFunction("CreateRecurringTasks")]
    [Description("Creates multiple instances of the same task in Notion across different dates. Use this when the user asks to schedule a recurring task (like workouts or routines) across a specific schedule.")]
    public async Task<string> CreateRecurringTasksAsync(
        [Description("The title of the recurring task (e.g., '10 km run/walk')")] string title,
        [Description("An array of target completion dates (yyyy-MM-dd) for each instance of the task.")] string[] targetDates,
        [Description("The exact name of the parent Project. If unknown or not applicable, leave this empty.")] string projectName = "")
    {
        try
        {
            if (targetDates == null || targetDates.Length == 0)
            {
                return "Error: No target dates provided for the recurring task.";
            }

            var cache = _syncWorker.CurrentState;
            string? targetProjectId = null;
            NotionProject? targetProjectNode = null;

            // 1. Resolve Project Name to Notion UUID
            if (!string.IsNullOrWhiteSpace(projectName))
            {
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
                    return $"Error: Could not find an active project named '{projectName}'. Please check the active state context.";
                }
            }

            int successCount = 0;
            var failedDates = new List<string>();

            // 2. Loop through the dates and create tasks
            foreach (var dateString in targetDates)
            {
                var properties = new Dictionary<string, PropertyValue>
                {
                    { "Name", new TitlePropertyValue { Title = new List<RichTextBase> { new RichTextTextInput { Text = new Text { Content = title } } } } },
                    { "Type", new SelectPropertyValue { Select = new SelectOption { Name = "Regular" } } }
                };

                if (!string.IsNullOrEmpty(targetProjectId))
                {
                    properties.Add("Projects", new RelationPropertyValue { Relation = new List<ObjectId> { new ObjectId { Id = targetProjectId } } });
                }

                DateTime? parsedDate = null;
                if (DateTime.TryParse(dateString, out var tempDate))
                {
                    parsedDate = tempDate;
                    properties.Add("Date", new DatePropertyValue { Date = new Date { Start = parsedDate } });
                }
                else
                {
                    failedDates.Add($"{dateString} (Invalid Format)");
                    continue; // Skip invalid dates
                }

                var newPageParams = new PagesCreateParameters
                {
                    Parent = new DatabaseParentInput { DatabaseId = _tasksDbId },
                    Properties = properties,
                    Icon = new EmojiObject { Emoji = "✔️" }
                };

                try
                {
                    // 3. Send to Notion API
                    var createdPage = await _notionClient.Pages.CreateAsync(newPageParams);

                    // 4. Optimistic Caching
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
                    successCount++;
                }
                catch
                {
                    failedDates.Add(dateString);
                }

                // 5. Rate Limit Protection
                // Notion strictly enforces a 3 requests per second limit. 
                await Task.Delay(350); 
            }

            // 6. Build the result summary
            string resultMessage = $"Successfully created {successCount} instances of '{title}'" +
                                   (!string.IsNullOrEmpty(projectName) ? $" under project '{projectName}'." : ".");

            if (failedDates.Any())
            {
                resultMessage += $"\nFailed to create tasks for these dates: {string.Join(", ", failedDates)}";
            }

            return resultMessage;
        }
        catch (Exception ex)
        {
            return $"Failed to process recurring tasks batch in Notion. Error: {ex.Message}";
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

    [KernelFunction("CreateProjectWithTasks")]
    [Description("Creates a new Project in Notion and automatically generates multiple Tasks linked to it. Use this when the user asks to plan a project.")]
    public async Task<string> CreateProjectWithTasksAsync(
        [Description("The exact name of the parent Goal to link this project to. (Required)")] string goalName,
        [Description("The title of the new Project")] string projectName,
        [Description("The target start date of the project (yyyy-MM-dd)")] string startDate,
        [Description("The target end date of the project (yyyy-MM-dd)")] string endDate,
        [Description("A JSON array of tasks. Format EXACTLY like this: [{\"Name\": \"Task 1\", \"Date\": \"2026-03-15\"}]")] string tasksJson)
    {
        try
        {
            var cache = _syncWorker.CurrentState;

            // 1. Resolve Goal Name to Notion UUID
            var targetGoalNode = cache.Values
                .SelectMany(v => v.Goals)
                .FirstOrDefault(g => g.Name.Contains(goalName, StringComparison.OrdinalIgnoreCase));

            if (targetGoalNode == null)
                return $"Error: Could not find an active goal named '{goalName}'.";

            // 2. Create the Project Page
            var projProperties = new Dictionary<string, PropertyValue>
            {
                { "Name", new TitlePropertyValue { Title = new List<RichTextBase> { new RichTextTextInput { Text = new Text { Content = projectName } } } } },
                { "Goals", new RelationPropertyValue { Relation = new List<ObjectId> { new ObjectId { Id = targetGoalNode.Id } } } }
            };


            // Use this for the Project dates
            if (DateTime.TryParse(startDate, out var pStart) && DateTime.TryParse(endDate, out var pEnd))
            {
                // .Date zeroes out the time, and Unspecified removes the timezone
                var cleanStart = DateTime.SpecifyKind(pStart.Date, DateTimeKind.Unspecified);
                var cleanEnd = DateTime.SpecifyKind(pEnd.Date, DateTimeKind.Unspecified);
                
                projProperties.Add("Start", new DatePropertyValue { Date = new Date { Start = cleanStart, End = null } });
                projProperties.Add("End", new DatePropertyValue { Date = new Date { Start = cleanEnd, End = null } });
            }




            // if (DateTime.TryParse(startDate, out var pStart) && DateTime.TryParse(endDate, out var pEnd))
            // {
            //     projProperties.Add("Start", new DatePropertyValue { Date = new Date { Start = pStart, End = null } });
            //     projProperties.Add("End", new DatePropertyValue { Date = new Date { Start = pEnd, End = null } });
            // }

            var projResult = await _notionClient.Pages.CreateAsync(new PagesCreateParameters
            {
                Parent = new DatabaseParentInput { DatabaseId = _projectsDbId },
                Properties = projProperties
            });

            // 3. Clean up LLM JSON output (in case it includes markdown backticks)
            string cleanJson = tasksJson.Trim();
            if (cleanJson.StartsWith("```json")) cleanJson = cleanJson.Substring(7);
            if (cleanJson.StartsWith("```")) cleanJson = cleanJson.Substring(3);
            if (cleanJson.EndsWith("```")) cleanJson = cleanJson.Substring(0, cleanJson.Length - 3);
            
            // 4. Deserialize Tasks
            var taskDefinitions = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(cleanJson.Trim());
            var createdTasks = new List<NotionTask>();

            // 5. Create Tasks in a loop (Respecting Rate Limits)
            if (taskDefinitions != null)
            {
                foreach (var taskDef in taskDefinitions)
                {
                    if (!taskDef.TryGetValue("Name", out string? taskTitle)) continue;
                    taskDef.TryGetValue("Date", out string? taskDateStr);

                    var taskProperties = new Dictionary<string, PropertyValue>
                    {
                        { "Name", new TitlePropertyValue { Title = new List<RichTextBase> { new RichTextTextInput { Text = new Text { Content = taskTitle } } } } },
                        { "Projects", new RelationPropertyValue { Relation = new List<ObjectId> { new ObjectId { Id = projResult.Id } } } }
                    };

                    DateTime? parsedTaskDate = null;
                    if (DateTime.TryParse(taskDateStr, out var tDate))
                    {
                        parsedTaskDate = tDate;
                        taskProperties.Add("Date", new DatePropertyValue { Date = new Date { Start = parsedTaskDate } });
                    }

                    var taskResult = await _notionClient.Pages.CreateAsync(new PagesCreateParameters
                    {
                        Parent = new DatabaseParentInput { DatabaseId = _tasksDbId },
                        Properties = taskProperties
                    });

                    createdTasks.Add(new NotionTask { Id = taskResult.Id, Name = taskTitle, Date = parsedTaskDate, IsCompleted = false });
                    
                    // Critical: Prevent Notion API 429 Too Many Requests error
                    await Task.Delay(400); 
                }
            }

            // 6. Optimistic Caching! Inject the whole tree into memory
            var newProjectNode = new NotionProject
            {
                Id = projResult.Id,
                Name = projectName,
                Tasks = createdTasks
            };
            targetGoalNode.Projects.Add(newProjectNode);

            return $"Success! Created project '{projectName}' under goal '{goalName}' with {createdTasks.Count} sequenced tasks.";
        }
        catch (Exception ex)
        {
            return $"Failed to create project and tasks. Error: {ex.Message}";
        }
    }
}