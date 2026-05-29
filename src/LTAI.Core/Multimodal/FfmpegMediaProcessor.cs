using LTAI.Core.Governors;

namespace LTAI.Core.Multimodal;

public sealed class MediaResult
{
    public bool Ok { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Format { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string Error { get; set; } = "";
}

/// <summary>
/// FFmpeg-based media processor for audio extraction and conversion.
/// Wraps FFmpeg CLI with both MicroKernel and direct Process.Start execution paths.
/// Temp files are created in %TEMP%/ltai_media and cleaned up in finally blocks.
/// Callers: LTAI.Core.Multimodal.TtsEngine, LTAI.Web.AudioEndpoints, LTAI.Infra.
/// Thread-safe: each method creates unique temp files per call.
/// </summary>
public sealed class FfmpegMediaProcessor
{
    private readonly string? _ffmpegPath;
    private readonly IMicroKernel? _kernel;

    public FfmpegMediaProcessor(string? ffmpegPath = null, IMicroKernel? kernel = null)
    {
        _kernel = kernel;
        _ffmpegPath = ffmpegPath ?? FindFfmpeg();
    }

    public bool IsAvailable => !string.IsNullOrEmpty(_ffmpegPath) && global::System.IO.File.Exists(_ffmpegPath);

    private string? FindFfmpeg()
    {
        var names = new[] { "ffmpeg", "ffmpeg.exe" };
        foreach (var name in names)
        {
            if (_kernel != null)
            {
                try
                {
                    var kResult = _kernel.ExecuteAsync(new KernelOp
                    {
                        Command = name,
                        Arguments = "-version",
                        Timeout = TimeSpan.FromSeconds(5)
                    }).GetAwaiter().GetResult();
                    if (kResult.Success) return name;
                }
                catch { /* non-fatal */ }
            }
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
            await global::System.IO.File.WriteAllBytesAsync(inputFile, videoData).ConfigureAwait(false);
            var args = $"-i \"{inputFile}\" -vn -acodec pcm_s16le -ar {sampleRate} -ac 1 -f {outputFormat} \"{outputFile}\" -y";

            if (_kernel != null)
            {
                try
                {
                    var kResult = await _kernel.ExecuteAsync(new KernelOp
                    {
                        Command = _ffmpegPath!,
                        Arguments = args,
                        Timeout = TimeSpan.FromSeconds(120)
                    }).ConfigureAwait(false);
                    if (kResult.Success && global::System.IO.File.Exists(outputFile))
                    {
                        var audioData = await global::System.IO.File.ReadAllBytesAsync(outputFile).ConfigureAwait(false);
                        return new MediaResult
                        {
                            Ok = true, Data = audioData, Format = outputFormat,
                            DurationSeconds = audioData.Length / (double)(sampleRate * 2)
                        };
                    }
                }
                catch { /* fall through to Process.Start */ }
            }

            var psi = new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = _ffmpegPath, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };

            using var process = global::System.Diagnostics.Process.Start(psi);
            if (process == null) return new MediaResult { Error = "Could not start FFmpeg" };
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode == 0 && global::System.IO.File.Exists(outputFile))
            {
                var audioData = await global::System.IO.File.ReadAllBytesAsync(outputFile).ConfigureAwait(false);
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
            // File.Delete is idempotent on non-existent paths — pre-check is unnecessary
            try { global::System.IO.File.Delete(inputFile); } catch { /* best-effort cleanup */ }
            try { global::System.IO.File.Delete(outputFile); } catch { /* best-effort cleanup */ }
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
            await global::System.IO.File.WriteAllBytesAsync(inputFile, audioData).ConfigureAwait(false);
            var args = $"-i \"{inputFile}\" -ar 16000 -ac 1 \"{outputFile}\" -y";

            if (_kernel != null)
            {
                try
                {
                    var kResult = await _kernel.ExecuteAsync(new KernelOp
                    {
                        Command = _ffmpegPath!,
                        Arguments = args,
                        Timeout = TimeSpan.FromSeconds(120)
                    }).ConfigureAwait(false);
                    if (kResult.Success && global::System.IO.File.Exists(outputFile))
                        return new MediaResult { Ok = true, Data = await global::System.IO.File.ReadAllBytesAsync(outputFile).ConfigureAwait(false), Format = outputFormat };
                }
                catch { /* fall through to Process.Start */ }
            }

            var psi = new global::System.Diagnostics.ProcessStartInfo
            {
                FileName = _ffmpegPath, Arguments = args,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };

            using var process = global::System.Diagnostics.Process.Start(psi);
            if (process == null) return new MediaResult { Error = "Could not start FFmpeg" };
            await process.WaitForExitAsync().ConfigureAwait(false);

            if (process.ExitCode == 0 && global::System.IO.File.Exists(outputFile))
                return new MediaResult { Ok = true, Data = await global::System.IO.File.ReadAllBytesAsync(outputFile).ConfigureAwait(false), Format = outputFormat };

            return new MediaResult { Error = "FFmpeg conversion failed" };
        }
        catch (Exception ex) { return new MediaResult { Error = ex.Message }; }
        finally
        {
            try { global::System.IO.File.Delete(inputFile); } catch { /* best-effort cleanup */ }
            try { global::System.IO.File.Delete(outputFile); } catch { /* best-effort cleanup */ }
        }
    }
}
