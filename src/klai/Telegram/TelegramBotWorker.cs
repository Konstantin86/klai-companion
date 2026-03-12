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

namespace klai.Telegram;

public class TelegramBotWorker : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramBotWorker> _logger;
    private readonly NotionSyncWorker _notionCache;
    private readonly Kernel _kernel;
    private readonly IConfiguration _config;
    private readonly long _allowedGroupId;



    public TelegramBotWorker(IConfiguration configuration, ILogger<TelegramBotWorker> logger, NotionSyncWorker notionCache, Kernel kernel)
    {
        _logger = logger;
        _notionCache = notionCache;
        _kernel = kernel;
        _config = configuration;

        // Grab the token from your .env file
        var token = configuration["TELEGRAM_BOT_TOKEN"]
            ?? throw new ArgumentNullException("TELEGRAM_BOT_TOKEN is missing.");

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

        // UX Polish: Show "typing..." indicator in Telegram while the AI thinks
        await botClient.SendChatAction(message.Chat.Id, ChatAction.Typing, messageThreadId: topicId, cancellationToken: cancellationToken);

        string responseText;

        try
        {
            if (topicId == null)
            {
                // [MVP] Implement generic Q&A flow (with no topic)
                responseText = await HandleGenericQaAsync(messageText);
            }
            else
            {
                // [MVP] Implement happy path of goal-oriented topic flow & initial routing
                responseText = await HandleGoalOrientedFlowAsync(messageText, topicId.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing AI request");
            responseText = "Sorry, I encountered an internal error while processing your request.";
        }

        //        await botClient.SendMessage(chatId: message.Chat.Id, messageThreadId: topicId, text: responseText, cancellationToken: cancellationToken);

        // ... (existing try/catch block where responseText is generated)

        // Split the response into Telegram-safe chunks
        var messageChunks = ChunkMessage(responseText);

        // Send the AI's response back to the exact topic in order
        foreach (var chunk in messageChunks)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                messageThreadId: topicId,
                text: chunk,
                cancellationToken: cancellationToken
            );

            // Add a tiny delay between chunks to guarantee Telegram displays them in the correct order
            await Task.Delay(100, cancellationToken);
        }



    }

    private List<string> ChunkMessage(string text, int maxLength = 4000)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;

        // Use 4000 instead of 4096 to leave a small buffer
        int startIndex = 0;
        while (startIndex < text.Length)
        {
            int length = Math.Min(maxLength, text.Length - startIndex);

            // If we are not at the very end of the text, try to find a clean break (newline)
            if (startIndex + length < text.Length)
            {
                int lastNewline = text.LastIndexOf('\n', startIndex + length, length);
                if (lastNewline > startIndex) // We found a newline in this chunk!
                {
                    length = lastNewline - startIndex;
                }
                // If no newline is found, it will just hard-cut at 4000 characters
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
        if (activeContext == null) { return $"Unmapped Topic ID ({topicId}). Please add this ID to your `appconfig.json` under `TopicMappings` to link it to a Notion Value."; }

        string systemPrompt = activeContext.SystemPrompt + $"\nToday's date is {DateTime.UtcNow:yyyy-MM-dd}.";

        var routingTriggers = _config.GetSection("AiAgentConfig:RoutingTriggers").Get<string[]>() ?? ["/plan", "/deep"];
        bool requiresDeepReasoning = routingTriggers.Any(trigger => messageText.StartsWith(trigger, StringComparison.OrdinalIgnoreCase));

        string serviceId = requiresDeepReasoning ? "advanced" : "fast";
        _logger.LogInformation("Routing to Goal Flow | Context: {ValueName} | Model: {Model}", activeContext.Name, serviceId);

        // Strip the trigger word (e.g., "/plan") so the LLM doesn't get confused reading it
        if (requiresDeepReasoning)
        {
            var triggerWord = routingTriggers.First(t => messageText.StartsWith(t, StringComparison.OrdinalIgnoreCase));
            messageText = messageText.Substring(triggerWord.Length).Trim();
        }

        var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>(serviceId);
        var chatHistory = new ChatHistory(systemPrompt);

        // NOTE: In Phase 4, we will load the SQLite history and Notion JSON context here.
        // For this baseline test, we just pass the raw message.
        chatHistory.AddUserMessage(messageText);

        var response = await chatCompletion.GetChatMessageContentAsync(chatHistory);
        return response.Content ?? "No response generated.";
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram Bot encountered an error.");
        return Task.CompletedTask;
    }
}