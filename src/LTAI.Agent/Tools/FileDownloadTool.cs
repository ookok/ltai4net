using System.ComponentModel;
using System.Net.Http;
using LTAI.AI;
using LTAI.Core;

namespace LTAI.Agent.Tools;

/// <summary>
/// 文件下载工具。下载前由 MAF ToolApprovalAgent 审批。
/// </summary>
[ToolDomain("file")]
public static class FileDownloadTool
{
    // 共享 HttpClient — 复用连接池，避免 socket 耗尽
    private static readonly Lazy<HttpClient> _sharedHttp = new(() => new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(10) });

    [Description("从 URL 下载文件到本地工作目录。支持大文件下载，自动显示进度。含 SSRF 防护。\n"
        + "适用场景：下载网络上的文件、获取图片/压缩包/安装包、下载数据文件。\n"
        + "不适用场景：读取网页内容（请用 WebFetch）、搜索网页（请用 WebSearch）、下载内网地址的文件（被 SSRF 防护阻止）。\n"
        + "关键参数：url — 文件下载地址；savePath — 保存路径（相对于工作目录）。")]
    [ToolExample("下载这个文件到本地")]
    [ToolExample("从网上下载一个图片")]
    [ToolExample("下载最新的安装包")]
    public static async Task<string> DownloadFile(
        [Description("文件下载地址")] string url,
        [Description("保存路径（相对于工作目录）")] string savePath,
        CancellationToken ct = default)
    {
        // Use a linked CTS so download is cancellable
        using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dlCts.CancelAfter(TimeSpan.FromMinutes(10));
        // ⚠️ SSRF 防护
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            IsPrivateHost(uri.Host))
            return "Error: 不支持的 URL";

        // DNS rebinding 防护：解析到 IP 后二次验证
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false);
            if (addresses.Any(addr => IsPrivateIP(addr)))
                return "Error: 目标地址解析到内网 IP，已阻止下载";
        }
        catch
        {
            return "Error: DNS 解析失败";
        }

        var ws = Directory.GetCurrentDirectory();
        var fp = PathUtils.SafeResolvePath(ws, savePath);
        if (fp == null) return "Error: 路径逃逸";

        try
        {
            var dir = Path.GetDirectoryName(fp);
            if (dir != null) Directory.CreateDirectory(dir);

            using var resp = await _sharedHttp.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, dlCts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // Reject oversized downloads early based on Content-Length header
            var totalBytes = resp.Content.Headers.ContentLength ?? -1;
            const long maxDownloadBytes = 500L * 1024 * 1024; // 500 MB
            if (totalBytes > maxDownloadBytes)
                return $"Error: File too large ({totalBytes / 1024.0 / 1024.0:F1}MB, max {maxDownloadBytes / 1024 / 1024}MB)";

            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var fileStream = File.Create(fp);

            var buffer = new byte[81920];
            long readBytes = 0;
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, dlCts.Token).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
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
        if (string.IsNullOrEmpty(host)) return true;
        if (host.Equals("127.0.0.1") || host.Equals("localhost") ||
            host.Equals("::1") || host.Equals("[::1]") ||
            host.Equals("0.0.0.0") || host.Equals("[::]"))
            return true;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4) // IPv4
            {
                if (b[0] == 10) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 127) return true;
                if (b[0] == 169 && b[1] == 254) return true; // link-local
                if (b[0] == 0) return true; // 0.0.0.0/8
            }
            else if (b.Length == 16) // IPv6
            {
                // IPv4-mapped IPv6 (::ffff:10.x.x.x)
                if (b[10] == 0xff && b[11] == 0xff)
                {
                    if (b[12] == 10) return true;
                    if (b[12] == 172 && b[13] >= 16 && b[13] <= 31) return true;
                    if (b[12] == 192 && b[13] == 168) return true;
                    if (b[12] == 127) return true;
                    if (b[12] == 169 && b[13] == 254) return true;
                    if (b[12] == 0) return true;
                }
                // Unique local address (fc00::/7)
                if ((b[0] & 0xfe) == 0xfc) return true;
                // Link-local (fe80::/10)
                if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;
                // Loopback (::1) already checked above
            }
        }
        return false;
    }

    /// <summary>DNS rebinding 防护：检查 IP 是否属于私有地址段。</summary>
    private static bool IsPrivateIP(System.Net.IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4) // IPv4
        {
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 127) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            if (b[0] == 0) return true;
        }
        else if (b.Length == 16) // IPv6
        {
            if (b[10] == 0xff && b[11] == 0xff)
            {
                if (b[12] == 10) return true;
                if (b[12] == 172 && b[13] >= 16 && b[13] <= 31) return true;
                if (b[12] == 192 && b[13] == 168) return true;
                if (b[12] == 127) return true;
                if (b[12] == 169 && b[13] == 254) return true;
                if (b[12] == 0) return true;
            }
            if ((b[0] & 0xfe) == 0xfc) return true;       // ULA
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true; // link-local
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0
             && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0
             && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0
             && b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] == 1) return true; // ::1
        }
        return false;
    }
}
