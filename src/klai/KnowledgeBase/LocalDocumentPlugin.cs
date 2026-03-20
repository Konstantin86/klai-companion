#pragma warning disable CS0618 // Suppress the MEAI obsolete warning

using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using DocumentFormat.OpenXml.Presentation;
using Microsoft.SemanticKernel.ChatCompletion;

namespace klai.KnowledgeBase;

public class LocalDocumentPlugin
{
    private readonly IServiceProvider _serviceProvider;

    public LocalDocumentPlugin(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    [KernelFunction("ReadLocalDocument")]
    [Description("Reads a local file. If the file is extremely large, you MUST provide a searchQuery to find specific information.")]
    public async Task<string> ReadLocalDocumentAsync(
        [Description("The exact URI (local file path) provided in the Knowledge Base list")] string uri,
        [Description("Optional. If the document is massive, provide a specific question or topic to search for within the document.")] string? searchQuery = null)
    {
        try
        {
            if (!File.Exists(uri)) return $"Error: The file at {uri} could not be found.";

            bool isPdf = uri.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            bool isDocx = uri.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
            bool isPptx = uri.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase);
            bool isImage = uri.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               uri.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               uri.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

            string extractedText = await Task.Run(async () =>
            {
                var sb = new StringBuilder();

                if (isPdf)
                {
                    using PdfDocument document = PdfDocument.Open(uri);
                    foreach (var page in document.GetPages())
                    {
                        var text = ContentOrderTextExtractor.GetText(page);
                        sb.AppendLine(text);
                        sb.AppendLine();
                    }
                }
                else if (isDocx)
                {
                    using WordprocessingDocument wordDoc = WordprocessingDocument.Open(uri, false);
                    var body = wordDoc.MainDocumentPart?.Document.Body;
                    if (body != null)
                    {
                        foreach (var paragraph in body.Elements<Paragraph>())
                        {
                            sb.AppendLine(paragraph.InnerText);
                        }
                    }
                }
                else if (isPptx)
                {
                    // --- PPTX PARSING LOGIC (WITH EMBEDDED IMAGES) ---
                    using PresentationDocument pptDoc = PresentationDocument.Open(uri, false);
                    PresentationPart? presentationPart = pptDoc.PresentationPart;

                    if (presentationPart?.Presentation?.SlideIdList != null)
                    {
                        // Resolve the Vision AI service once for the whole presentation
                        using var scope = _serviceProvider.CreateScope();
                        var chatCompletion = scope.ServiceProvider.GetRequiredService<Kernel>().GetRequiredService<IChatCompletionService>("fast");

                        int slideIndex = 1;

                        foreach (SlideId slideId in presentationPart.Presentation.SlideIdList.Elements<SlideId>())
                        {
                            if (slideId.RelationshipId == null) continue;

                            SlidePart slidePart = (SlidePart)presentationPart.GetPartById(slideId.RelationshipId);
                            sb.AppendLine($"[Slide {slideIndex}]");

                            // 1. EXTRACT STANDARD TEXT
                            var texts = slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>();
                            foreach (var text in texts)
                            {
                                sb.Append(text.Text + " ");
                            }
                            sb.AppendLine();

                            // 2. EXTRACT AND PROCESS IMAGES
                            int imageIndex = 1;
                            foreach (ImagePart imagePart in slidePart.ImageParts)
                            {
                                using Stream imageStream = imagePart.GetStream();

                                // Filter out tiny images (logos, icons, bullet points) to save API calls
                                // 15,000 bytes is a safe threshold for a meaningless icon.
                                if (imageStream.Length < 15000) continue;

                                using MemoryStream ms = new MemoryStream();
                                await imageStream.CopyToAsync(ms);
                                byte[] imageBytes = ms.ToArray();

                                // OpenXML content types are usually "image/png", "image/jpeg", etc.
                                string mimeType = imagePart.ContentType;

                                try
                                {
                                    var chatHistory = new ChatHistory();
                                    chatHistory.Add(new ChatMessageContent(AuthorRole.User, new ChatMessageContentItemCollection
                        {
                            new TextContent("Extract any text from this presentation slide image. If it is a chart, diagram, or architectural drawing, describe its contents and flow in detail."),
                            new ImageContent(imageBytes, mimeType)
                        }));

                                    var response = await chatCompletion.GetChatMessageContentAsync(chatHistory);

                                    sb.AppendLine($"\n  [Embedded Image {imageIndex} Description]:");
                                    sb.AppendLine($"  {response.Content}");
                                    imageIndex++;
                                }
                                catch (Exception ex)
                                {
                                    sb.AppendLine($"  [Failed to process image {imageIndex}: {ex.Message}]");
                                }
                            }

                            sb.AppendLine("\n");
                            slideIndex++;
                        }
                    }
                }
                else if (isImage)
                {
                    // --- VISION AI PARSING LOGIC ---
                    // 1. Resolve the fast model to do the heavy lifting
                    using var scope = _serviceProvider.CreateScope();
                    var chatCompletion = scope.ServiceProvider.GetRequiredService<Kernel>().GetRequiredService<IChatCompletionService>("fast");

                    // 2. Load the image from disk
                    byte[] imageBytes = await File.ReadAllBytesAsync(uri);
                    string mimeType = uri.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

                    // 3. Build a multimodal prompt asking the AI to act as an OCR scanner
                    var chatHistory = new ChatHistory();
                    chatHistory.Add(new ChatMessageContent(AuthorRole.User, new ChatMessageContentItemCollection
                    {
                        new TextContent("Please extract all readable text from this image exactly as written. If there are diagrams, charts, UI elements, or visual concepts, describe them in detail so I can understand the context purely through text."),
                        new ImageContent(imageBytes, mimeType)
                    }));

                    // 4. Get the text description and append it!
                    var response = await chatCompletion.GetChatMessageContentAsync(chatHistory);
                    sb.AppendLine("[IMAGE CONTENT EXTRACTED VIA AI VISION]");
                    sb.AppendLine(response.Content);
                }
                else
                {
                    string rawText = await File.ReadAllTextAsync(uri);
                    sb.Append(rawText);
                }

                return sb.ToString().Trim();
            });

            if (string.IsNullOrWhiteSpace(extractedText)) return "The document appears to be empty.";

            // --- THE JIT RAG DECISION LOGIC ---

            // Case 1: Small file. Just return the whole thing.
            if (extractedText.Length < 40000 && string.IsNullOrWhiteSpace(searchQuery))
            {
                return extractedText;
            }

            // Case 2: Massive file, but the LLM forgot to ask a specific question.
            if (extractedText.Length >= 40000 && string.IsNullOrWhiteSpace(searchQuery))
            {
                return $"SYSTEM WARNING: This document is too large ({extractedText.Length} characters) to read at once. You MUST call this tool again and provide a specific 'searchQuery' parameter to find the relevant information.";
            }

            // Case 3: We have a massive file AND a search query. Let's do Ephemeral RAG!
            return await PerformEphemeralRagAsync(extractedText, searchQuery!);
        }
        catch (Exception ex)
        {
            return $"Failed to read document at {uri}. Error: {ex.Message}";
        }
    }

