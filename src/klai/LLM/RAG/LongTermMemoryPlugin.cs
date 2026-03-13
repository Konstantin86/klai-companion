using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using Qdrant.Client;
using System.ComponentModel;
using System.Text;

namespace klai.LLM.RAG;

public class LongTermMemoryPlugin
{
    private readonly QdrantClient _qdrantClient;
    private readonly string _collectionName = "klai_long_term_memory";

    public LongTermMemoryPlugin(QdrantClient qdrantClient)
    {
        _qdrantClient = qdrantClient;
        //_embeddingService = embeddingService;
    }

    [KernelFunction("Search")]
    [Description("Searches the user's long-term memory vector database. Use this tool anytime you need to recall past chat conversations, archived or completed Notion goals, projects, tasks AND the user's personal notes, how-to guides, and affirmations or historical decisions not present in the current active state.")]

    public async Task<string> SearchAsync([Description("The topic, project, or past conversation concept to search for")] string query, Kernel kernel)
    {
        try
        {
            var embeddingService = kernel.GetRequiredService<ITextEmbeddingGenerationService>("embedding");
            // 1. Convert the AI's text query into a vector embedding
            var embedding = await embeddingService.GenerateEmbeddingAsync(query);

            // 2. Search Qdrant for the closest semantic matches
            var points = await _qdrantClient.SearchAsync(
                collectionName: _collectionName,
                vector: embedding.ToArray(),
                limit: 3 // Grab the top 3 most relevant chunks to save tokens
            );

            if (points.Count == 0) return "No relevant archived records found.";

            // 3. Format the retrieved payload into a clean string for the LLM
            var sb = new StringBuilder();
            sb.AppendLine("Found the following archived records:");
            
            foreach (var point in points)
            {
                // We will structure our background job to store the raw text in a "content" payload field
                if (point.Payload.TryGetValue("content", out var contentValue))
                {
                    sb.AppendLine($"- {contentValue.StringValue}");
                }
            }
            
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Failed to search archive: {ex.Message}";
        }
    }
}