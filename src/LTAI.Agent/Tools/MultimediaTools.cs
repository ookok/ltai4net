using System.ComponentModel;
using System.Text;
using SkiaSharp;

namespace LTAI.Agent.Tools;

public sealed class MultimediaTools
{
    private readonly string _ws;
    public MultimediaTools(string ws) => _ws = ws;

    [Description("Get image information: dimensions, format, file size")]
    public async Task<string> ImageInfo([Description("Path to image file")] string path)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";
        if (!File.Exists(fp)) return "Error: File not found";

        try
        {
            var fi = new FileInfo(fp);
            using var stream = File.OpenRead(fp);
            using var codec = SKCodec.Create(stream);
            if (codec == null) return "Unsupported image format";

            var info = codec.Info;
            var sb = new StringBuilder();
            sb.AppendLine("## Image: " + Path.GetFileName(fp));
            sb.AppendLine();
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|----------|-------|");
            sb.AppendLine($"| Dimensions | {info.Width} x {info.Height} px |");
            sb.AppendLine($"| Aspect Ratio | {(double)info.Width / info.Height:F3} |");
            sb.AppendLine($"| Color Type | {info.ColorType} |");
            sb.AppendLine($"| Format | {codec.EncodedFormat} |");
            sb.AppendLine($"| File Size | {FormatSize(fi.Length)} |");
            return sb.ToString();
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("Resize an image to specified dimensions")]
    public async Task<string> ImageResize(
        [Description("Path to image file")] string path,
        [Description("Width in pixels")] int width,
        [Description("Height in pixels")] int height,
        [Description("Output path")] string? output = null)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";
        if (!File.Exists(fp)) return "Error: File not found";

        var outPath = output != null ? ResolvePath(output) : fp;
        if (outPath == null) return "Error: Output path escape";

        width = Math.Clamp(width, 1, 10000);
        height = Math.Clamp(height, 1, 10000);

        try
        {
            using var input = File.OpenRead(fp);
            using var original = SKBitmap.Decode(input);
            if (original == null) return "Cannot decode image";

            using var resized = original.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized == null) return "Resize failed";

