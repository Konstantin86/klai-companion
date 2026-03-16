using System.ComponentModel;
using Microsoft.SemanticKernel;
using Notion.Client;
using klai.Notion;
using klai.Notion.Model;

namespace klai.Notion;

public class NotionTaskModifierPlugin
{
    private readonly INotionClient _notionClient;
    private readonly NotionSyncWorker _syncWorker;

    public NotionTaskModifierPlugin(INotionClient notionClient, NotionSyncWorker syncWorker)
    {
        _notionClient = notionClient;
        _syncWorker = syncWorker;
    }

    [KernelFunction("RescheduleTask")]
    [Description("Changes the scheduled date of a specific Notion task. Use this when asked to move a task, delay a project, or release the schedule.")]
    public async Task<string> RescheduleTaskAsync(
        [Description("The exact current name/title of the task as it appears in the active state")] string taskName,
        [Description("The new target date in YYYY-MM-DD format, or leave empty/null to clear the date completely")] string? newDate)
    {
        try
        {
            var targetTask = _syncWorker.CurrentState.GetTaskByName(taskName);
            if (targetTask == null)
            {
                return $"Error: Could not find a task named '{taskName}'. Please verify the exact name from the CURRENT STATE.";
            }

            // 2. Build the Notion API Payload
            var properties = new Dictionary<string, PropertyValue>();
            DateTime? parsedDate = null;

            if (string.IsNullOrWhiteSpace(newDate))
            {
                properties["Date"] = new DatePropertyValue { Date = null };
            }
            else if (DateTime.TryParse(newDate, out var tempDate))
            {
                // Clean the timezone so Notion accepts it as a pure date
                parsedDate = DateTime.SpecifyKind(tempDate.Date, DateTimeKind.Unspecified);
                properties["Date"] = new DatePropertyValue
                {
                    Date = new Date { Start = parsedDate, End = null }
                };
            }
            else
            {
                return $"Error: '{newDate}' is not a valid date format. Please use YYYY-MM-DD.";
            }

            // 3. Send the patch request to Notion
            await _notionClient.Pages.UpdatePropertiesAsync(targetTask.Id, properties);

            // 4. Optimistic Caching! Update the local object reference immediately.
            targetTask.Date = parsedDate;

            return $"Successfully rescheduled '{taskName}' to {(parsedDate.HasValue ? parsedDate.Value.ToString("yyyy-MM-dd") : "No Date")}.";
        }
        catch (Exception ex)
        {
            return $"Failed to reschedule task. Error: {ex.Message}";
        }
    }

    [KernelFunction("RenameTask")]
    [Description("Changes the title/name of a specific Notion task.")]
    public async Task<string> RenameTaskAsync(
        [Description("The exact current name/title of the task")] string currentTaskName,
        [Description("The new name for the task")] string newTaskName)
    {
        try
        {
            // 1. Find the task in local memory
            var targetTask = _syncWorker.CurrentState.GetTaskByName(currentTaskName);
            if (targetTask == null)
            {
                return $"Error: Could not find a task named '{currentTaskName}'.";
            }

            // 2. Build the Notion API Payload
            var properties = new Dictionary<string, PropertyValue>
            {
                { "Name", new TitlePropertyValue
                    {
                        Title = new List<RichTextBase>
                        {
                            new RichTextTextInput { Text = new Text { Content = newTaskName } }
                        }
                    }
                }
            };

            // 3. Send the patch request
            await _notionClient.Pages.UpdatePropertiesAsync(targetTask.Id, properties);

            // 4. Optimistic Caching! Update the local object reference immediately.
            targetTask.Name = newTaskName;

            return $"Successfully renamed task from '{currentTaskName}' to '{newTaskName}'.";
        }
        catch (Exception ex)
        {
            return $"Failed to rename task. Error: {ex.Message}";
        }
    }
}