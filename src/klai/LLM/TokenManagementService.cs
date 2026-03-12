using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;
using System.Linq;

namespace klai.LLM;

public class TokenManagementService
{
    // Thread-safe cache to hold tokenizers for any model we encounter
    private readonly ConcurrentDictionary<string, Tokenizer> _tokenizers = new();
    private readonly ILogger<TokenManagementService> _logger;

    public TokenManagementService(ILogger<TokenManagementService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Lazily loads the tokenizer for a specific model and caches it for future use.
    /// </summary>
    private Tokenizer GetTokenizer(string modelName)
    {
        return _tokenizers.GetOrAdd(modelName, name => 
        {
            _logger.LogInformation("Initializing Tiktoken tokenizer for model: {ModelName}", name);
            
            try 
            {
                return TiktokenTokenizer.CreateForModel(name);
            }
            catch 
            {
                _logger.LogWarning("Tokenizer for '{ModelName}' not found. Falling back to default gpt-4o encoding.", name);
                return TiktokenTokenizer.CreateForModel("gpt-4o");
            }
        });
    }

    public int CountTokens(string text, string modelName)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return GetTokenizer(modelName).CountTokens(text);
    }

    public string TruncateToTokenLimit(string text, int maxTokens, string modelName)
    {
        if (string.IsNullOrEmpty(text) || maxTokens <= 0) return string.Empty;

        var tokenizer = GetTokenizer(modelName);
        var tokenCount = tokenizer.CountTokens(text);
        
        if (tokenCount <= maxTokens) return text;

        _logger.LogWarning("Context for {ModelName} exceeded budget ({Actual} > {Budget}). Truncating...", modelName, tokenCount, maxTokens);

        var encoded = tokenizer.EncodeToIds(text);
        var truncatedIds = encoded.Take(maxTokens).ToArray();
        
        return tokenizer.Decode(truncatedIds);
    }
}