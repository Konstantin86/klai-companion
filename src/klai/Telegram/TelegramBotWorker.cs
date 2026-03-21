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
using System.Collections.Concurrent;
using FFMpegCore;
using Microsoft.SemanticKernel.AudioToText;
using Microsoft.SemanticKernel.TextToAudio;

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
    private readonly ConcurrentDictionary<string, List<Message>> _mediaGroupCache = new();

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

    private async Task SendVoiceResponseAsync(ITelegramBotClient botClient, long chatId, int? topicId, string text, CancellationToken cancellationToken)
    {
        string tempDir = Path.Combine("data", "temp");
        Directory.CreateDirectory(tempDir);

        string mp3Path = Path.Combine(tempDir, $"{Guid.NewGuid()}.mp3");
        string oggPath = Path.Combine(tempDir, $"{Guid.NewGuid()}.ogg");

        try
        {
            // 1. Generate Audio using Azure OpenAI TTS
#pragma warning disable SKEXP0001
            var ttsService = _kernel.GetRequiredService<ITextToAudioService>("text-to-audio");
#pragma warning restore SKEXP0001

            // Generate the raw audio bytes from the LLM's text
            var audioContent = await ttsService.GetAudioContentAsync(text, null, _kernel, cancellationToken);
            await System.IO.File.WriteAllBytesAsync(mp3Path, audioContent.Data!.Value.ToArray(), cancellationToken);

            // 2. Convert MP3 to Telegram-compatible OGG Opus
            // We use CustomArgument here because FFMpegCore doesn't have a strongly-typed Opus enum
            await FFMpegArguments
                .FromFileInput(mp3Path)
                .OutputToFile(oggPath, true, options => options
                    .WithCustomArgument("-c:a libopus -b:a 32k"))
                .ProcessAsynchronously();

            // 3. Upload and send as a native Telegram Voice Message
            using var fileStream = new FileStream(oggPath, FileMode.Open, FileAccess.Read);
            var inputFile = InputFile.FromStream(fileStream, "voice.ogg");

            await botClient.SendVoice(
                chatId: chatId,
                voice: inputFile,
                messageThreadId: topicId,
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate or send TTS voice response.");
        }
        finally
        {
            // 4. Garbage Collection
            if (System.IO.File.Exists(mp3Path)) System.IO.File.Delete(mp3Path);
            if (System.IO.File.Exists(oggPath)) System.IO.File.Delete(oggPath);
        }
    }

    private async Task<string> TranscribeVoiceMessageAsync(ITelegramBotClient botClient, string fileId, CancellationToken cancellationToken)
    {
        var fileInfo = await botClient.GetFile(fileId, cancellationToken);

        string tempDir = Path.Combine("data", "temp");
        Directory.CreateDirectory(tempDir);

        string oggPath = Path.Combine(tempDir, $"{Guid.NewGuid()}.ogg");
        string mp3Path = Path.Combine(tempDir, $"{Guid.NewGuid()}.mp3"); // Changed to .mp3

        try
        {
            // 1. Download the raw OGG Opus file from Telegram
            using (var fileStream = new FileStream(oggPath, FileMode.Create, FileAccess.Write))
            {
                await botClient.DownloadFile(fileInfo.FilePath, fileStream, cancellationToken);
            }

            // 2. Convert to standard MP3 using FFmpeg
            await FFMpegArguments
                .FromFileInput(oggPath)
                .OutputToFile(mp3Path, true, options => options
                    .WithAudioCodec(FFMpegCore.Enums.AudioCodec.LibMp3Lame) // Use the available MP3 codec
                    .WithAudioSamplingRate(16000))
                .ProcessAsynchronously();

            // 3. Send to Azure OpenAI Whisper
            var audioBytes = await System.IO.File.ReadAllBytesAsync(mp3Path, cancellationToken);
#pragma warning disable SKEXP0001
            var audioContent = new AudioContent(audioBytes, "audio/mp3"); // Updated MIME type
            var sttService = _kernel.GetRequiredService<IAudioToTextService>("audio-to-text");
#pragma warning restore SKEXP0001
            var result = await sttService.GetTextContentAsync(audioContent, null, _kernel, cancellationToken);

            return result.Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transcribe voice message.");
            return "[VOICE MESSAGE UNREADABLE]";
        }
        finally
        {
            // 4. Garbage Collection
            if (System.IO.File.Exists(oggPath)) System.IO.File.Delete(oggPath);
            if (System.IO.File.Exists(mp3Path)) System.IO.File.Delete(mp3Path);
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        Message? message = null;
        string messageText = string.Empty;
        List<Message> groupedMessages = new List<Message>();

        // --- ROUTING & BOT NAME STRIPPER ---
        if (update.Type == UpdateType.Message && update.Message != null)
        {
            message = update.Message;

            // --- 1. THE MEDIA GROUP AGGREGATOR ---
            if (message.MediaGroupId != null)
            {
                var group = _mediaGroupCache.GetOrAdd(message.MediaGroupId, _ => new List<Message>());
                lock (group) { group.Add(message); }

                if (group.Count == 1)
                {
                    // We are the first file. Wait 1.5 seconds for the rest to arrive from Telegram.
                    await Task.Delay(1500, cancellationToken);
                    _mediaGroupCache.TryRemove(message.MediaGroupId, out groupedMessages);
                }
                else
                {
                    // We are file 2, 3, etc. Just add to the list and terminate this thread.
                    // The thread holding the 1st file will process us after the delay.
                    return;
                }
            }
            else
            {
                groupedMessages = new List<Message> { message };
            }

            // --- NEW: VOICE MESSAGE INTERCEPTION ---
            var messageWithVoice = groupedMessages.FirstOrDefault(m => m.Voice != null);
            if (messageWithVoice != null)
            {
                await botClient.SendChatAction(message.Chat.Id, ChatAction.Typing, messageThreadId: message.MessageThreadId, cancellationToken: cancellationToken);

                // Transcribe it!
                messageText = await TranscribeVoiceMessageAsync(botClient, messageWithVoice.Voice.FileId, cancellationToken);

                // Send the transcription back to the chat so you know what the bot "heard"
                await botClient.SendMessage(message.Chat.Id, $"🎤 _\"{messageText}\"_", messageThreadId: message.MessageThreadId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            }
            else
            {
                // 2. Extract the text (Caption is usually only on the first grouped message)
                var messageWithText = groupedMessages.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Caption ?? m.Text));
                messageText = messageWithText?.Text ?? messageWithText?.Caption ?? string.Empty;

                // 3. Strip "@klaiassist_bot"
                if (messageText.StartsWith("/"))
                {
                    int atIndex = messageText.IndexOf('@');
                    int spaceIndex = messageText.IndexOf(' ');

                    if (atIndex > 0 && (spaceIndex == -1 || atIndex < spaceIndex))
                    {
                        int endOfBotName = spaceIndex > -1 ? spaceIndex : messageText.Length;
                        messageText = messageText.Remove(atIndex, endOfBotName - atIndex);
                    }
                }
            }

        }
        else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
        {
            await botClient.AnswerCallbackQuery(update.CallbackQuery.Id, cancellationToken: cancellationToken);
            message = update.CallbackQuery.Message;
            messageText = update.CallbackQuery.Data ?? string.Empty;
            groupedMessages = new List<Message> { message! };
            if (message == null) return;
        }
        else { return; }

        // If there's no text and absolutely no files in the group, abort.
        if (string.IsNullOrWhiteSpace(messageText) && !groupedMessages.Any(m => m.Document != null || m.Photo != null)) return;

        if (_allowedGroupId != 0 && message!.Chat.Id != _allowedGroupId)
        {
            _logger.LogWarning("Unauthorized access attempt from Chat ID: {ChatId}", message.Chat.Id);
            return;
        }

        int? topicId = message.MessageThreadId;

        if (messageText.Equals("/?", StringComparison.OrdinalIgnoreCase))
        {
            string manual =
                "🤖 <b>Klai Companion Manual</b>\n\n" +
                "<code>/kb</code> - List all Knowledge Base artifacts in this topic.\n" +
                "<code>/kb</code> + <b>Upload File</b> + <b>Description</b> - Save a file to the Knowledge Base (see the list of supported files below).\n" +
                "<code>/kb</code> + <b>Google Spreadsheet Link</b> + <b>Description</b> - Save a Google Spreadsheet to the Knowledge Base.\n" +
                "<code>/kb-update &lt;Id&gt;</code> + <b>Upload File</b> - Replace an existing artifact (see the list of supported files below).\n" +
                "<code>/kb-remove &lt;Id&gt;</code> - Delete an artifact from the KB.\n" +
                "<code>/plan &lt;description&gt;</code> - Create a Notion project and timeline.\n" +
                "<code>/deep &lt;prompt&gt;</code> - Route query to the advanced reasoning model.\n\n" +
                "<b>Normal Chat:</b> Just talk to me natively. You can also upload files without commands and I will read them temporarily for that specific request.\n\n" +
                "<b>Plugins (Skills):</b>\n" +
                " - Create Notion Tasks\n" +
                " - Create Notion Projects with Tasks\n" +
                " - Link existing Tasks to Notion Projects\n" +
                " - Reschedule existing Tasks in Notion\n" +
                " - Rename existing Tasks in Notion\n" +
                " - Read files, Google Spreadsheets, Images\n" +
                " - Search in Long-Term Memory Archive (see Memory principles below)\n\n" +
                "<b>Supported Files:</b>\n" +
                " - docx, pdf, pptx, text files (txt, csv, json, xml, etc...) up to 20MB\n" +
                " - Google Spreadsheets\n" +
                " - Images (jpg/jpeg, png) up to 20MB\n" +
                " - Number of files is not limited (for inline upload or as a KB)\n\n" +
                "<b>Context:</b>\n" +
                " - Active Topic (Value) System Prompt (configurable in the appsettings.json)\n" +
                " - User profile (configurable in the appsettings.json)\n" +
                " - Active Topic Current State (Active Goals, Projects and Tasks) (Loaded from Notion each 5 minutes)\n" +
                " - Available Knowledge Base Artifacts\n" +
                " - Critical Persona Instructions (configurable in the appsettings.json)\n" +
                " - Rules of Engagement (Available Tools & How to Handle Missing Data)\n" +
                " - Chat History (10 most recent messages)\n\n" +
                "<b>Memory:</b>\n" +
                " - Short-Term (Contextual Awareness - see details in the \"Context\" section above)\n" +
                " - Long-Term (RAG. Vectorized past chat conversations, archived or completed Notion goals, projects, old tasks, notes. Long-Term Memory is Refreshed once every 24 hours)\n";

            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: manual,
                messageThreadId: topicId,
                parseMode: ParseMode.Html, // Using HTML to prevent Markdown parser crashes
                cancellationToken: cancellationToken);
            return;
        }

        // 1. Check for Knowledge Base Uploads First
        if (topicId != null)
        {
            bool wasKbUpload = await HandleKnowledgeUploadAsync(botClient, groupedMessages, messageText, topicId.Value, cancellationToken);
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
        foreach (var msg in groupedMessages)
        {
            string? fileId = null;
            string fileName = "unknown_file";
            long? fileSizeBytes = null;

            if (msg.Document != null)
            {
                fileId = msg.Document.FileId;
                fileName = msg.Document.FileName ?? "unknown_file";
                fileSizeBytes = msg.Document.FileSize;
            }
            else if (msg.Photo != null && msg.Photo.Length > 0)
            {
                var bestPhoto = msg.Photo.OrderByDescending(p => p.FileSize).First();
                fileId = bestPhoto.FileId;
                fileName = $"photo_{bestPhoto.FileUniqueId}.jpg";
                fileSizeBytes = bestPhoto.FileSize;
            }

            if (fileId != null)
            {
                if (fileSizeBytes > 20971520)
                {
                    await botClient.SendMessage(
                        msg.Chat.Id,
                        $"⚠️ `{fileName}` is too large ({fileSizeBytes / 1024 / 1024} MB). Telegram limits bot downloads to 20MB. Please compress it or use a web link.",
                        messageThreadId: topicId,
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);

                    continue; // Skip this massive file, but keep processing the smaller ones!
                }

                bool isDocx = fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
                bool isPdf = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                bool isPptx = fileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase);
                bool isImage = fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                               fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                               fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

                string tempDirectory = Path.Combine("data", "temp");
                Directory.CreateDirectory(tempDirectory);

                string uniqueFileName = $"{Guid.NewGuid().ToString().Substring(0, 8)}_{fileName}";
                string tempFilePath = Path.Combine(tempDirectory, uniqueFileName);

                using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.ReadWrite))
                {
                    var fileInfo = await botClient.GetFile(fileId, cancellationToken);
                    await botClient.DownloadFile(fileInfo.FilePath, fileStream, cancellationToken);

                    if (!isDocx && !isPdf && !isPptx && !isImage && !IsTextStream(fileStream))
                    {
                        fileStream.Close();
                        File.Delete(tempFilePath);
                        await botClient.SendMessage(message.Chat.Id, $"⚠️ `{fileName}` is a binary file. Skipping.", messageThreadId: topicId, cancellationToken: cancellationToken);
                        continue; // Skip this file, but process the others!
                    }
                }

                finalUserPrompt += $"\n\n[SYSTEM NOTE: The user attached a file. Use ReadLocalDocument on: {tempFilePath}]";
                await botClient.SendMessage(message.Chat.Id, $"⏳ Reading `{fileName}`...", messageThreadId: topicId, cancellationToken: cancellationToken);
            }
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


        bool wasVoiceMessage = groupedMessages.Any(m => m.Voice != null);

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

        // If you spoke to it, it speaks back!
        if (wasVoiceMessage)
        {
            // Change the "typing..." indicator to "recording voice..."
            await botClient.SendChatAction(message.Chat.Id, ChatAction.RecordVoice, messageThreadId: topicId, cancellationToken: cancellationToken);
            
            // Strip out markdown symbols (like **, `) before sending to TTS so it doesn't try to pronounce them
            string cleanSpeechText = responseText.Replace("*", "").Replace("`", "");
            
            await SendVoiceResponseAsync(botClient, message.Chat.Id, topicId, cleanSpeechText, cancellationToken);
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

    private async Task<bool> HandleKnowledgeUploadAsync(ITelegramBotClient botClient, List<Message> groupedMessages, string messageText, int topicId, CancellationToken cancellationToken)
    {
        var kbTrigger = _config.GetValue<string>("KnowledgeUploadTrigger", "/kb");
        var kbUpdateTrigger = "/kb-update";
        var kbRemoveTrigger = "/kb-remove";

        bool isKbUpload = messageText.StartsWith(kbTrigger, StringComparison.OrdinalIgnoreCase);
        bool isKbUpdate = messageText.StartsWith(kbUpdateTrigger, StringComparison.OrdinalIgnoreCase);
        bool isKbRemove = messageText.StartsWith(kbRemoveTrigger, StringComparison.OrdinalIgnoreCase);

        if (!isKbUpload && !isKbUpdate && !isKbRemove)
            return false; // Not a KB command, let the LLM handle it

        // Open the DB connection ONCE for the whole operation
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Data.KlaiDbContext>();

        // We only need to check the first message for singular text commands
        var firstMsg = groupedMessages.First();

        // --- SCENARIO 1: LIST ARTIFACTS (/kb with no attachments) ---
        if (messageText.Trim().Equals(kbTrigger, StringComparison.OrdinalIgnoreCase) && !groupedMessages.Any(m => m.Document != null || m.Photo != null))
        {
            var artifacts = await dbContext.KnowledgeArtifacts
                .Where(a => a.TopicId == topicId)
                .OrderBy(a => a.Id)
                .ToListAsync(cancellationToken);

            if (!artifacts.Any())
            {
                await botClient.SendMessage(firstMsg.Chat.Id, "📭 No knowledge base artifacts found for this topic.", messageThreadId: topicId, cancellationToken: cancellationToken);
                return true;
            }

            var sb = new System.Text.StringBuilder("📚 <b>Knowledge Base Artifacts:</b>\n\n");
            foreach (var a in artifacts)
            {
                string safeUri = a.Uri.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                string safeDesc = (a.Description ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
                sb.AppendLine($"<code>{a.Id}</code> - {safeUri} - <i>{safeDesc}</i>");
            }

            await botClient.SendMessage(firstMsg.Chat.Id, sb.ToString(), messageThreadId: topicId, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            return true;
        }

        // --- SCENARIO 4: REMOVE ARTIFACT (/kb-remove <Id>) ---
        if (isKbRemove)
        {
            var parts = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int artifactId))
            {
                await botClient.SendMessage(firstMsg.Chat.Id, "⚠️ Invalid format. Use `/kb-remove <Id>`.", messageThreadId: topicId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                return true;
            }

            var existingArtifact = await dbContext.KnowledgeArtifacts.FirstOrDefaultAsync(a => a.Id == artifactId && a.TopicId == topicId, cancellationToken);
            if (existingArtifact == null)
            {
                await botClient.SendMessage(firstMsg.Chat.Id, $"⚠️ Artifact with ID `{artifactId}` not found in this topic.", messageThreadId: topicId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                return true;
            }

            if (existingArtifact.ArtifactType == "LocalDocument" && System.IO.File.Exists(existingArtifact.Uri))
            {
                try { System.IO.File.Delete(existingArtifact.Uri); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete artifact file: {Uri}", existingArtifact.Uri); }
            }

            dbContext.KnowledgeArtifacts.Remove(existingArtifact);
            await dbContext.SaveChangesAsync(cancellationToken);
            await botClient.SendMessage(firstMsg.Chat.Id, $"🗑️ Successfully removed Artifact `{artifactId}`.", messageThreadId: topicId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            return true;
        }

        // --- SCENARIO 2: UPDATE EXISTING ARTIFACT (/kb-update <Id>) ---
        if (isKbUpdate)
        {
            // For updates, we just grab the first valid file from the group
            var msgToUpdate = groupedMessages.FirstOrDefault(m => m.Document != null || m.Photo != null);
            if (msgToUpdate == null)
            {
                await botClient.SendMessage(firstMsg.Chat.Id, "⚠️ Please attach a new document when using `/kb-update <Id>`.", messageThreadId: topicId, cancellationToken: cancellationToken);
                return true;
            }

            var parts = messageText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out int artifactId))
            {
                await botClient.SendMessage(firstMsg.Chat.Id, "⚠️ Invalid format. Use `/kb-update <Id>` in the caption.", messageThreadId: topicId, cancellationToken: cancellationToken);
                return true;
            }

            var existingArtifact = await dbContext.KnowledgeArtifacts.FirstOrDefaultAsync(a => a.Id == artifactId && a.TopicId == topicId, cancellationToken);
            if (existingArtifact == null || existingArtifact.ArtifactType != "LocalDocument")
            {
                await botClient.SendMessage(firstMsg.Chat.Id, $"⚠️ Artifact `{artifactId}` not found or isn't a local file.", messageThreadId: topicId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                return true;
            }

            string? fileId = null;
            string fileName = "unknown_file";
            long? fileSizeBytes = null;

            if (msgToUpdate.Document != null)
            {
                fileId = msgToUpdate.Document.FileId;
                fileName = msgToUpdate.Document.FileName ?? "unknown_file";
                fileSizeBytes = msgToUpdate.Document.FileSize;
            }
            else if (msgToUpdate.Photo != null && msgToUpdate.Photo.Length > 0)
            {
                var bestPhoto = msgToUpdate.Photo.OrderByDescending(p => p.FileSize).First();
                fileId = bestPhoto.FileId;
                fileName = $"photo_{bestPhoto.FileUniqueId}.jpg";
                fileSizeBytes = bestPhoto.FileSize;
            }

            if (fileSizeBytes > 20971520)
            {
                await botClient.SendMessage(
                    firstMsg.Chat.Id,
                    $"⚠️ `{fileName}` is too large ({fileSizeBytes / 1024 / 1024} MB). Telegram limits bot downloads to 20MB. Please compress it or use a web link.",
                    messageThreadId: topicId,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);

                return false;
            }

            var fileInfo = await botClient.GetFile(fileId!, cancellationToken);
            string saveDirectory = Path.Combine("data", "files");
            Directory.CreateDirectory(saveDirectory);

            string uniqueFileName = $"{Guid.NewGuid().ToString().Substring(0, 8)}_{fileName}";
            string newLocalFilePath = Path.Combine(saveDirectory, uniqueFileName);

            using (var fileStream = new FileStream(newLocalFilePath, FileMode.Create, FileAccess.ReadWrite))
            {
                await botClient.DownloadFile(fileInfo.FilePath, fileStream, cancellationToken);
            }

            if (System.IO.File.Exists(existingArtifact.Uri))
            {
                try { System.IO.File.Delete(existingArtifact.Uri); }
                catch { /* Ignore */ }
            }

            existingArtifact.Uri = newLocalFilePath;
            existingArtifact.AddedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            await botClient.SendMessage(firstMsg.Chat.Id, $"✅ Successfully updated Artifact `{artifactId}`. The new file is `{fileName}`.", messageThreadId: topicId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            return true;
        }

        // --- SCENARIO 3: CREATE NEW ARTIFACTS (/kb <description>) ---
        string description = messageText.Substring(kbTrigger.Length).Trim();
        bool handledSomething = false;

        // 1. Handle Google Sheets Links first
        var sheetRegex = new System.Text.RegularExpressions.Regex(@"(https:\/\/docs\.google\.com\/spreadsheets\/d\/[a-zA-Z0-9-_]+)(?:.*?gid=(\d+))?\S*");
        var match = sheetRegex.Match(messageText);

        if (match.Success)
        {
            string baseUrl = match.Groups[1].Value;
            string gid = match.Groups[2].Success ? match.Groups[2].Value : "0";
            description = description.Replace(match.Value, "").Trim();

            dbContext.KnowledgeArtifacts.Add(new KnowledgeArtifact
            {
                TopicId = topicId,
                ArtifactType = "GoogleSheet",
                Uri = $"{baseUrl}?gid={gid}",
                Description = string.IsNullOrWhiteSpace(description) ? "Google Spreadsheet" : description,
                AddedAt = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync(cancellationToken);

            await botClient.SendMessage(firstMsg.Chat.Id, $"✅ Linked Google Sheet (Tab ID: {gid}) to the Knowledge Base.", messageThreadId: topicId, cancellationToken: cancellationToken);
            handledSomething = true;
        }

        // 2. Loop through all grouped files and save them
        foreach (var msg in groupedMessages)
        {
            string? fileId = null;
            string fileName = "unknown_file";
            long? fileSizeBytes = null;

            // Extract the right file/photo ID (just like Ephemeral handler)
            if (msg.Document != null)
            {
                fileId = msg.Document.FileId;
                fileName = msg.Document.FileName ?? "unknown_file";
                fileSizeBytes = msg.Document.FileSize;
            }
            else if (msg.Photo != null && msg.Photo.Length > 0)
            {
                var bestPhoto = msg.Photo.OrderByDescending(p => p.FileSize).First();
                fileId = bestPhoto.FileId;
                fileName = $"photo_{bestPhoto.FileUniqueId}.jpg";
                fileSizeBytes = bestPhoto.FileSize;
            }


            if (fileSizeBytes > 20971520)
            {
                await botClient.SendMessage(
                    firstMsg.Chat.Id,
                    $"⚠️ `{fileName}` is too large ({fileSizeBytes / 1024 / 1024} MB). Telegram limits bot downloads to 20MB. Please compress it or use a web link.",
                    messageThreadId: topicId,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken);

                return false;
            }

            if (fileId != null)
            {
                bool isDocx = fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
                bool isPdf = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                bool isPptx = fileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase);
                bool isImage = fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                               fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                               fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

                var fileInfo = await botClient.GetFile(fileId, cancellationToken);
                string saveDirectory = Path.Combine("data", "files");
                Directory.CreateDirectory(saveDirectory);

                string uniqueFileName = $"{Guid.NewGuid().ToString().Substring(0, 8)}_{fileName}";
                string localFilePath = Path.Combine(saveDirectory, uniqueFileName);

                using (var fileStream = new FileStream(localFilePath, FileMode.Create, FileAccess.ReadWrite))
                {
                    await botClient.DownloadFile(fileInfo.FilePath, fileStream, cancellationToken);

                    if (!isDocx && !isPdf && !isPptx && !isImage && !IsTextStream(fileStream))
                    {
                        fileStream.Close();
                        File.Delete(localFilePath);
                        await botClient.SendMessage(firstMsg.Chat.Id, $"⚠️ `{fileName}` appears to be a binary file and was skipped.", messageThreadId: topicId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                        continue; // Skip this one, but process the next files!
                    }
                }

                dbContext.KnowledgeArtifacts.Add(new KnowledgeArtifact
                {
                    TopicId = topicId,
                    ArtifactType = "LocalDocument",
                    Uri = localFilePath,
                    Description = string.IsNullOrWhiteSpace(description) ? fileName : description,
                    AddedAt = DateTime.UtcNow
                });

                // Save immediately so it gets a unique ID
                await dbContext.SaveChangesAsync(cancellationToken);
                await botClient.SendMessage(firstMsg.Chat.Id, $"✅ Saved `{fileName}` to the Knowledge Base.", messageThreadId: topicId, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                handledSomething = true;
            }
        }

        if (!handledSomething)
        {
            await botClient.SendMessage(firstMsg.Chat.Id, "⚠️ I saw the `/kb` tag, but couldn't find any valid files or Google Sheets links attached.", messageThreadId: topicId, cancellationToken: cancellationToken);
        }

        // Return true ONCE at the very end
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