using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

public class AzureWhisperClient
{
    private readonly HttpClient _httpClient;
    private readonly string _requestUrl;
    private readonly string _azureApiKey;

    public AzureWhisperClient(HttpClient httpClient, string azureEndpoint, string deploymentName, string apiKey)
    {
        _httpClient = httpClient;
        _azureApiKey = apiKey;
        
        // Azure explicitly requires the api-version parameter. 
        // 2024-02-01 is the current stable GA version for Azure OpenAI Whisper.
        string apiVersion = "2024-02-01";
        
        // Format: https://{your-resource}.openai.azure.com/openai/deployments/{deployment-name}/audio/transcriptions?api-version=...
        _requestUrl = $"{azureEndpoint.TrimEnd('/')}/openai/deployments/{deploymentName}/audio/transcriptions?api-version={apiVersion}";
    }

    public async Task<string> TranscribeChunkAsync(string filePath, string previousContextPrompt = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _requestUrl);
        
        // Azure relies on 'api-key', not 'Authorization: Bearer'
        request.Headers.Add("api-key", _azureApiKey);

        using var content = new MultipartFormDataContent();
        
        // Stream directly from disk to protect server RAM
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var fileContent = new StreamContent(fileStream);
        
        // Explicitly declare it as an m4a audio file for Zoom
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/m4a"); 
        
        content.Add(fileContent, "file", Path.GetFileName(filePath));
        content.Add(new StringContent("text"), "response_format");

        // Optional: Pass the end of the last chunk to help the AI keep context across slices
        if (!string.IsNullOrWhiteSpace(previousContextPrompt))
        {
            content.Add(new StringContent(previousContextPrompt), "prompt");
        }

        request.Content = content;

        var response = await _httpClient.SendAsync(request);
        
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Azure Whisper API rejected the chunk. Status: {response.StatusCode}. Details: {error}");
        }

        return await response.Content.ReadAsStringAsync();
    }
}