            var fmt = Path.GetExtension(outPath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
                ".png" => SKEncodedImageFormat.Png,
                ".webp" => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Png,
            };
            using var data = resized.Encode(fmt, 90);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            await File.WriteAllBytesAsync(outPath, data.ToArray());
            return $"Resized to {width}x{height} ({FormatSize(data.Size)})";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("Convert image between formats (PNG, JPEG, WebP, BMP)")]
    public async Task<string> ImageConvert(
        [Description("Source path")] string path,
        [Description("Target: png, jpg, webp, bmp")] string format)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";
        if (!File.Exists(fp)) return "Error: File not found";

        var ext = format.TrimStart('.').ToLowerInvariant();
        if (ext is not ("jpg" or "jpeg" or "png" or "webp" or "bmp"))
            return "Unsupported format: " + format;

        var outPath = Path.ChangeExtension(fp, ext);

        try
        {
            using var input = File.OpenRead(fp);
            using var bitmap = SKBitmap.Decode(input);
            if (bitmap == null) return "Cannot decode image";

            var skFmt = ext switch
            {
                "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
                "png" => SKEncodedImageFormat.Png,
                "webp" => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Bmp,
            };
            using var data = bitmap.Encode(skFmt, 90);
            await File.WriteAllBytesAsync(outPath, data.ToArray());
            return $"Converted to {ext} ({FormatSize(data.Size)})";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("Get audio/video file information using FFprobe")]
    public async Task<string> MediaInfo([Description("Path to media file")] string path)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";
        if (!File.Exists(fp)) return "Error: File not found";

        try
        {
            var fi = new FileInfo(fp);
            var (code, stdout, stderr) = await RunProcessAsync("ffprobe",
                $"-v quiet -print_format json -show_format -show_streams \"{fp}\"");
            if (code != 0) return "FFprobe not available. Install FFmpeg.\n" + stderr;

            using var doc = System.Text.Json.JsonDocument.Parse(stdout);
            var fmt = doc.RootElement.GetProperty("format");
            var sb = new StringBuilder();
            sb.AppendLine("## " + fi.Name);
            sb.AppendLine($"| Format | {fmt.GetProperty("format_name")} |");
            sb.AppendLine($"| Duration | {double.Parse(fmt.GetProperty("duration").GetString()!):F1}s |");
            sb.AppendLine($"| Bitrate | {int.Parse(fmt.GetProperty("bit_rate").GetString()!) / 1000} kbps |");
            sb.AppendLine($"| Size | {FormatSize(fi.Length)} |");

            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.GetProperty("codec_type").GetString();
                    var codec = s.GetProperty("codec_name").GetString();
                    if (type == "video")
                        sb.AppendLine($"| Video | {codec}, {s.GetProperty("width")}x{s.GetProperty("height")} |");
                    else if (type == "audio")
                        sb.AppendLine($"| Audio | {codec}, {s.GetProperty("sample_rate")}Hz |");
                }
            }
            return sb.ToString();
        }
        catch (Exception ex) { return "Error: " + ex.Message + ". Install FFmpeg."; }
    }

    [Description("Convert audio file format using FFmpeg")]
    public async Task<string> AudioConvert(
        [Description("Source path")] string path,
        [Description("Target: mp3, wav, ogg, flac, aac")] string format)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";
        if (!File.Exists(fp)) return "Error: File not found";

        var ext = format.TrimStart('.').ToLowerInvariant();
        if (ext is not ("mp3" or "wav" or "ogg" or "flac" or "aac"))
            return "Unsupported: " + format;

        var outPath = Path.ChangeExtension(fp, ext);
        try
        {
            var (code, _, stderr) = await RunProcessAsync("ffmpeg", $"-i \"{fp}\" -y -vn \"{outPath}\"", 120);
            if (code != 0) return "FFmpeg failed:\n" + stderr;

            var fi = new FileInfo(outPath);
            return $"Converted to {ext} ({FormatSize(fi.Length)})";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("Capture a screenshot")]
    public async Task<string> Screenshot([Description("Output path")] string? output = null)
    {
        var outPath = output != null ? ResolvePath(output) : Path.Combine(_ws, "screenshot.png");
        if (outPath == null) return "Error: Path escape";

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var script = "Add-Type -AssemblyName System.Windows.Forms; " +
                    "$s=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds; " +
                    "$b=New-Object System.Drawing.Bitmap $s.Width,$s.Height; " +
                    "$g=[System.Drawing.Graphics]::FromImage($b); " +
                    "$g.CopyFromScreen($s.Left,$s.Top,0,0,$s.Size); " +
                    $"$b.Save('{outPath.Replace("'", "''")}'); $g.Dispose(); $b.Dispose()";

                var (code, _, _) = await RunProcessAsync("powershell",
                    $"-NoProfile -Command \"{script}\"");
                if (code != 0) return "Screenshot failed";
            }
            else
            {
                var (c1, _, _) = await RunProcessAsync("import", $"-window root \"{outPath}\"", 10);
                if (c1 != 0)
                {
                    var (c2, _, _) = await RunProcessAsync("scrot", $"\"{outPath}\"", 10);
                    if (c2 != 0) return "Screenshot failed. Install ImageMagick or scrot.";
                }
            }

            var fi = new FileInfo(outPath);
            return fi.Exists
                ? $"Screenshot saved ({FormatSize(fi.Length)})"
                : "Screenshot failed";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    private string? ResolvePath(string path)
    {
        var fp = Path.GetFullPath(Path.Combine(_ws, path));
        return fp.StartsWith(_ws, StringComparison.OrdinalIgnoreCase) ? fp : null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string file, string args, int timeoutSec = 30)
    {
        using var proc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        var ok = proc.WaitForExit(timeoutSec * 1000);
        if (!ok) { proc.Kill(entireProcessTree: true); return (-1, "", "Timed out"); }
        return (proc.ExitCode, stdout, stderr);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        < 1073741824 => $"{bytes / 1048576.0:F1} MB",
        _ => $"{bytes / 1073741824.0:F2} GB"
    };
}
