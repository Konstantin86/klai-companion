using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

public class MediaChunker
{
    // Splits the .m4a file into 20-minute (1200 seconds) chunks instantaneously
    public async Task<List<string>> SplitAudioAsync(string inputFilePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string outputPattern = Path.Combine(outputDirectory, "chunk_%03d.m4a");

        // -f segment: split the file
        // -segment_time 1200: split every 20 minutes (keeps it safely under 25MB)
        // -c copy: DO NOT re-encode. Just slice the binary stream (super fast)
        string arguments = $"-y -i \"{inputFilePath}\" -f segment -segment_time 1200 -c copy \"{outputPattern}\"";

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg", 
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);
        if (process == null) throw new Exception("Failed to start FFmpeg. Is it installed?");

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            string error = await process.StandardError.ReadToEndAsync();
            throw new Exception($"FFmpeg failed to split audio: {error}");
        }

        // Return the list of generated chunk files, sorted chronologically
        return Directory.GetFiles(outputDirectory, "chunk_*.m4a").OrderBy(f => f).ToList();
    }
}