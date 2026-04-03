using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public class DriveDownloader
{
    private readonly HttpClient _httpClient;

    public DriveDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> DownloadToDiskAsync(string sharedLink, string targetDirectory)
    {
        // 1. Extract the File ID from the standard Google Drive share link
        var match = Regex.Match(sharedLink, @"/d/([a-zA-Z0-9_-]+)");
        if (!match.Success) 
            throw new ArgumentException("Invalid Google Drive link format. Ensure it contains '/d/FILE_ID'.");
        
        string fileId = match.Groups[1].Value;
        
        // 2. Construct the direct download URL
        string downloadUrl = $"https://drive.google.com/uc?export=download&id={fileId}";

        // 3. Send the request. 
        // IMPORTANT: ResponseHeadersRead prevents the HttpClient from buffering the whole file into memory!
        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        // 4. Determine the extension (defaulting to .m4a since we are targeting Zoom audio)
        string extension = ".m4a"; 
        if (response.Content.Headers.ContentType?.MediaType?.Contains("mp4") == true)
        {
            extension = ".mp4";
        }

        // 5. Create the full target file path
        string fileName = $"downloaded_media_{Guid.NewGuid()}{extension}";
        string filePath = Path.Combine(targetDirectory, fileName);

        // 6. Stream the content directly to the disk
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
        
        await contentStream.CopyToAsync(fileStream);

        // Return the path so FFmpeg knows where to find it
        return filePath;
    }
}