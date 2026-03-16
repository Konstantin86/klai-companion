using System.ComponentModel;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.SemanticKernel;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace klai.KnowledgeBase;

public class LocalDocumentPlugin
{
    [KernelFunction("ReadLocalDocument")]
    [Description("Reads the text content of a local Microsoft Word (.docx) or PDF (.pdf) file. Use this when the Knowledge Base lists a LocalDocument URI that you need to reference.")]
    public async Task<string> ReadLocalDocumentAsync(
        [Description("The exact URI (local file path) provided in the Knowledge Base list")] string uri)
    {
        try
        {
            if (!File.Exists(uri))
            {
                return $"Error: The file at {uri} could not be found. It may have been deleted.";
            }

            // Determine file type based on extension
            bool isPdf = uri.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

            return await Task.Run(() =>
            {
                var sb = new StringBuilder();

                if (isPdf)
                {
                    // --- PDF PARSING LOGIC ---
                    using PdfDocument document = PdfDocument.Open(uri);
                    foreach (var page in document.GetPages())
                    {
                        // ContentOrderTextExtractor does a great job maintaining paragraph structure
                        var text = ContentOrderTextExtractor.GetText(page);
                        sb.AppendLine(text);
                        sb.AppendLine(); // Add a blank line between pages for readability
                    }
                }
                else
                {
                    // --- DOCX PARSING LOGIC ---
                    using WordprocessingDocument wordDoc = WordprocessingDocument.Open(uri, false);
                    var body = wordDoc.MainDocumentPart?.Document.Body;

                    if (body == null) return "The document appears to be empty.";

                    foreach (var paragraph in body.Elements<Paragraph>())
                    {
                        sb.AppendLine(paragraph.InnerText);
                    }
                }

                string extractedText = sb.ToString().Trim();
                
                // Safeguard: Truncate if the document is massively huge to save tokens
                if (extractedText.Length > 50000)
                {
                    extractedText = extractedText.Substring(0, 50000) + "\n\n[TRUNCATED due to length]";
                }

                return string.IsNullOrWhiteSpace(extractedText) ? "The document appears to contain no readable text (it may be a scanned image)." : extractedText;
            });
        }
        catch (Exception ex)
        {
            return $"Failed to read document at {uri}. Error: {ex.Message}";
        }
    }
}