    private async Task<string> PerformEphemeralRagAsync(string fullText, string searchQuery)
    {
        using var scope = _serviceProvider.CreateScope();
        var kernel = scope.ServiceProvider.GetRequiredService<Kernel>();
        var embeddingService = kernel.GetRequiredService<ITextEmbeddingGenerationService>("embedding");
        // 1. Chunk the text (Simple 2000 character chunks)
        var chunks = ChunkText(fullText, 2000, 400); // 400 char overlap for better context retention

        // 2. Generate embeddings for all chunks in one batch API call
        var chunkEmbeddings = await embeddingService.GenerateEmbeddingsAsync(chunks);

        // 3. Generate embedding for the user's search query
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(searchQuery);

        // 4. Calculate Cosine Similarity in-memory
        var scoredChunks = new List<(string Chunk, float Score)>();
        for (int i = 0; i < chunks.Count; i++)
        {
            float score = CosineSimilarity(queryEmbedding.Span, chunkEmbeddings[i].Span);
            scoredChunks.Add((chunks[i], score));
        }

        // 5. Take the top 5 most relevant chunks
        var topChunks = scoredChunks
            .OrderByDescending(x => x.Score)
            .Take(5)
            .Select(x => x.Chunk)
            .ToList();

        return $"[EPHEMERAL SEARCH RESULTS FOR: '{searchQuery}']\n\n" + string.Join("\n\n---\n\n", topChunks);
    }

    private List<string> ChunkText(string text, int chunkSize = 2000, int overlap = 400)
    {
        var chunks = new List<string>();
        int position = 0;

        while (position < text.Length)
        {
            int currentChunkSize = Math.Min(chunkSize, text.Length - position);
            chunks.Add(text.Substring(position, currentChunkSize));

            // Move the pointer forward, but step back by the overlap amount
            position += (chunkSize - overlap);
        }

        return chunks;
    }

    // A blazing fast, manual math function to compare two vectors in memory
    private float CosineSimilarity(ReadOnlySpan<float> vector1, ReadOnlySpan<float> vector2)
    {
        float dotProduct = 0;
        float magnitude1 = 0;
        float magnitude2 = 0;

        for (int i = 0; i < vector1.Length; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            magnitude1 += vector1[i] * vector1[i];
            magnitude2 += vector2[i] * vector2[i];
        }

        if (magnitude1 == 0 || magnitude2 == 0) return 0;
        return (float)(dotProduct / (Math.Sqrt(magnitude1) * Math.Sqrt(magnitude2)));
    }
}