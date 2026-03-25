using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Notion.Client;
using klai.Notion.Model;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;


namespace klai.Notion;

public class NotionSyncWorker : BackgroundService
{
    private readonly INotionClient _notionClient;
    private readonly ILogger<NotionSyncWorker> _logger;
    private readonly IConfiguration _config;

    // This singleton will be injected into your Telegram Webhook later
    public NotionStateCache CurrentState { get; private set; } = new();

    public NotionSyncWorker(IConfiguration config, ILogger<NotionSyncWorker> logger)
    {
        _config = config;
        _logger = logger;

        var secret = _config["NOTION_SECRET"] ?? throw new ArgumentNullException("NOTION_SECRET is missing");
        _notionClient = NotionClientFactory.Create(new ClientOptions { AuthToken = secret });
    }

    public async Task<List<SafeNotionPage>> GetAllTasksSafelyAsync(string databaseId, string integrationToken, string? archiveColumnName = null, DateTime? modifiedAfter = null)
    {
        var allPages = new List<SafeNotionPage>();

        using var httpClient = new HttpClient();
        string url = $"https://api.notion.com/v1/data_sources/{databaseId}/query";

        bool hasMore = true;
        string nextCursor = null;

        while (hasMore)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", $"Bearer {integrationToken}");

            // Bumped to the version that officially supports the /data_sources endpoint
            request.Headers.Add("Notion-Version", "2026-03-11");

            // Build the payload
            var requestBody = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(nextCursor))
            {
                requestBody["start_cursor"] = nextCursor;
            }

            var filters = new List<object>();

            // 1. Checkbox Filter
            if (!string.IsNullOrEmpty(archiveColumnName))
            {
                filters.Add(new
                {
                    property = archiveColumnName,
                    checkbox = new { equals = false }
                });
            }

            // 2. Timestamp Filter
            if (modifiedAfter.HasValue)
            {
                filters.Add(new
                {
                    timestamp = "last_edited_time",
                    last_edited_time = new
                    {
                        on_or_after = modifiedAfter.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                    }
                });
            }

            if (filters.Count == 1)
            {
                requestBody["filter"] = filters[0];
            }
            else if (filters.Count > 1)
            {
                requestBody["filter"] = new { and = filters };
            }

            var jsonPayload = JsonConvert.SerializeObject(requestBody);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var rawJson = await response.Content.ReadAsStringAsync();

            var pageData = JsonConvert.DeserializeObject<SafeQueryResponse>(rawJson);

            if (pageData?.Results != null)
            {
                allPages.AddRange(pageData.Results);
            }

