using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.SemanticKernel;

namespace klai.KnowledgeBase;

public class LocalDocumentPlugin
{
    [KernelFunction("ReadLocalDocument")]
    [Description("Reads the text content of a local Microsoft Word (.docx) file. Use this when the Knowledge Base lists a LocalDocument URI that you need to reference.")]
    public async Task<string> ReadLocalDocumentAsync(
        [Description("The exact URI (local file path) provided in the Knowledge Base list")] string uri)
    {
        try
        {
            if (!File.Exists(uri))
            {
                return $"Error: The file at {uri} could not be found. It may have been deleted.";
            }

            // We use Task.Run because OpenXML is synchronous, and we don't want to block the bot's thread
            return await Task.Run(() =>
            {
                using WordprocessingDocument wordDoc = WordprocessingDocument.Open(uri, false);
                var body = wordDoc.MainDocumentPart?.Document.Body;

                if (body == null) return "The document appears to be empty.";

                var sb = new StringBuilder();
                
                // Iterate through paragraphs to maintain basic line breaks
                foreach (var paragraph in body.Elements<Paragraph>())
                {
                    sb.AppendLine(paragraph.InnerText);
                }

                string extractedText = sb.ToString().Trim();
                
                // Optional safeguard: Truncate if the document is massively huge to save tokens
                // (e.g., limit to 50,000 characters which is ~12,500 tokens)
                if (extractedText.Length > 50000)
                {
                    extractedText = extractedText.Substring(0, 50000) + "\n\n[TRUNCATED due to length]";
                }

                return extractedText;
            });
        }
        catch (Exception ex)
        {
            return $"Failed to read document at {uri}. Error: {ex.Message}";
        }
    }
}