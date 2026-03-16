using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using klai.Notion;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.DependencyInjection;
using klai.Chat.Model;
using klai.Data;
using klai.LLM;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.EntityFrameworkCore;
using klai.Sql.Model;

namespace klai.Telegram;

public class TelegramBotWorker : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramBotWorker> _logger;
    private readonly NotionSyncWorker _notionCache;
    private readonly Kernel _kernel;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TokenManagementService _tokenManager;
    private readonly long _allowedGroupId;



    public TelegramBotWorker(IConfiguration configuration, ILogger<TelegramBotWorker> logger, NotionSyncWorker notionCache, Kernel kernel, IServiceScopeFactory scopeFactory, TokenManagementService tokenManager)
    {
        _logger = logger;
        _notionCache = notionCache;
        _kernel = kernel;
        _config = configuration;
        _scopeFactory = scopeFactory;
        _tokenManager = tokenManager;

        // Grab the token from your .env file
        var token = configuration["TELEGRAM_BOT_TOKEN"] ?? throw new ArgumentNullException("TELEGRAM_BOT_TOKEN is missing.");

        _botClient = new TelegramBotClient(token);

        // Security: Prevent the bot from answering if someone adds it to a random group
        long.TryParse(configuration["ALLOWED_TELEGRAM_GROUP_ID"], out _allowedGroupId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions { AllowedUpdates = [UpdateType.Message] };
        _logger.LogInformation("Starting Telegram Bot listener...");
        _botClient.StartReceiving(updateHandler: HandleUpdateAsync, errorHandler: HandleErrorAsync, receiverOptions: receiverOptions, cancellationToken: stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message) return;

        string messageText = message.Text ?? message.Caption ?? string.Empty;

        if (string.IsNullOrWhiteSpace(messageText) && message.Document == null) return;

        if (_allowedGroupId != 0 && message.Chat.Id != _allowedGroupId)
        {
            _logger.LogWarning("Unauthorized access attempt from Chat ID: {ChatId}", message.Chat.Id);
            return;
        }

        int? topicId = message.MessageThreadId;

        // 1. Check for Knowledge Base Uploads First
        if (topicId != null)
        {
            bool wasKbUpload = await HandleKnowledgeUploadAsync(botClient, message, topicId.Value, cancellationToken);
            if (wasKbUpload) return; // Stop processing, the KB handler took care of it
        }

        string finalUserPrompt = messageText;

        // --- INTERCEPT /PLAN COMMAND ---
        var planTrigger = _config.GetValue<string>("ProjectPlanTrigger", "/plan");
        bool isPlanCommand = messageText.StartsWith(planTrigger, StringComparison.OrdinalIgnoreCase);

        if (isPlanCommand)
        {
            string planContent = messageText.Substring(planTrigger.Length).Trim();
            finalUserPrompt = $@"[SYSTEM DIRECTIVE: The user invoked the project planning mode. 
                            You MUST evaluate this request and immediately use the 'NotionPlanner-CreateProjectWithTasks' tool to build this project and its timeline in Notion. 
                            Do not just list the steps in chat; you MUST call the tool and pass the JSON array.]
                            
                            User Request: {planContent}";
        }

        // --- EPHEMERAL DOCUMENT HANDLING ---
        if (message.Document != null)
        {
            var fileName = message.Document.FileName ?? "unknown_file";
            bool isDocx = fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
            bool isPdf = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

            string tempDirectory = Path.Combine("data", "temp");
            Directory.CreateDirectory(tempDirectory);

            string uniqueFileName = $"{Guid.NewGuid().ToString().Substring(0, 8)}_{fileName}";
            string tempFilePath = Path.Combine(tempDirectory, uniqueFileName);

            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.ReadWrite))
            {
                var fileInfo = await botClient.GetFile(message.Document.FileId, cancellationToken);
                await botClient.DownloadFile(fileInfo.FilePath, fileStream, cancellationToken);

                if (!isDocx && !isPdf && !IsTextStream(fileStream))
                {
                    fileStream.Close();
                    File.Delete(tempFilePath);
                    await botClient.SendMessage(message.Chat.Id, "⚠️ I cannot process binary files for queries.", messageThreadId: topicId, cancellationToken: cancellationToken);
                    return;
                }
            }

            finalUserPrompt += $"\n\n[SYSTEM NOTE: The user attached a temporary file for this request. You MUST use the ReadLocalDocument tool on the following URI to read its contents before answering: {tempFilePath}]";
            
            await botClient.SendMessage(message.Chat.Id, $"⏳ Reading `{fileName}`...", messageThreadId: topicId, cancellationToken: cancellationToken);
        }

        // --- EPHEMERAL GOOGLE SHEET HANDLING ---
        var sheetRegex = new System.Text.RegularExpressions.Regex(@"(https:\/\/docs\.google\.com\/spreadsheets\/d\/[a-zA-Z0-9-_]+)(?:.*?gid=(\d+))?\S*");
        var match = sheetRegex.Match(messageText);

        if (match.Success)
        {
            string baseUrl = match.Groups[1].Value;
            string gid = match.Groups[2].Success ? match.Groups[2].Value : "0";
            string cleanUri = $"{baseUrl}?gid={gid}";

            finalUserPrompt += $"\n\n[SYSTEM NOTE: The user included a Google Sheet link in their request. You MUST use the ReadGoogleSheet tool on the following URI to read its contents before answering: {cleanUri}]";
        }

        _logger.LogInformation("Received: '{Text}' | Topic ID: {Topic}", messageText, topicId?.ToString() ?? "General");

        await botClient.SendChatAction(message.Chat.Id, ChatAction.Typing, messageThreadId: topicId, cancellationToken: cancellationToken);

        // Save the RAW text to memory so we don't pollute the DB with system directives
        await SaveMessageAsync(topicId, "User", messageText);

        string responseText;

        try
        {
            if (topicId == null)
            {
                responseText = await HandleGenericQaAsync(finalUserPrompt);
            }
            else
            {
                responseText = await HandleGoalOrientedFlowAsync(finalUserPrompt, topicId.Value);
            }

            await SaveMessageAsync(topicId, "Assistant", responseText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing AI request");
            responseText = "Sorry, I encountered an internal error while processing your request.";
        }

        var messageChunks = ChunkMessage(responseText);

        foreach (var chunk in messageChunks)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                messageThreadId: topicId,
                text: chunk,
                cancellationToken: cancellationToken
            );

            await Task.Delay(100, cancellationToken);
        }
    }

    private List<string> ChunkMessage(string text, int maxLength = 4000)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;

        int startIndex = 0;
        while (startIndex < text.Length)
        {
            int length = Math.Min(maxLength, text.Length - startIndex);

            if (startIndex + length < text.Length)
            {
                int lastNewline = text.LastIndexOf('\n', startIndex + length, length);
                if (lastNewline > startIndex)
                {
                    length = lastNewline - startIndex;
                }
            }

            chunks.Add(text.Substring(startIndex, length).Trim());
            startIndex += length;
        }

        return chunks;
    }

    private async Task<string> HandleGenericQaAsync(string messageText)
    {
        _logger.LogInformation("Routing to Generic Q&A (Fast Model)");
        var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>("fast");
        var chatHistory = new ChatHistory("You are Kostiantyn's helpful AI assistant. Answer queries concisely.");
        chatHistory.AddUserMessage(messageText);
        var response = await chatCompletion.GetChatMessageContentAsync(chatHistory);
        return response.Content ?? "No response generated.";
    }

    private async Task<bool> HandleKnowledgeUploadAsync(ITelegramBotClient botClient, Message message, int topicId, CancellationToken cancellationToken)
    {
        var trigger = _config.GetValue<string>("KnowledgeUploadTrigger", "/kb");

        // Telegram puts text in 'Caption' if a file is attached, otherwise it's in 'Text'
        string messageText = message.Text ?? message.Caption ?? string.Empty;

        if (!messageText.StartsWith(trigger, StringComparison.OrdinalIgnoreCase))
            return false; // Not a KB upload request, let the normal LLM flow handle it

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Data.KlaiDbContext>();

        // Strip the trigger word to isolate the user's description
        string description = messageText.Substring(trigger.Length).Trim();

        // --- SCENARIO 1: Document Upload (.docx, .pdf, or ANY Text File) ---
        if (message.Document != null)
        {
            var fileName = message.Document.FileName ?? "unknown_file";
            bool isDocx = fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
            bool isPdf = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

            // 1. Download the file from Telegram's servers
            var fileInfo = await botClient.GetFile(message.Document.FileId, cancellationToken);
            string saveDirectory = Path.Combine("data", "files");
            Directory.CreateDirectory(saveDirectory); 

            string uniqueFileName = $"{Guid.NewGuid().ToString().Substring(0, 8)}_{fileName}";
            string localFilePath = Path.Combine(saveDirectory, uniqueFileName);

            using (var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.ReadWrite))
            {
                await botClient.DownloadFile(fileInfo.FilePath, fileStream, cancellationToken);
                
                // 2. The Magic Check: If it's not a known rich format, check if it's plain text
                if (!isDocx && !isPdf)
                {
                    if (!IsTextStream(fileStream))
                    {
                        // It's an image, executable, or unknown binary. Clean it up and abort.
                        fileStream.Close();
                        File.Delete(localFilePath);
                        
                        await botClient.SendMessage(message.Chat.Id, $"⚠️ `{fileName}` appears to be a binary file. I can only process PDFs, Word docs, and plain text files (code, json, txt, etc).", messageThreadId: topicId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                        return true;
                    }
                }
            } // stream closes here

            // 3. Save the artifact record to SQLite
            dbContext.KnowledgeArtifacts.Add(new KnowledgeArtifact
            {
                TopicId = topicId,
                ArtifactType = "LocalDocument", // Still keeping it unified!
                Uri = localFilePath,
                Description = string.IsNullOrWhiteSpace(description) ? fileName : description,
                AddedAt = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            await botClient.SendMessage(message.Chat.Id, $"✅ Saved `{fileName}` to the Knowledge Base.", messageThreadId: topicId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            return true;
        }

        // --- SCENARIO 2: Google Spreadsheet Link ---
        // Group 1 grabs the Base URL. Group 2 optionally grabs the GID if you pasted a link to a specific tab.
        var sheetRegex = new System.Text.RegularExpressions.Regex(@"(https:\/\/docs\.google\.com\/spreadsheets\/d\/[a-zA-Z0-9-_]+)(?:.*?gid=(\d+))?\S*");
        var match = sheetRegex.Match(messageText);

        if (match.Success)
        {
            string baseUrl = match.Groups[1].Value;
            // If the URL had a gid, grab it. Otherwise, default to "0" (the first tab).
            string gid = match.Groups[2].Success ? match.Groups[2].Value : "0";
            string fullUrl = match.Value;

            description = description.Replace(fullUrl, "").Trim();

            dbContext.KnowledgeArtifacts.Add(new KnowledgeArtifact
            {
                TopicId = topicId,
                ArtifactType = "GoogleSheet",
                Uri = $"{baseUrl}?gid={gid}", // We save it in a clean format the plugin can easily parse
                Description = string.IsNullOrWhiteSpace(description) ? "Google Spreadsheet" : description,
                AddedAt = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            await botClient.SendMessage(message.Chat.Id, $"✅ Linked Google Sheet (Tab ID: {gid}) to the Knowledge Base.", messageThreadId: topicId, cancellationToken: cancellationToken);
            return true;
        }

        // --- SCENARIO 3: Error / Invalid Input ---
        await botClient.SendMessage(message.Chat.Id, "⚠️ I saw the `/kb` tag, but couldn't find a `.docx` file attachment or a valid Google Sheets link.", messageThreadId: topicId, cancellationToken: cancellationToken);
        return true;
    }

    private async Task<string> HandleGoalOrientedFlowAsync(string messageText, int topicId)
    {
        var activeContext = _notionCache.CurrentState.GetActiveContextForTopic(topicId, _config);
        if (activeContext == null) { return $"Unmapped Topic ID ({topicId}). Please link it in `appconfig.json`."; }

        var routingTriggers = _config.GetSection("AiAgentConfig:RoutingTriggers").Get<string[]>() ?? ["/plan", "/deep"];
        bool requiresDeepReasoning = routingTriggers.Any(t => messageText.StartsWith(t, StringComparison.OrdinalIgnoreCase));

        string serviceId = requiresDeepReasoning ? "advanced" : "fast";
        string modelName = _config[$"AiAgentConfig:Models:{(requiresDeepReasoning ? "Advanced" : "Fast")}"]!;

        if (requiresDeepReasoning)
        {
            var triggerWord = routingTriggers.First(t => messageText.StartsWith(t, StringComparison.OrdinalIgnoreCase));
            messageText = messageText.Substring(triggerWord.Length).Trim();
        }

        // --- 1. LOAD CONTEXT & ENFORCE TOKEN BUDGET ---
        int maxNotionTokens = _config.GetValue<int>("AiAgentConfig:TokenBudgets:MaxNotionContext", 2000);
        string rawNotionText = activeContext.ToTokenOptimizedString();
        string safeNotionText = _tokenManager.TruncateToTokenLimit(rawNotionText, maxNotionTokens, modelName);
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Data.KlaiDbContext>();

        var artifacts = await dbContext.KnowledgeArtifacts.Where(a => a.TopicId == topicId).OrderBy(a => a.AddedAt).ToListAsync();

        var kbBuilder = new System.Text.StringBuilder();
        if (artifacts.Any())
        {
            kbBuilder.AppendLine("AVAILABLE KNOWLEDGE BASE ARTIFACTS:");
            kbBuilder.AppendLine("To read these artifacts, you MUST use the corresponding tool and pass the exact URI provided. Do not guess their contents.");
            kbBuilder.AppendLine();

            foreach (var artifact in artifacts)
            {
                kbBuilder.AppendLine($"[Type: {artifact.ArtifactType}] - URI: {artifact.Uri}");
                kbBuilder.AppendLine($"Description: {artifact.Description}");

                if (artifact.ArtifactType == "LocalDocument")
                {
                    kbBuilder.AppendLine("-> Use Tool: LocalDocument-ReadLocalDocument");
                }
                else if (artifact.ArtifactType == "GoogleSheet")
                {
                    kbBuilder.AppendLine("-> Use Tool: GoogleSheets-ReadGoogleSheet");
                }
                kbBuilder.AppendLine();
            }
        }
        string dynamicKbContext = kbBuilder.ToString();

        var userProfile = string.Join("\n", _config.GetSection("AiAgentConfig:SystemPrompt:UserProfile").Get<string[]>() ?? Array.Empty<string>());
        var criticalInstructions = string.Join("\n", _config.GetSection("AiAgentConfig:SystemPrompt:CriticalInstructions").Get<string[]>() ?? Array.Empty<string>());
        var rulesOfEngagement = string.Join("\n", _config.GetSection("AiAgentConfig:SystemPrompt:RulesOfEngagement").Get<string[]>() ?? Array.Empty<string>());

        string systemPrompt = $@"
                {activeContext.Value.SystemPrompt}
                Today's date is {DateTime.UtcNow:yyyy-MM-dd}.

                USER PROFILE:
                {userProfile}

                CURRENT STATE (ACTIVE GOALS & PROJECTS):
                {safeNotionText}

                {dynamicKbContext}

                CRITICAL INSTRUCTIONS: 
                {criticalInstructions}
                
                RULES OF ENGAGEMENT (AVAILABLE TOOLS & MISSING DATA):
                {rulesOfEngagement}";

        // --- TEMPORARY DEBUG DUMP ---
        try
        {
            string formattedPrompt = systemPrompt.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            await System.IO.File.WriteAllTextAsync("debug_system_prompt.txt", formattedPrompt);
        }
        catch (Exception ex) { Console.WriteLine($"Could not write debug prompt: {ex.Message}"); }
        // ----------------------------

        var executionSettings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
        };

        var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>(serviceId);
        var chatHistory = new ChatHistory(systemPrompt);

        var recentMessages = await GetRecentChatHistoryAsync(topicId, 10);

        foreach (var msg in recentMessages)
        {
            if (msg.Role == "User")
            {
                chatHistory.AddUserMessage(msg.Content);
            }
            else if (msg.Role == "Assistant")
            {
                chatHistory.AddAssistantMessage(msg.Content);
            }
        }
        chatHistory.AddUserMessage(messageText);
        var response = await chatCompletion.GetChatMessageContentAsync(chatHistory, executionSettings, _kernel);

        return response.Content ?? "No response generated.";
    }

    private async Task<List<ChatMessageEntity>> GetRecentChatHistoryAsync(int topicId, int limit = 10)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Data.KlaiDbContext>();

        var messages = await dbContext.ChatMessages
            .Where(m => m.TopicId == topicId)
            .OrderByDescending(m => m.Timestamp) // Get newest first
            .Take(limit)
            .ToListAsync();

        messages.Reverse();
        return messages;
    }

    private async Task SaveMessageAsync(int? topicId, string role, string content)
    {
        // Create a scoped lifetime for the database transaction
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KlaiDbContext>();

        if (!System.IO.Directory.Exists("data"))
        {
            System.IO.Directory.CreateDirectory("data");
        }

        // For the MVP: Automatically create the SQLite file/tables if they don't exist
        await dbContext.Database.EnsureCreatedAsync();

        dbContext.ChatMessages.Add(new ChatMessageEntity
        {
            TopicId = topicId,
            Role = role,
            Content = content,
            Timestamp = DateTime.UtcNow
        });

        await dbContext.SaveChangesAsync();
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram Bot encountered an error.");
        return Task.CompletedTask;
    }

    private bool IsTextStream(Stream stream)
    {
        // If the stream is empty, it's technically not a binary, but there's no text either
        if (stream.Length == 0) return false;

        long originalPosition = stream.Position;
        stream.Position = 0;

        // Read up to the first 512 bytes
        byte[] buffer = new byte[512];
        int bytesRead = stream.Read(buffer, 0, buffer.Length);

        // Reset the stream so Telegram or File streams can still use it!
        stream.Position = originalPosition;

        for (int i = 0; i < bytesRead; i++)
        {
            if (buffer[i] == 0x00) // A null byte is the universal sign of a binary file
            {
                return false;
            }
        }
        return true;
    }
}