using System.ComponentModel;
using System.Net.Http;

namespace LTAI.Agent.Tools;

/// <summary>
/// 文件下载工具。需要用户确认后才下载。
/// AI 调用时需传 confirm=true 才会执行。
/// </summary>
public static class FileDownloadTool
{
    [Description("从 URL 下载文件到本地工作目录，需要用户确认才能执行。")]
    public static async Task<string> DownloadFile(
        [Description("文件下载地址")] string url,
        [Description("保存路径（相对于工作目录）")] string savePath,
        [Description("用户确认标记，必须为 true 才执行下载")] bool confirm = false)
    {
        if (!confirm)
            return "⚠️ 需要下载文件，但尚未确认。请用户确认后重新调用，设置 confirm=true。\n"
                 + $"URL: {url}\n保存到: {savePath}";

        // ⚠️ SSRF 防护
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            IsPrivateHost(uri.Host))
            return "Error: 不支持的 URL";

        var ws = Directory.GetCurrentDirectory();
        var fp = Path.GetFullPath(Path.Combine(ws, savePath));

        if (!fp.StartsWith(Path.GetFullPath(ws), StringComparison.OrdinalIgnoreCase))
            return "Error: 路径逃逸";

        try
        {
            var dir = Path.GetDirectoryName(fp);
            if (dir != null) Directory.CreateDirectory(dir);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? -1;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(fp);

            var buffer = new byte[81920];
            long readBytes = 0;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                readBytes += bytesRead;
            }

            var size = totalBytes > 0
                ? $"{totalBytes / 1024.0 / 1024.0:F1}MB"
                : $"{readBytes / 1024.0:F1}KB";
            return $"✅ 已下载: {Path.GetFileName(fp)} ({size})";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>SSRF 防护：检查是否是私有/内网地址。</summary>
    private static bool IsPrivateHost(string host)
    {
        if (host.Equals("127.0.0.1") || host.Equals("localhost") ||
            host.Equals("::1") || host.Equals("[::1]"))
            return true;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                if (b[0] == 10) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 127) return true;
            }
        }
        return false;
    }
}
