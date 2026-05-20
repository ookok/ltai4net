namespace LTAI.Core.Multimodal;

public sealed class MediaResult
{
    public bool Ok { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Format { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string Error { get; set; } = "";
}

public sealed class FfmpegMediaProcessor
{
    private readonly string _ffmpegPath;

    public FfmpegMediaProcessor(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? FindFfmpeg();
    }

    public bool IsAvailable => !string.IsNullOrEmpty(_ffmpegPath) && global::System.IO.File.Exists(_ffmpegPath);

    private static string? FindFfmpeg()
    {
        var names = new[] { "ffmpeg", "ffmpeg.exe" };
        foreach (var name in names)
        {
            try
            {
                var startInfo = new global::System.Diagnostics.ProcessStartInfo
                {
                    FileName = name, Arguments = "-version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false, CreateNoWindow = true
                };
                using var process = global::System.Diagnostics.Process.Start(startInfo);
                if (process != null) { process.WaitForExit(5000); if (process.ExitCode == 0) return name; }
            }
            catch { /* non-fatal */ }
        }

        var commonPaths = new[]
        {
            global::System.IO.Path.Combine("tools", "ffmpeg.exe"),
            global::System.IO.Path.Combine("bin", "ffmpeg.exe"),
            "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg"
        };
        foreach (var p in commonPaths)
            if (global::System.IO.File.Exists(p)) return p;
        return "";
    }

    public async Task<MediaResult> ExtractAudioAsync(byte[] videoData, string outputFormat = "wav", int sampleRate = 16000)
    {
        if (!IsAvailable)
            return new MediaResult { Error = "FFmpeg not found" };

        var tmpDir = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "ltai_media");
        global::System.IO.Directory.CreateDirectory(tmpDir);
        var inputFile = global::System.IO.Path.Combine(tmpDir, $"input_{Guid.NewGuid():N}");
        var outputFile = global::System.IO.Path.Combine(tmpDir, $"output_{Guid.NewGuid():N}.{outputFormat}");

        try
        {
            await global::System.IO.File.WriteAllBytesAsync(inputFile, videoData);
            var args = $"-i \"{inputFile}\" -vn -acodec pcm_s16le -ar {sampleRate} -ac 1 -f {outputFormat} \"{outputFile}\" -y";

            var psi = new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = _ffmpegPath, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };

            using var process = global::System.Diagnostics.Process.Start(psi);
            if (process == null) return new MediaResult { Error = "Could not start FFmpeg" };
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && global::System.IO.File.Exists(outputFile))
            {
                var audioData = await global::System.IO.File.ReadAllBytesAsync(outputFile);
                return new MediaResult
                {
                    Ok = true, Data = audioData, Format = outputFormat,
                    DurationSeconds = audioData.Length / (double)(sampleRate * 2)
                };
            }
            return new MediaResult { Error = "FFmpeg extraction failed" };
        }
        catch (Exception ex) { return new MediaResult { Error = ex.Message }; }
        finally
        {
            try
            {
                if (global::System.IO.File.Exists(inputFile)) global::System.IO.File.Delete(inputFile);
                if (global::System.IO.File.Exists(outputFile)) global::System.IO.File.Delete(outputFile);
            }
            catch { /* non-fatal */ }
        }
    }

    public async Task<MediaResult> ConvertAudioAsync(byte[] audioData, string inputFormat, string outputFormat = "wav")
    {
        if (!IsAvailable) return new MediaResult { Error = "FFmpeg not found" };

        var tmpDir = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "ltai_media");
        global::System.IO.Directory.CreateDirectory(tmpDir);
        var inputFile = global::System.IO.Path.Combine(tmpDir, $"input_{Guid.NewGuid():N}.{inputFormat}");
        var outputFile = global::System.IO.Path.Combine(tmpDir, $"output_{Guid.NewGuid():N}.{outputFormat}");

        try
        {
            await global::System.IO.File.WriteAllBytesAsync(inputFile, audioData);
            var args = $"-i \"{inputFile}\" -ar 16000 -ac 1 \"{outputFile}\" -y";

            var psi = new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = _ffmpegPath, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };

            using var process = global::System.Diagnostics.Process.Start(psi);
            if (process == null) return new MediaResult { Error = "Could not start FFmpeg" };
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && global::System.IO.File.Exists(outputFile))
                return new MediaResult { Ok = true, Data = await global::System.IO.File.ReadAllBytesAsync(outputFile), Format = outputFormat };

            return new MediaResult { Error = "FFmpeg conversion failed" };
        }
        catch (Exception ex) { return new MediaResult { Error = ex.Message }; }
        finally
        {
            try
            {
                if (global::System.IO.File.Exists(inputFile)) global::System.IO.File.Delete(inputFile);
                if (global::System.IO.File.Exists(outputFile)) global::System.IO.File.Delete(outputFile);
            }
            catch { /* non-fatal */ }
        }
    }
}
