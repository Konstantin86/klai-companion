using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace klai.KnowledgeBase;

public class GoogleSheetsPlugin
{
    // Reuse a single HttpClient instance to prevent socket exhaustion
    private static readonly HttpClient _httpClient = new HttpClient();

    [KernelFunction("ReadGoogleSheet")]
    [Description("Reads the tabular data from a public Google Spreadsheet. Use this when the Knowledge Base lists a GoogleSheet URI that you need to reference.")]
    public async Task<string> ReadGoogleSheetAsync(
        [Description("The exact URI (Google Sheet URL) provided in the Knowledge Base list")] string uri)
    {
        try
        {
            // 1. Parse the clean URI we saved in the database
            string[] uriParts = uri.Split("?gid=");
            string baseUrl = uriParts[0].TrimEnd('/');
            string gid = uriParts.Length > 1 ? uriParts[1] : "0";

            // 2. Build the exact export URL for that specific tab
            string exportUrl = $"{baseUrl}/export?format=csv&gid={gid}";

            // 3. Fetch the raw CSV data
            var response = await _httpClient.GetAsync(exportUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                return $"Error: Could not access the Google Sheet. Make sure the sharing settings are set to 'Anyone with the link can view'. HTTP Status: {response.StatusCode}";
            }

            string csvData = await response.Content.ReadAsStringAsync();

            // 3. Safeguard: Truncate if the spreadsheet is massive (limit to ~50,000 chars / ~12,500 tokens)
            if (csvData.Length > 50000)
            {
                csvData = csvData.Substring(0, 50000) + "\n\n[TRUNCATED due to length]";
            }

            return csvData;
        }
        catch (Exception ex)
        {
            return $"Failed to read Google Sheet at {uri}. Error: {ex.Message}";
        }
    }
}