            hasMore = pageData?.HasMore ?? false;
            nextCursor = pageData?.NextCursor;
        }

        return allPages;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting Notion data sync...");
                // Read the fresh value on every loop iteration!
                var lookbackMonths = _config.GetValue<int>("AiAgentConfig:Notion:TaskLookbackMonths", -2);
                var cutoffDate = DateTime.UtcNow.AddMonths(lookbackMonths);

                await BuildInMemoryGraphAsync(cutoffDate);

                //temporary code for debug purposes:
                //CurrentState = System.Text.Json.JsonSerializer.Deserialize<NotionStateCache>(await System.IO.File.ReadAllTextAsync("notion_state_debug.json"));

                var intervalMinutes = _config.GetValue<int>("AiAgentConfig:Timers:NotionSyncIntervalMinutes", 5);
                _logger.LogInformation("Notion sync complete. Next run in 5 minutes.");

                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing with Notion");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task BuildInMemoryGraphAsync(DateTime cutoffDate)
    {
        var valuesDbId = _config["AiAgentConfig:Notion:ValuesDbPageId"];
        var goalsDbId = _config["AiAgentConfig:Notion:GoalsDbPageId"];
        var projectsDbId = _config["AiAgentConfig:Notion:ProjectsDbPageId"];
        var tasksDbId = _config["AiAgentConfig:Notion:TasksDbPageId"];
        var notesDbId = _config["AiAgentConfig:Notion:NotesDbPageId"];

        var valuesTask = FetchAllPagesAsync(valuesDbId, "Archive");
        var goalsTask = FetchAllPagesAsync(goalsDbId, "Archived");
        var projectsTask = FetchAllPagesAsync(projectsDbId, "Archive");
        var tasksTask = FetchAllPagesAsync(tasksDbId, null, cutoffDate);
        var notesTask = FetchAllPagesAsync(notesDbId, "Archived");

        await Task.WhenAll(valuesTask, goalsTask, projectsTask, tasksTask, notesTask);

        var rawValues = valuesTask.Result;
        var rawGoals = goalsTask.Result;
        var rawProjects = projectsTask.Result;
        var rawTasks = tasksTask.Result;
        var rawNotes = notesTask.Result;

        var newState = new NotionStateCache { LastSyncedAt = DateTime.UtcNow };

        // 2. Build the graph (Linking Parents to Children)
        foreach (var page in rawValues)
        {
            var valueName = GetTitle(page, "Name");
            if (string.IsNullOrEmpty(valueName)) continue;

            var valueNode = new NotionValue
            {
                Id = page.Id,
                Name = valueName,
                // Assign persona from config right now so the LLM has it ready
                SystemPrompt = _config[$"AiAgentConfig:GoalPersonas:{valueName}"]
                               ?? _config["AiAgentConfig:GoalPersonas:Default"]!
            };

            // Find Goals linked to this Value
            var relatedGoals = rawGoals.Where(g => GetRelationIds(g, "Values").Contains(page.Id));

            foreach (var goalPage in relatedGoals)
            {
                var goalNode = new NotionGoal
                {
                    Id = goalPage.Id,
                    Name = GetTitle(goalPage, "Name"),
                    Status = GetStatus(goalPage, "Status"),
                    StartDate = GetDate(goalPage, "Start"),
                    EndDate = GetDate(goalPage, "End")
                };

                // Find Projects linked to this Goal
                var relatedProjects = rawProjects.Where(p => GetRelationIds(p, "Goals").Contains(goalPage.Id));

                foreach (var projPage in relatedProjects)
                {
                    var projectNode = new NotionProject
                    {
                        Id = projPage.Id,
                        Name = GetTitle(projPage, "Name"),
                        Status = GetStatus(projPage, "Status"),
                        Start = GetDate(projPage, "Start"),
                        End = GetDate(projPage, "End")
                    };

                    // Find Tasks linked to this Project
                    var relatedTasks = rawTasks.Where(t => GetRelationIds(t, "Projects").Contains(projPage.Id));

                    projectNode.Tasks = relatedTasks.Select(t => new NotionTask
                    {
                        Id = t.Id,
                        Name = GetTitle(t, "Name"),
                        IsCompleted = GetCheckbox(t, "Completed"),
                        Date = GetDate(t, "Date")
                    }).ToList();

                    goalNode.Projects.Add(projectNode);
                }

                valueNode.Goals.Add(goalNode);
            }

            newState.Values.Add(valueNode);
        }

        // Find Tasks linked to this Project
        var floatingTaskPages = rawTasks.Where(t => GetRelationIds(t, "Projects").Count == 0);

        foreach (var tPage in floatingTaskPages)
        {
            newState.FloatingTasks.Add(new NotionTask
            {
                Id = tPage.Id,
                Name = GetTitle(tPage, "Name"),
                IsCompleted = GetCheckbox(tPage, "Completed"),
                Date = GetDate(tPage, "Date")
            });
        }

        foreach (var notePage in rawNotes)
        {
            var type = GetSelect(notePage, "Type");

            // Filter: Only pull in "Note" or "Affirmation"
            if (type != "Note" && type != "Affirmation") continue;

            var noteNode = new NotionNote
            {
                Id = notePage.Id,
                Name = GetTitle(notePage, "Name"),
                Url = GetUrlProperty(notePage, "URL"),
                ProjectIds = GetRelationIds(notePage, "Projects"),
                GoalIds = GetRelationIds(notePage, "Goals"),
                ValueIds = GetRelationIds(notePage, "Values"),
                IsArchived = GetCheckbox(notePage, "Archived"),
                IsFavourite = GetCheckbox(notePage, "Favourite"),
                CreatedTime = GetCreatedTime(notePage, "Created time"),
                LastEditedTime = GetLastEditedTime(notePage, "Last edited time"),
                Type = type
            };

            // Fetch the actual text blocks
            noteNode.Content = await FetchPageContentAsync(notePage.Id);

            if (!string.IsNullOrWhiteSpace(noteNode.Content))
            {
                newState.Notes.Add(noteNode);
            }
        }
        // Swap the cache atomically
        CurrentState = newState;

        // --- TEMPORARY DEBUG CODE ---
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var jsonString = System.Text.Json.JsonSerializer.Serialize(newState, options);
            await System.IO.File.WriteAllTextAsync("notion_state_debug.json", jsonString);
            _logger.LogInformation("Saved Notion state to notion_state_debug.json for manual inspection.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write debug JSON file.");
        }
    }

    // --- Helper Methods to safely parse Notion API Properties ---

    private async Task<List<SafeNotionPage>> FetchAllPagesAsync(string? databaseId, string? archiveColumnName, DateTime? modifiedAfter = null)
    {
        if (string.IsNullOrEmpty(databaseId)) return new List<SafeNotionPage>();
        var response = new List<SafeNotionPage>();

        try
        {
            var secret = _config["NOTION_SECRET"] ?? throw new ArgumentNullException("NOTION_SECRET is missing");
            response = await GetAllTasksSafelyAsync(databaseId, secret, archiveColumnName, modifiedAfter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching pages from Notion for Database ID: {DatabaseId}", databaseId);
        }

        return response;
    }

    private string GetTitle(SafeNotionPage page, string propertyName)
    {
        if (page.Properties.TryGetValue(propertyName, out var prop) && prop is TitlePropertyValue titleProp)
        {
            return titleProp.Title.FirstOrDefault()?.PlainText ?? "";
        }
        return "";
    }

    private DateTime? GetDate(SafeNotionPage page, string propertyName)
    {
        if (page.Properties.TryGetValue(propertyName, out var prop) && prop is DatePropertyValue dateProp && dateProp.Date != null)
        {
            // Notion usually stores the primary date in the 'Start' property 
            // even if it's just a single day (not a range).
            return dateProp.Date.Start?.LocalDateTime;
        }
        return null;
    }

    private string GetStatus(SafeNotionPage page, string propertyName)
    {
        if (page.Properties.TryGetValue(propertyName, out var prop) && prop is StatusPropertyValue statusProp)
        {
            return statusProp.Status?.Name ?? "";
        }
        return "";
    }

    private bool GetCheckbox(SafeNotionPage page, string propertyName)
    {
        if (page.Properties.TryGetValue(propertyName, out var prop) && prop is CheckboxPropertyValue checkboxProp)
        {
            return checkboxProp.Checkbox;
        }
        return false;
    }

    private List<string> GetRelationIds(SafeNotionPage page, string propertyName)
    {
        if (page.Properties.TryGetValue(propertyName, out var prop) && prop is RelationPropertyValue relationProp)
        {
            return relationProp.Relation.Select(r => r.Id).ToList();
        }
        return new List<string>();
    }

    private string GetSelect(SafeNotionPage page, string propertyName)
    {
        if (page.Properties.TryGetValue(propertyName, out var prop) && prop is SelectPropertyValue selectProp)
        {
            return selectProp.Select?.Name ?? "";
        }
        return "";
    }

    private string GetUrlProperty(SafeNotionPage page, string propertyName)
    {
        if (page.Properties.TryGetValue(propertyName, out var prop) && prop is UrlPropertyValue urlProp)
        {
            return urlProp.Url ?? "";
        }
        return "";
    }

    private DateTime? GetCreatedTime(SafeNotionPage page, string propertyName)
    {
        if (page.Properties.TryGetValue(propertyName, out var prop) && prop is CreatedTimePropertyValue timeProp)
        {
            return DateTime.Parse(timeProp.CreatedTime);
        }
        return null;
    }

    private DateTime? GetLastEditedTime(SafeNotionPage page, string propertyName)
    {
        if (page.Properties.TryGetValue(propertyName, out var prop) && prop is LastEditedTimePropertyValue timeProp)
        {
            return DateTime.Parse(timeProp.LastEditedTime);
        }
        return null;
    }

    private async Task<string> FetchPageContentAsync(string pageId)
    {
        try
        {
            // Note: Pagination may be needed here if your notes are extremely long (>100 blocks)
            var blocks = await _notionClient.Blocks.RetrieveChildrenAsync(new BlockRetrieveChildrenRequest { BlockId = pageId });
            var contentBuilder = new System.Text.StringBuilder();

            foreach (var block in blocks.Results)
            {
                // Extract text from standard paragraphs
                if (block is ParagraphBlock paragraph && paragraph.Paragraph.RichText != null)
                {
                    contentBuilder.AppendLine(string.Join("", paragraph.Paragraph.RichText.Select(rt => rt.PlainText)));
                }
                // Extract text from headers
                else if (block is HeadingOneBlock h1 && h1.Heading_1.RichText != null)
                {
                    contentBuilder.AppendLine(string.Join("", h1.Heading_1.RichText.Select(rt => rt.PlainText)));
                }
                else if (block is HeadingTwoBlock h2 && h2.Heading_2.RichText != null)
                {
                    contentBuilder.AppendLine(string.Join("", h2.Heading_2.RichText.Select(rt => rt.PlainText)));
                }
                else if (block is HeadingThreeBlock h3 && h3.Heading_3.RichText != null)
                {
                    contentBuilder.AppendLine(string.Join("", h3.Heading_3.RichText.Select(rt => rt.PlainText)));
                }
                // Extract text from bullet points
                else if (block is BulletedListItemBlock bullet && bullet.BulletedListItem.RichText != null)
                {
                    contentBuilder.AppendLine("- " + string.Join("", bullet.BulletedListItem.RichText.Select(rt => rt.PlainText)));
                }
            }

            return contentBuilder.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch content for page {PageId}", pageId);
            return string.Empty;
        }
    }
}