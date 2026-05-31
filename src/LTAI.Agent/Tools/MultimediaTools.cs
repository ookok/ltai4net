using System.ComponentModel;
using System.Text;
using LTAI.AI;
using SkiaSharp;

namespace LTAI.Agent.Tools;

[ToolDomain("multimedia")]
public sealed class MultimediaTools
{
    private readonly string _ws;
    public MultimediaTools(string ws) => _ws = ws;

    [Description("获取图片信息：尺寸、格式、文件大小、色彩类型。支持 JPG/PNG/WebP/BMP 等格式。\n"
        + "适用场景：查看图片分辨率、确认图片格式、检查文件大小。\n"
        + "不适用场景：修改图片（请用 ImageResize/ImageConvert）、获取音频/视频信息（请用 MediaInfo）。\n"
        + "关键参数：path — 图片文件路径。")]
    [ToolExample("看看这张图片的分辨率")]
    [ToolExample("这个图片是什么格式的")]
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

    [Description("调整图片尺寸到指定宽高像素。保持或改变宽高比。\n"
        + "适用场景：压缩大图片、生成缩略图、统一图片尺寸。\n"
        + "不适用场景：转换图片格式（请用 ImageConvert）、裁剪图片（请用其他工具）。\n"
        + "关键参数：path — 图片路径；width/height — 目标宽高像素；output — 输出路径。")]
    [ToolExample("把这个图片缩小到 800x600")]
    [ToolExample("生成一个缩略图")]
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
            await File.WriteAllBytesAsync(outPath, data.ToArray()).ConfigureAwait(false);
            return $"Resized to {width}x{height} ({FormatSize(data.Size)})";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("转换图片格式。支持 PNG/JPEG/WebP/BMP 之间的互转。\n"
        + "适用场景：将 PNG 转为 JPG 减小体积、转换 WebP 为通用格式。\n"
        + "不适用场景：调整图片尺寸（请用 ImageResize）、获取图片信息（请用 ImageInfo）。\n"
        + "关键参数：path — 源图片路径；format — 目标格式(png/jpg/webp/bmp)。")]
    [ToolExample("把这个 png 转成 jpg")]
    [ToolExample("转换为 WebP 格式")]
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
            await File.WriteAllBytesAsync(outPath, data.ToArray()).ConfigureAwait(false);
            return $"Converted to {ext} ({FormatSize(data.Size)})";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("获取音视频文件信息：格式、时长、码率、编码、分辨率。需要 FFprobe。\n"
        + "适用场景：查看视频分辨率、检查音频采样率、确认媒体文件格式和时长。\n"
        + "不适用场景：转换音频格式（请用 AudioConvert）、获取图片信息（请用 ImageInfo）。\n"
        + "关键参数：path — 媒体文件路径。")]
    [ToolExample("这个视频的分辨率是多少")]
    [ToolExample("看看这个音频文件的格式")]
    public async Task<string> MediaInfo([Description("Path to media file")] string path)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";
        if (!File.Exists(fp)) return "Error: File not found";

        try
        {
            var fi = new FileInfo(fp);
            var (code, stdout, stderr) = await RunProcessAsync("ffprobe",
                $"-v quiet -print_format json -show_format -show_streams \"{fp}\"").ConfigureAwait(false);
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

    [Description("转换音频文件格式。支持 MP3/WAV/OGG/FLAC/AAC。需要 FFmpeg。\n"
        + "适用场景：将音频转为 MP3 格式、转为 WAV 便于编辑。\n"
        + "不适用场景：获取音频信息（请用 MediaInfo）、处理图片（请用 ImageConvert/ImageResize）。\n"
        + "关键参数：path — 源音频路径；format — 目标格式(mp3/wav/ogg/flac/aac)。")]
    [ToolExample("把这个音频转成 mp3")]
    [ToolExample("转换为 WAV 格式")]
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
            var (code, _, stderr) = await RunProcessAsync("ffmpeg", $"-i \"{fp}\" -y -vn \"{outPath}\"", 120).ConfigureAwait(false);
            if (code != 0) return "FFmpeg failed:\n" + stderr;

            var fi = new FileInfo(outPath);
            return $"Converted to {ext} ({FormatSize(fi.Length)})";
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    [Description("截取屏幕截图。支持 Windows(powershell) 和 Linux(import/scrot)。\n"
        + "适用场景：获取当前屏幕内容、保存错误界面截图、分享桌面状态。\n"
        + "不适用场景：录制屏幕视频、截取特定窗口（仅支持全屏截图）。\n"
        + "关键参数：output — 输出路径，默认 screenshot.png。")]
    [ToolExample("截个屏")]
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
                    $"-NoProfile -Command \"{script}\"").ConfigureAwait(false);
                if (code != 0) return "Screenshot failed";
            }
            else
            {
                var (c1, _, _) = await RunProcessAsync("import", $"-window root \"{outPath}\"", 10).ConfigureAwait(false);
                if (c1 != 0)
                {
                    var (c2, _, _) = await RunProcessAsync("scrot", $"\"{outPath}\"", 10).ConfigureAwait(false);
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

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_ws, path);

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
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
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
