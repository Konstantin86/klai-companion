using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using klai.Notion;

namespace klai.Telegram;

public class TelegramBotWorker : BackgroundService
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<TelegramBotWorker> _logger;
    private readonly NotionSyncWorker _notionCache;
    private readonly long _allowedGroupId;

    public TelegramBotWorker(IConfiguration configuration, ILogger<TelegramBotWorker> logger, NotionSyncWorker notionCache)
    {
        _logger = logger;
        _notionCache = notionCache;
        
        // Grab the token from your .env file
        var token = configuration["TELEGRAM_BOT_TOKEN"] 
            ?? throw new ArgumentNullException("TELEGRAM_BOT_TOKEN is missing.");
        
        _botClient = new TelegramBotClient(token);

        // Security: Prevent the bot from answering if someone adds it to a random group
        long.TryParse(configuration["ALLOWED_TELEGRAM_GROUP_ID"], out _allowedGroupId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            // We only need to process messages for this MVP
            AllowedUpdates = [UpdateType.Message] 
        };

        _logger.LogInformation("Starting Telegram Bot listener...");

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken
        );

        // Keeps the background service running
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        // Only process text messages
        if (update.Message is not { Text: { } messageText } message)
            return;

        // Security check: Ignore messages outside your personal group
        if (_allowedGroupId != 0 && message.Chat.Id != _allowedGroupId)
        {
            _logger.LogWarning("Unauthorized access attempt from Chat ID: {ChatId}", message.Chat.Id);
            return;
        }

        // --- THE MAGIC PROPERTY FOR TOPICS ---
        // In Telegram Forums, the Topic ID is stored in MessageThreadId.
        // If it's null, the message was sent in the default "General" topic.
        int? topicId = message.MessageThreadId;

        _logger.LogInformation(
            "Received: '{Text}' | Group: {Group} | Topic ID: {Topic}",
            messageText, message.Chat.Id, topicId?.ToString() ?? "General");

        // TODO: Map topicId to your Life Areas
        // Example: 
        // if (topicId == 12) -> Route to Semantic Kernel with "Finance" persona
        // if (topicId == 15) -> Route to Semantic Kernel with "YouTube" persona for The Automated Engineer

        // Echoing back for testing. 
        // NOTE: Passing messageThreadId ensures the bot replies inside the correct topic!
        await botClient.SendMessage(
            chatId: message.Chat.Id,
            messageThreadId: topicId, 
            text: $"Acknowledged in Topic {topicId}. You said: {messageText}",
            cancellationToken: cancellationToken
        );
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram Bot encountered an error.");
        return Task.CompletedTask;
    }
}