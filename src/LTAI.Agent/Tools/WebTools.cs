using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Tools;

/// <summary>
/// Web search (DuckDuckGo → Brave → Serper) and fetch tools.
/// DuckDuckGo requires NO API key. Brave needs BRAVE_API_KEY. Serper needs SERPER_API_KEY.
/// </summary>
public sealed class WebTools
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WebTools> _logger;

    public WebTools(IHttpClientFactory httpFactory, ILogger<WebTools>? logger = null)
    {
        _httpFactory = httpFactory;
        _logger = logger ?? NullLogger<WebTools>.Instance;
    }

    [Description("Search the public web for current information")]
    public async Task<string> WebSearch(
        [Description("Search query")] string query,
        [Description("Number of results to return (1-10)")] int topK = 5)
    {
        topK = Math.Clamp(topK, 1, 10);

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            // Level 1: DuckDuckGo (free, no API key)
            var result = await TryDuckDuckGoAsync(http, query, topK).ConfigureAwait(false);
            if (result != null) return result;

            // Level 2: Brave (if BRAVE_API_KEY set)
            result = await TryBraveSearchAsync(http, query, topK).ConfigureAwait(false);
            if (result != null) return result;

            // Level 3: Serper/Google (if SERPER_API_KEY set — 2500 free searches/month)
            result = await TrySerperSearchAsync(http, query, topK).ConfigureAwait(false);
            if (result != null) return result;

            return "No search results available from any provider.";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WebSearch failed for query: {Query}", query);
            return $"Search failed: {ex.Message}";
        }
    }

    [Description("Fetch a web URL (http/https only) and return its text content. 不支持 file:// 等本地协议。读取本地文件请用 ReadFileContent。")]
    public async Task<string> WebFetch(
        [Description("URL to download (http/https only)")] string url,
        [Description("Maximum characters to return")] int maxChars = 50000)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"Error: Invalid URL '{url}'";

        if (uri.Scheme is not "http" and not "https")
            return "❌ WebFetch 不支持 file:// 协议。读取本地文件请使用 ReadFileContent 工具（【推荐】读取文件内容的首选工具）。不要用 WebFetch 或命令行读取文件。";

        // ⚠️ SSRF 防护：阻止内网地址
        if (IsPrivateHost(uri.Host))
            return "Error: Cannot fetch private/internal URLs";

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.MaxResponseContentBufferSize = Math.Min(maxChars * 2, 1024 * 1024);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // Check Content-Length before downloading
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > maxChars * 4L)
                return $"Content too large: {contentLength.Value:N0} bytes (max {maxChars} chars requested). Use a more specific query.";

            // Bounded read: only download up to maxChars worth of data
            var buffer = new char[maxChars + 1024];
            int totalChars = 0;
            bool isHtml = response.Content.Headers.ContentType?.MediaType?.Contains("text/html") == true;

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (totalChars < maxChars)
            {
                var chunkSize = Math.Min(4096, maxChars + 1024 - totalChars);
                var chunk = new char[chunkSize];
                var charsRead = await reader.ReadAsync(chunk, 0, chunkSize).ConfigureAwait(false);
                if (charsRead == 0) break;

                Array.Copy(chunk, 0, buffer, totalChars, charsRead);
                totalChars += charsRead;

                // Detect HTML from first chunk if Content-Type header isn't reliable
                if (!isHtml && totalChars > 100)
                {
                    var preview = new string(buffer, 0, Math.Min(totalChars, 500));
                    isHtml = preview.Contains("<html", StringComparison.OrdinalIgnoreCase)
                          || preview.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
                }
            }

            var content = new string(buffer, 0, totalChars);

            if (isHtml)
                content = StripHtml(content);

            if (content.Length > maxChars)
                content = content[..maxChars] +
                    $"\n... (truncated, more content available)";

            return content;
        }
        catch (OperationCanceledException)
        {
            return "Fetch timed out — the page may be too large or slow.";
        }
        catch (HttpRequestException ex)
        {
            return $"HTTP error: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WebFetch failed for URL: {Url}", url);
            return $"Fetch failed: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════
    //  Search providers
    // ═══════════════════════════════════════════

    /// <summary>DuckDuckGo via HTML endpoint — free, no API key.</summary>
    private async Task<string?> TryDuckDuckGoAsync(HttpClient http, string query, int topK)
    {
        try
        {
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Parse result: <a rel="nofollow" class="result__a" href="...">Title</a>
            var resultMatches = Regex.Matches(html,
                @"<a\s+rel=""nofollow""\s+class=""result__a""\s+href=""([^""]+)"">(.*?)</a>",
                RegexOptions.IgnoreCase);

            // Parse snippet: <a class="result__snippet" ...>text</a>
            var snippetMatches = Regex.Matches(html,
                @"<a\s+class=""result__snippet""[^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase);

            if (resultMatches.Count == 0)
            {
                _logger.LogDebug("DDG returned no parseable results for: {Query}", query);
                return null;
            }

            var sb = new System.Text.StringBuilder();
            int count = Math.Min(resultMatches.Count, topK);

            for (int i = 0; i < count; i++)
            {
                var href = resultMatches[i].Groups[1].Value;
                var title = StripHtmlTags(resultMatches[i].Groups[2].Value);
                var snippet = i < snippetMatches.Count
                    ? StripHtmlTags(snippetMatches[i].Groups[1].Value)
                    : "";

                sb.AppendLine($"- [{title}]({href})");
                if (!string.IsNullOrWhiteSpace(snippet))
                    sb.AppendLine($"  {System.Net.WebUtility.HtmlDecode(snippet)}");
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DDG search failed for: {Query}", query);
            return null; // fall through to next provider
        }
    }

    /// <summary>Brave Search API — needs BRAVE_API_KEY env var.</summary>
    private async Task<string?> TryBraveSearchAsync(HttpClient http, string query, int topK)
    {
        var apiKey = LTAI.Core.Configuration.SecretManager.Get("BRAVE_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={topK}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("X-Subscription-Token", apiKey);

            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var results = doc.RootElement.GetProperty("web").GetProperty("results").EnumerateArray();

            var sb = new System.Text.StringBuilder();
            foreach (var r in results.Take(topK))
            {
                var title = r.GetProperty("title").GetString() ?? "";
                var link = r.GetProperty("url").GetString() ?? "";
                var desc = r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

                sb.AppendLine($"- [{title}]({link})");
                if (!string.IsNullOrWhiteSpace(desc))
                    sb.AppendLine($"  {desc}");
                sb.AppendLine();
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Brave search failed for: {Query}", query);
            return null;
        }
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
            }
        }
        return false;
    }

    /// <summary>Serper (Google) API — needs SERPER_API_KEY env var. Free tier: 2500 searches/month.</summary>
    private async Task<string?> TrySerperSearchAsync(HttpClient http, string query, int topK)
    {
        var apiKey = LTAI.Core.Configuration.SecretManager.Get("SERPER_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            var body = JsonSerializer.Serialize(new { q = query, num = topK });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search");
            req.Headers.Add("X-API-KEY", apiKey);
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;

            var sb = new System.Text.StringBuilder();

            // Organic results
            if (root.TryGetProperty("organic", out var organic))
            {
                foreach (var r in organic.EnumerateArray().Take(topK))
                {
                    var title = r.GetProperty("title").GetString() ?? "";
                    var link = r.GetProperty("link").GetString() ?? "";
                    var snippet = r.TryGetProperty("snippet", out var sn) ? sn.GetString() ?? "" : "";

                    sb.AppendLine($"- [{title}]({link})");
                    if (!string.IsNullOrWhiteSpace(snippet))
                        sb.AppendLine($"  {snippet}");
                    sb.AppendLine();
                }
            }

            // Also include top answer/knowledge panel if available
            if (root.TryGetProperty("answerBox", out var answerBox))
            {
                var ansTitle = answerBox.TryGetProperty("title", out var at) ? at.GetString() : null;
                var ansSnippet = answerBox.TryGetProperty("snippet", out var asn) ? asn.GetString() : null;
                if (ansTitle != null || ansSnippet != null)
                {
                    sb.Insert(0, $"**💡 Knowledge Panel**\n{ansTitle ?? ""}: {ansSnippet ?? ""}\n\n");
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Serper search failed for: {Query}", query);
            return null;
        }
    }

    // ═══════════════════════════════════════════
    //  HTML processing
    // ═══════════════════════════════════════════

    /// <summary>Single-pass HTML tag/entity stripper. ~10-50x faster than 9-pass regex.</summary>
    private static string StripHtml(string html)
    {
        if (html.IndexOf('<') < 0 && html.IndexOf('&') < 0)
            return html.Trim();

        var sb = new System.Text.StringBuilder(html.Length);
        bool inTag = false;
        bool inScript = false;
        bool inStyle = false;

        for (int i = 0; i < html.Length; i++)
        {
            var c = html[i];

            // Inside <script> — skip until </script>
            if (inScript)
            {
                if (c == '<' && i + 8 < html.Length &&
                    html[i + 1] == '/' &&
                    html.AsSpan(i + 2, 6).Equals("script", StringComparison.OrdinalIgnoreCase))
                {
                    inScript = false;
                    i += 8;
                    while (i < html.Length && html[i] != '>') i++;
                    if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                }
                continue;
            }

            // Inside <style> — skip until </style>
            if (inStyle)
            {
                if (c == '<' && i + 6 < html.Length &&
                    html[i + 1] == '/' &&
                    html.AsSpan(i + 2, 5).Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    inStyle = false;
                    i += 6;
                    while (i < html.Length && html[i] != '>') i++;
                    if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                }
                continue;
            }

            // Enter a tag
            if (c == '<')
            {
                inTag = true;

                // Detect script/style start tags
                if (i + 7 < html.Length)
                {
                    var next = html.AsSpan(i + 1);
                    if (next.StartsWith("script", StringComparison.OrdinalIgnoreCase) && (next[6] == '>' || char.IsWhiteSpace(next[6])))
                        inScript = true;
                    else if (next.StartsWith("style", StringComparison.OrdinalIgnoreCase) && (next[5] == '>' || char.IsWhiteSpace(next[5])))
                        inStyle = true;
                }
                continue;
            }

            if (c == '>' && inTag)
            {
                inTag = false;
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
                continue;
            }

            if (!inTag && !inScript && !inStyle)
            {
                // Decode HTML entities
                if (c == '&')
                {
                    var entity = DecodeEntity(html, i, out int consumed);
                    if (entity != null)
                    {
                        sb.Append(entity);
                        i += consumed;
                        continue;
                    }
                }

                // Collapse whitespace
                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length == 0 || sb[^1] != ' ')
                        sb.Append(' ');
                    continue;
                }

                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    private static string? DecodeEntity(string html, int start, out int consumed)
    {
        var end = html.IndexOf(';', start + 1);
        if (end < 0 || end - start > 12) { consumed = 0; return null; }

        var entity = html.AsSpan(start + 1, end - start - 1);
        consumed = end - start;

        if (entity.Length == 0) return null;

        if (entity[0] == '#')
        {
            if (entity.Length > 1 && (entity[1] == 'x' || entity[1] == 'X') &&
                int.TryParse(entity[2..], System.Globalization.NumberStyles.HexNumber, null, out var cp))
                return char.ConvertFromUtf32(cp);
            if (int.TryParse(entity[1..], out cp))
                return char.ConvertFromUtf32(cp);
            return null;
        }

        return entity switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => " ",
            "ndash" => "–",
            "mdash" => "—",
            "hellip" => "…",
            "laquo" => "«",
            "raquo" => "»",
            "bull" => "•",
            "lsquo" => "'",
            "rsquo" => "'",
            "ldquo" => "\"",
            "rdquo" => "\"",
            _ => null
        };
    }

    private static string StripHtmlTags(string html)
    {
        return Regex.Replace(html, @"<[^>]+>", "");
    }
}
