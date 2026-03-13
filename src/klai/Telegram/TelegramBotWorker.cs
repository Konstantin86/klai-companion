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
        if (update.Message is not { Text: { } messageText } message) return;

        if (_allowedGroupId != 0 && message.Chat.Id != _allowedGroupId)
        {
            _logger.LogWarning("Unauthorized access attempt from Chat ID: {ChatId}", message.Chat.Id);
            return;
        }

        int? topicId = message.MessageThreadId;
        _logger.LogInformation("Received: '{Text}' | Topic ID: {Topic}", messageText, topicId?.ToString() ?? "General");

        await botClient.SendChatAction(message.Chat.Id, ChatAction.Typing, messageThreadId: topicId, cancellationToken: cancellationToken);

        await SaveMessageAsync(topicId, "User", messageText);

        string responseText;

        try
        {
            if (topicId == null)
            {
                responseText = await HandleGenericQaAsync(messageText);
            }
            else
            {
                responseText = await HandleGoalOrientedFlowAsync(messageText, topicId.Value);
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

    private async Task<string> HandleGoalOrientedFlowAsync(string messageText, int topicId)
    {
        var activeContext = _notionCache.CurrentState.GetActiveContextForTopic(topicId, _config);

        if (activeContext == null)
        {
            return $"Unmapped Topic ID ({topicId}). Please link it in `appconfig.json`.";
        }

        var routingTriggers = _config.GetSection("AiAgentConfig:RoutingTriggers").Get<string[]>() ?? ["/plan", "/deep"];
        bool requiresDeepReasoning = routingTriggers.Any(t => messageText.StartsWith(t, StringComparison.OrdinalIgnoreCase));

        string serviceId = requiresDeepReasoning ? "advanced" : "fast";
        // Grab the actual model name (e.g., "gpt-4o-mini") from config to feed to the tokenizer
        string modelName = _config[$"AiAgentConfig:Models:{(requiresDeepReasoning ? "Advanced" : "Fast")}"]!;

        if (requiresDeepReasoning)
        {
            var triggerWord = routingTriggers.First(t => messageText.StartsWith(t, StringComparison.OrdinalIgnoreCase));
            messageText = messageText.Substring(triggerWord.Length).Trim();
        }

        // --- 1. LOAD CONTEXT & ENFORCE TOKEN BUDGET ---
        int maxNotionTokens = _config.GetValue<int>("AiAgentConfig:TokenBudgets:MaxNotionContext", 2000);

        // Serialize WITHOUT indentation to save hundreds of formatting tokens
        //var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
        //string rawNotionJson = System.Text.Json.JsonSerializer.Serialize(activeContext, jsonOptions);

        string rawNotionText = activeContext.ToTokenOptimizedString();

        // The Gatekeeper: Ensure the JSON fits in the budget
        string safeNotionText = _tokenManager.TruncateToTokenLimit(rawNotionText, maxNotionTokens, modelName);

        string systemPrompt = $@"
                {activeContext.SystemPrompt}
                Today's date is {DateTime.UtcNow:yyyy-MM-dd}.

                USER PROFILE:
                Name: Kostya
                Age/Physical: 35yo, 185cm, 85kg
                Family: Married, 2 kids
                Role: Delivery Manager / Solutions Architect
                Communication Style: Direct, technical, no fluff.

                CURRENT STATE (ACTIVE GOALS & PROJECTS):
                {safeNotionText}

                CRITICAL INSTRUCTIONS: 
                1. BE EXTREMELY CONCISE. Keep your answers to 1-3 short paragraphs maximum. Do not output long essays, lists, or full reports unless the user explicitly asks for a detailed breakdown. Get straight to the point.
                
                RULES OF ENGAGEMENT (AVAILABLE TOOLS & MISSING DATA):
                - PAST FACTS & NOTES: If you need a historical fact, past decision, or personal context not in the prompt, DO NOT hallucinate. Use your Long-Term Memory search tool first.
                - PROACTIVE INQUIRY: If you still lack critical numerical data (like salaries, budgets, or specific metrics) after searching your memory, STOP and ask me for it. Do not use placeholder assumptions to finish a task.
                - MY CAREER: If I ask about my resume, skills, or job history, pull my CV.
                - PROJECT TRACKING: If I ask about specific risks, milestones, or tabular data not in the active state, load the Project Spreadsheet.";

        var executionSettings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
        };

        var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>(serviceId);
        var chatHistory = new ChatHistory(systemPrompt);

        var recentMessages = await GetRecentChatHistoryAsync(topicId, 5); // Load last 5 turns

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

    private async Task<List<ChatMessageEntity>> GetRecentChatHistoryAsync(int topicId, int limit = 5)
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
}