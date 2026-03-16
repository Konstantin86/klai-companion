using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.EntityFrameworkCore;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using klai.Data;
using System.Text;
using klai.Notion;
using klai.Sql.Model;
using klai.Notion.Model;
using Microsoft.Extensions.Configuration;

namespace klai.RAG;

public class MemoryConsolidationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MemoryConsolidationWorker> _logger;
    private readonly IConfiguration _config;
    private readonly string _collectionName = "klai_long_term_memory";

    public MemoryConsolidationWorker(IServiceProvider serviceProvider, ILogger<MemoryConsolidationWorker> logger, IConfiguration config)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Memory Consolidation Worker started.");


        bool qdrantReady = false;
        while (!qdrantReady && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureCollectionExistsAsync();
                qdrantReady = true;
                _logger.LogInformation("✅ Successfully connected to Qdrant!");
            }
            catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unavailable)
            {
                _logger.LogWarning("⏳ Qdrant is still initializing. Retrying in 5 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error connecting to Qdrant. Retrying...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Starting nightly memory consolidation cycle...");

            try
            {
                await ConsolidateChatHistoryAsync(stoppingToken);
                await ConsolidateClosedNotionItemsAsync(stoppingToken);
                await ConsolidateNotionNotesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during memory consolidation.");
            }

            var intervalMinutes = _config.GetValue<int>("AiAgentConfig:RAG:ConsolidationIntervalMinutes", 1440);

            _logger.LogInformation("Consolidation cycle complete. Sleeping for {Interval} minutes...", intervalMinutes);

            // 3. Sleep for the configured duration
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private async Task ConsolidateNotionNotesAsync(CancellationToken token)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KlaiDbContext>();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();
        var embeddingService = kernel.GetRequiredService<ITextEmbeddingGenerationService>("embedding");
        var qdrantClient = scope.ServiceProvider.GetRequiredService<QdrantClient>();

        var notionSyncWorker = scope.ServiceProvider.GetRequiredService<NotionSyncWorker>();
        var notionCache = notionSyncWorker.CurrentState;

        var processedIds = await dbContext.VectorizedNotionItems
            .Where(v => v.ItemType == "Note")
            .Select(v => v.NotionId)
            .ToHashSetAsync(token);

        var notesToProcess = notionCache.Notes?
            .Where(n => !processedIds.Contains(n.Id))
            .ToList() ?? new List<NotionNote>(); // Assuming your class is named Note

        if (!notesToProcess.Any()) return;

        _logger.LogInformation($"Found {notesToProcess.Count} new Notion notes to vectorize.");

        foreach (var note in notesToProcess)
        {
            if (string.IsNullOrWhiteSpace(note.Content))
            {
                // Skip empty notes but write to ledger so we don't keep checking them
                await MarkNoteAsProcessed(dbContext, note.Id, token);
                continue;
            }

            // 2. Chunk the note content (Targeting ~1000 chars per chunk)
            var chunks = ChunkText(note.Content, maxChunkLength: 1000);

            // 3. Vectorize and Push each chunk
            for (int i = 0; i < chunks.Count; i++)
            {
                string chunkContent = chunks[i];

                // Prepend the title and date to EVERY chunk so the AI always has context 
                // even if it only retrieves chunk #3 of a long note.
                string formattedContent = $"Note Title: {note.Name}\nLast Edited: {note.LastEditedTime:yyyy-MM-dd}\n\n{chunkContent}";

                var embedding = await embeddingService.GenerateEmbeddingAsync(formattedContent, cancellationToken: token);

                var pointId = Guid.NewGuid();
                var point = new PointStruct
                {
                    Id = new PointId { Uuid = pointId.ToString() },
                    Vectors = embedding.ToArray(),
                    Payload =
                    {
                        ["content"] = formattedContent,
                        ["source"] = "notion_notes",
                        ["item_type"] = "Note",
                        ["notion_id"] = note.Id,
                        ["chunk_index"] = i // Save the chunk index for debugging
                    }
                };

                await qdrantClient.UpsertAsync(_collectionName, new[] { point }, cancellationToken: token);
            }

            // 4. Write receipt to local ledger once all chunks are uploaded
            await MarkNoteAsProcessed(dbContext, note.Id, token);
            _logger.LogInformation($"Successfully vectorized Notion Note: '{note.Name}' ({chunks.Count} chunks)");
        }
    }

    // --- HELPER METHODS ---
    private async Task MarkNoteAsProcessed(KlaiDbContext dbContext, string notionId, CancellationToken token)
    {
        dbContext.VectorizedNotionItems.Add(new VectorizedNotionItem
        {
            NotionId = notionId,
            ItemType = "Note",
            ProcessedAt = DateTime.UtcNow
        });
        await dbContext.SaveChangesAsync(token);
    }

    private List<string> ChunkText(string text, int maxChunkLength)
    {
        var chunks = new List<string>();
        // Split cleanly by the \n characters found in your JSON
        var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = new StringBuilder();

        foreach (var p in paragraphs)
        {
            // If adding this paragraph pushes us over the limit, save current chunk and start a new one
            if (currentChunk.Length + p.Length > maxChunkLength && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString().Trim());
                currentChunk.Clear();
            }
            currentChunk.AppendLine(p.Trim());
        }

        // Add whatever is left over
        if (currentChunk.Length > 0)
        {
            chunks.Add(currentChunk.ToString().Trim());
        }

        return chunks;
    }

    private async Task ConsolidateChatHistoryAsync(CancellationToken token)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KlaiDbContext>();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();
        var embeddingService = kernel.GetRequiredService<ITextEmbeddingGenerationService>("embedding");
        var qdrantClient = scope.ServiceProvider.GetRequiredService<QdrantClient>();

        // 1. Find conversations older than 24 hours that haven't been vectorized
        var cutoffTime = DateTime.UtcNow.AddHours(-24);
        //var cutoffTime = DateTime.UtcNow;

        // Note: You will need to add the `IsVectorized` boolean to your ChatMessageEntity
        var unprocessedMessages = await dbContext.ChatMessages
            .Where(m => !m.IsVectorized && m.Timestamp < cutoffTime)
            .OrderBy(m => m.Timestamp)
            .ToListAsync(token);

        if (!unprocessedMessages.Any()) return;

        // Group by TopicId so we summarize distinct conversations separately
        var groupedMessages = unprocessedMessages.GroupBy(m => m.TopicId);

        foreach (var group in groupedMessages)
        {
            var rawChatLog = new StringBuilder();
            foreach (var msg in group)
            {
                rawChatLog.AppendLine($"{msg.Role}: {msg.Content}");
            }

            // 2. Ask the LLM to extract the facts
            string prompt = $@"Extract the key decisions, personal facts, and finalized plans from this chat log. 
                            Ignore pleasantries and dead-end troubleshooting. Be concise.
                            CHAT LOG:
                            {rawChatLog}";

            // --- ADD THESE TWO LINES ---
            // Tell the Kernel explicitly to use the "fast" model for this task
            var arguments = new KernelArguments(new PromptExecutionSettings { ServiceId = "fast" });

            var summaryResult = await kernel.InvokePromptAsync(prompt, arguments);
            string summary = summaryResult.ToString();

            // 3. Vectorize the summary
            var embedding = await embeddingService.GenerateEmbeddingAsync(summary, cancellationToken: token);

            // 4. Push to Qdrant
            var pointId = Guid.NewGuid();
            var point = new PointStruct
            {
                Id = new PointId { Uuid = pointId.ToString() },
                Vectors = embedding.ToArray(),
                Payload =
                {
                    ["content"] = summary,
                    ["source"] = "chat_summary",
                    ["topic_id"] = group.Key.ToString()
                }
            };

            await qdrantClient.UpsertAsync(_collectionName, new[] { point }, cancellationToken: token);
            foreach (var msg in group) { msg.IsVectorized = true; }
            await dbContext.SaveChangesAsync(token);

            _logger.LogInformation($"Vectorized chat summary for Topic {group.Key}");
        }
    }

    private async Task ConsolidateClosedNotionItemsAsync(CancellationToken token)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KlaiDbContext>();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();
        var embeddingService = kernel.GetRequiredService<ITextEmbeddingGenerationService>("embedding");
        var qdrantClient = scope.ServiceProvider.GetRequiredService<QdrantClient>();

        // Assuming you registered your Notion cache as a Singleton
        // Ask for the worker we actually registered
        var notionSyncWorker = scope.ServiceProvider.GetRequiredService<NotionSyncWorker>();

        // Grab the cache from its public property, just like the Telegram bot does!
        var notionCache = notionSyncWorker.CurrentState;

        // 1. Load the Local Ledger (Get all IDs we have already processed)
        var processedIds = await dbContext.VectorizedNotionItems
            .Select(v => v.NotionId)
            .ToHashSetAsync(token); // HashSet for fast O(1) lookups

        // 2. Extract closed items from your Notion state
        var itemsToProcess = new List<(string Id, string Type, string FormattedContent)>();
        var closedStatuses = new[] { "Done", "Completed", "Archived", "Failed" };

        foreach (var goal in notionCache.GetAllGoals())
        {
            // Check Goals
            if (closedStatuses.Contains(goal.Status) && !processedIds.Contains(goal.Id))
            {
                // Goals already have dates in your original code, which is great!
                itemsToProcess.Add((goal.Id, "Goal", $"Archived Goal: {goal.Name}\nStatus: {goal.Status}\nStart: {goal.StartDate:yyyy-MM-dd}\nEnd: {goal.EndDate:yyyy-MM-dd}"));
            }

            if (goal.Projects == null) continue;

            foreach (var proj in goal.Projects)
            {
                // Check Projects
                if (closedStatuses.Contains(proj.Status) && !processedIds.Contains(proj.Id))
                {
                    // Safely format project dates
                    string projDates = (proj.Start != default || proj.End != default)
                        ? $"\nStart: {proj.Start:yyyy-MM-dd}\nEnd: {proj.End:yyyy-MM-dd}"
                        : "";

                    itemsToProcess.Add((proj.Id, "Project", $"Archived Project: {proj.Name}\nParent Goal: {goal.Name}\nStatus: {proj.Status}{projDates}"));
                }

                if (proj.Tasks == null) continue;

                foreach (var task in proj.Tasks)
                {
                    // Check Tasks 
                    if (task.IsCompleted && !processedIds.Contains(task.Id))
                    {
                        // Safely format task target date (assuming it's a nullable DateTime?)
                        string taskDate = task.Date.HasValue
                            ? $"\nDue Date: {task.Date.Value:yyyy-MM-dd}"
                            : "";

                        itemsToProcess.Add((task.Id, "Task", $"Completed Task: {task.Name}\nParent Project: {proj.Name}{taskDate}\nCompleted On: {DateTime.UtcNow:yyyy-MM-dd}"));
                    }
                }
            }
        }

        foreach (var task in notionCache.FloatingTasks)
        {
            if (task.IsCompleted && !processedIds.Contains(task.Id))
            {
                string taskDate = task.Date.HasValue
                    ? $"\nDue Date: {task.Date.Value:yyyy-MM-dd}"
                    : "";

                itemsToProcess.Add((task.Id, "FloatingTask", $"Completed Task: {task.Name}{taskDate}\nCompleted On: {DateTime.UtcNow:yyyy-MM-dd}"));
            }
        }

        if (!itemsToProcess.Any()) return;

        _logger.LogInformation($"Found {itemsToProcess.Count} new closed Notion items to vectorize.");

        // 3. Vectorize, Push to Qdrant, and Write to the Ledger
        foreach (var item in itemsToProcess)
        {
            // Vectorize the formatted string
            var embedding = await embeddingService.GenerateEmbeddingAsync(item.FormattedContent, cancellationToken: token);

            // Push to Qdrant
            var pointId = Guid.NewGuid();
            var point = new PointStruct
            {
                Id = new PointId { Uuid = pointId.ToString() },
                Vectors = embedding.ToArray(),
                Payload =
                {
                    ["content"] = item.FormattedContent,
                    ["source"] = "notion_archive",
                    ["item_type"] = item.Type,
                    ["notion_id"] = item.Id
                }
            };

            await qdrantClient.UpsertAsync(_collectionName, new[] { point }, cancellationToken: token);

            // Write the receipt to the local SQLite ledger
            dbContext.VectorizedNotionItems.Add(new VectorizedNotionItem
            {
                NotionId = item.Id,
                ItemType = item.Type,
                ProcessedAt = DateTime.UtcNow
            });

            // Save immediately so a crash doesn't cause duplicates on the next run
            await dbContext.SaveChangesAsync(token);

            _logger.LogInformation($"Successfully archived Notion {item.Type}: {item.Id}");
        }
    }

    private async Task EnsureCollectionExistsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var qdrantClient = scope.ServiceProvider.GetRequiredService<QdrantClient>();

        var collections = await qdrantClient.ListCollectionsAsync();
        if (!collections.Contains(_collectionName))
        {
            // CHANGE SIZE FROM 1536 TO 3072
            await qdrantClient.CreateCollectionAsync(
                _collectionName,
                new VectorParams { Size = 3072, Distance = Distance.Cosine }
            );
        }
    }
}