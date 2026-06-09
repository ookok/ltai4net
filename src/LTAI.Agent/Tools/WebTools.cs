using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.Configuration;
using LTAI.Mm.Ir;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Tools;

public sealed class WebTools
{
    private sealed record SearchEngine(string Name, string Url, string Region)
    {
        public string BuildUrl(string query) => Url.Replace("{keyword}", Uri.EscapeDataString(query));
    }

    private static readonly SearchEngine[] AllEngines =
    [
        new("Google", "https://www.google.com/search?q={keyword}", "global"),
        new("Google HK", "https://www.google.com.hk/search?q={keyword}", "cn"),
        new("Bing INT", "https://cn.bing.com/search?q={keyword}&ensearch=1", "global"),
        new("Bing CN", "https://cn.bing.com/search?q={keyword}&ensearch=0", "cn"),
        new("DuckDuckGo", "https://html.duckduckgo.com/html/?q={keyword}", "global"),
        new("Brave", "https://search.brave.com/search?q={keyword}", "global"),
        new("Yahoo", "https://search.yahoo.com/search?p={keyword}", "global"),
        new("Startpage", "https://www.startpage.com/sp/search?query={keyword}", "global"),
        new("Ecosia", "https://www.ecosia.org/search?q={keyword}", "global"),
        new("Qwant", "https://www.qwant.com/?q={keyword}", "global"),
        new("Baidu", "https://www.baidu.com/s?wd={keyword}", "cn"),
        new("Sogou", "https://sogou.com/web?query={keyword}", "cn"),
        new("360", "https://www.so.com/s?q={keyword}", "cn"),
        new("Toutiao", "https://so.toutiao.com/search?keyword={keyword}", "cn"),
        new("WeChat", "https://wx.sogou.com/weixin?type=2&query={keyword}", "cn"),
    ];

    private static readonly Regex GoogleResultRx = new(
        @"<a\s+href=""/url\?q=([^""]+)"">(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GoogleSnippetRx = new(
        @"<div\s+class=""[^""]*VwiC3b[^""]*""[^>]*>(.*?)</div>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DuckResultRx = new(
        @"<a\s+rel=""nofollow""\s+class=""result__a""\s+href=""([^""]+)"">(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DuckSnippetRx = new(
        @"<a\s+class=""result__snippet""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlTagRx = new(@"<[^>]+>", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WebTools> _logger;

    public WebTools(IHttpClientFactory httpFactory, ILogger<WebTools>? logger = null)
    {
        _httpFactory = httpFactory;
        _logger = logger ?? NullLogger<WebTools>.Instance;
    }

    [Description("Search the public web for current information. Supports 15+ search engines across CN and global regions. Can filter by region, time, and specific site.")]
    public async Task<string> WebSearch(
        [Description("Search query")][MM("desc=搜索关键词; min=1; max=500")] string query,
        [Description("Region: 'cn' for Chinese engines, 'global' for international, 'all' for both")][MM("desc=搜索区域; enums=cn|global|all")] string region = "all",
        [Description("Time filter: 'hour', 'day', 'week', 'month', 'year', or empty for no filter")][MM("desc=时间过滤; enums=hour|day|week|month|year")] string? timeFilter = null,
        [Description("Limit to a specific site (e.g. 'github.com'). Empty for all sites")][MM("desc=限定站点; nullable")] string? site = null,
        [Description("Number of results to return per engine (1-10)")][MM("desc=结果数量; min=1; max=10")] int topK = 5)
    {
        topK = Math.Clamp(topK, 1, 10);

        try
        {
            var http = _httpFactory.CreateClient("websearch");
            http.Timeout = TimeSpan.FromSeconds(15);

            var finalQuery = query;
            if (!string.IsNullOrWhiteSpace(site))
                finalQuery = $"site:{site} {query}";
            if (!string.IsNullOrWhiteSpace(timeFilter))
                finalQuery = AppendTimeFilter(finalQuery, timeFilter);

            // 1. Try WolframAlpha for math/conversion queries
            if (IsWolframQuery(query))
            {
                var waResult = await FetchWolframAlphaAsync(http, query).ConfigureAwait(false);
                if (waResult != null)
                    return waResult;
            }

            // 2. Try API-based search engines first
            var apiResult = await TryApiSearchAsync(http, finalQuery, topK).ConfigureAwait(false);
            if (apiResult != null)
                return apiResult;

            // 3. Fallback to scraping-based engines
            var engines = (region.ToLowerInvariant()) switch
            {
                "cn" => AllEngines.Where(e => e.Region == "cn").ToArray(),
                "global" => AllEngines.Where(e => e.Region == "global").ToArray(),
                _ => AllEngines
            };

            var sb = new StringBuilder();
            int totalResults = 0;

            foreach (var engine in engines)
            {
                if (totalResults >= topK) break;
                var url = engine.BuildUrl(finalQuery);
                var html = await TryFetchHtmlAsync(http, url).ConfigureAwait(false);
                if (html == null) continue;

                var results = ParseEngineResults(engine.Name, html);
                if (results.Count == 0) continue;

                sb.AppendLine($"**{engine.Name}**");
                var count = Math.Min(results.Count, topK - totalResults);
                for (int i = 0; i < count; i++)
                {
                    sb.AppendLine($"- [{results[i].Title}]({results[i].Url})");
                    if (!string.IsNullOrWhiteSpace(results[i].Snippet))
                        sb.AppendLine($"  {results[i].Snippet}");
                    sb.AppendLine();
                }
                totalResults += count;
                sb.AppendLine("---");
            }

            if (sb.Length == 0)
                return "No search results found. Try a different query or region.";

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebSearch failed for query: {Query}", query);
            return $"Search failed: {ex.Message}";
        }
    }

    private sealed record SearchResult(string Title, string Url, string Snippet);

    private List<SearchResult> ParseEngineResults(string engine, string html)
    {
        var results = new List<SearchResult>();
        switch (engine)
        {
            case "DuckDuckGo":
                var dm = DuckResultRx.Matches(html);
                var sm = DuckSnippetRx.Matches(html);
                for (int i = 0; i < dm.Count; i++)
                {
                    var url = System.Net.WebUtility.HtmlDecode(dm[i].Groups[1].Value);
                    var title = StripHtmlTags(dm[i].Groups[2].Value);
                    var snippet = i < sm.Count ? StripHtmlTags(sm[i].Groups[1].Value) : "";
                    results.Add(new SearchResult(title, url, System.Net.WebUtility.HtmlDecode(snippet)));
                }
                break;

            case "Google":
            case "Google HK":
                var gm = GoogleResultRx.Matches(html);
                var gs = GoogleSnippetRx.Matches(html);
                for (int i = 0; i < gm.Count; i++)
                {
                    var rawUrl = gm[i].Groups[1].Value;
                    var url = System.Net.WebUtility.UrlDecode(rawUrl.Split('&')[0]);
                    var title = StripHtmlTags(gm[i].Groups[2].Value);
                    var snippet = i < gs.Count ? StripHtmlTags(gs[i].Groups[1].Value) : "";
                    if (!url.StartsWith("http")) continue;
                    results.Add(new SearchResult(title, url, System.Net.WebUtility.HtmlDecode(snippet)));
                }
                break;

            default:
                var links = System.Text.RegularExpressions.Regex.Matches(html,
                    @"<a[^>]+href=""(https?://[^""]+)""[^>]*>(.*?)</a>",
                    RegexOptions.IgnoreCase);
                foreach (Match m in links)
                {
                    var title = StripHtmlTags(m.Groups[2].Value);
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    results.Add(new SearchResult(title.Trim(), m.Groups[1].Value, ""));
                    if (results.Count >= 10) break;
                }
                break;
        }
        return results;
    }

    private async Task<string?> TryApiSearchAsync(HttpClient http, string query, int topK)
    {
        // Try Brave API
        var braveKey = SecretManager.Get("BRAVE_API_KEY");
        if (!string.IsNullOrEmpty(braveKey))
        {
            try
            {
                var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={topK}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Accept", "application/json");
                req.Headers.Add("X-Subscription-Token", braveKey);
                using var resp = await http.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                var results = doc.RootElement.GetProperty("web").GetProperty("results").EnumerateArray();
                var sb = new StringBuilder();
                sb.AppendLine("**Brave Search**");
                foreach (var r in results.Take(topK))
                {
                    var title = r.GetProperty("title").GetString() ?? "";
                    var link = r.GetProperty("url").GetString() ?? "";
                    var desc = r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    sb.AppendLine($"- [{title}]({link})");
                    if (!string.IsNullOrWhiteSpace(desc)) sb.AppendLine($"  {desc}");
                    sb.AppendLine();
                }
                return sb.ToString();
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Brave API failed"); }
        }

        // Try Serper (Google via Serper)
        var serperKey = SecretManager.Get("SERPER_API_KEY");
        if (!string.IsNullOrEmpty(serperKey))
        {
            try
            {
                var body = JsonSerializer.Serialize(new { q = query, num = topK });
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://google.serper.dev/search");
                req.Headers.Add("X-API-KEY", serperKey);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                var root = doc.RootElement;
                var sb = new StringBuilder();
                if (root.TryGetProperty("answerBox", out var ab))
                {
                    var at = ab.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var asn = ab.TryGetProperty("snippet", out var sn) ? sn.GetString() : null;
                    if (at != null || asn != null)
                        sb.AppendLine($"**💡 {at ?? ""}**: {asn ?? ""}\n");
                }
                sb.AppendLine("**Google (via Serper)**");
                if (root.TryGetProperty("organic", out var organic))
                {
                    foreach (var r in organic.EnumerateArray().Take(topK))
                    {
                        var title = r.GetProperty("title").GetString() ?? "";
                        var link = r.GetProperty("link").GetString() ?? "";
                        var snippet = r.TryGetProperty("snippet", out var sn) ? sn.GetString() ?? "" : "";
                        sb.AppendLine($"- [{title}]({link})");
                        if (!string.IsNullOrWhiteSpace(snippet)) sb.AppendLine($"  {snippet}");
                        sb.AppendLine();
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Serper API failed"); }
        }

        return null;
    }

    private async Task<string?> TryFetchHtmlAsync(HttpClient http, string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch { return null; }
    }

    private static bool IsWolframQuery(string query)
    {
        var lower = query.ToLowerInvariant();
        return lower.Contains("calculate") || lower.Contains("convert")
            || lower.Contains("integrate") || lower.Contains("derivative")
            || lower.Contains("solve") || lower.Contains("weather")
            || lower.Contains("stock") || lower.Contains("population")
            || lower.Contains("gdp") || lower.Contains("nutrition")
            || (lower.Contains(" to ") && (lower.Contains(" usd") || lower.Contains(" cny") || lower.Contains(" eur")))
            || Regex.IsMatch(lower, @"^\d+\s+\w+\s+(to|in)\s+\w+$");
    }

    private static async Task<string?> FetchWolframAlphaAsync(HttpClient http, string query)
    {
        try
        {
            var url = $"https://www.wolframalpha.com/input?i={Uri.EscapeDataString(query)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var result = StripHtml(html);
            if (result.Length > 5000) result = result[..5000];
            return $"**WolframAlpha Result**\n{result}";
        }
        catch { return null; }
    }

    private static string AppendTimeFilter(string query, string filter)
    {
        var tbs = filter.ToLowerInvariant() switch
        {
            "hour" => "qdr:h", "day" => "qdr:d", "week" => "qdr:w",
            "month" => "qdr:m", "year" => "qdr:y", _ => null
        };
        return tbs != null ? $"{query}&tbs={tbs}" : query;
    }

    // ── WebFetch (unchanged, kept for reference) ──────────────────────

    [Description("Fetch a web URL (http/https only) and return its text content. 不支持 file:// 等本地协议。")]
    public async Task<string> WebFetch(
        [Description("URL to download (http/https only)")] string url,
        [Description("Maximum characters to return")] int maxChars = 50000)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"Error: Invalid URL '{url}'";
        if (uri.Scheme is not "http" and not "https")
            return "❌ WebFetch 不支持 file:// 协议。";
        if (IsPrivateHost(uri.Host))
            return "Error: Cannot fetch private/internal URLs";

        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false);
            if (addresses.Any(addr => IsPrivateIP(addr)))
                return "Error: Target resolved to internal IP, blocked";
        }
        catch { return "Error: DNS resolution failed"; }

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

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > maxChars * 4L)
                return $"Content too large: {contentLength.Value:N0} bytes (max {maxChars} chars). Use a more specific query.";

            var buffer = System.Buffers.ArrayPool<char>.Shared.Rent(maxChars + 1024);
            int totalChars = 0;
            bool isHtml = response.Content.Headers.ContentType?.MediaType?.Contains("text/html") == true;

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            try
            {
                while (totalChars < maxChars)
                {
                    var chunkSize = Math.Min(4096, maxChars + 1024 - totalChars);
                    var charsRead = await reader.ReadAsync(buffer, totalChars, chunkSize).ConfigureAwait(false);
                    if (charsRead == 0) break;
                    totalChars += charsRead;

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
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) { return "Fetch timed out."; }
        catch (HttpRequestException ex) { return $"HTTP error: {ex.Message}"; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebFetch failed: {Url}", url);
            return $"Fetch failed: {ex.Message}";
        }
    }

    [Description("Send a custom HTTP request (GET/POST/PUT/DELETE) and return the response")]
    public async Task<string> HttpRequest(
        [Description("HTTP method (GET, POST, PUT, DELETE)")] string method,
        [Description("Request URL")] string url,
        [Description("Optional JSON body for POST/PUT")] string? body = null,
        [Description("Optional JSON headers")] string? headers = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"Error: Invalid URL '{url}'";
        if (IsPrivateHost(uri.Host))
            return "Error: Cannot request private/internal URLs";
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false);
            if (addresses.Any(addr => IsPrivateIP(addr)))
                return "Error: Target resolved to internal IP, blocked";
        }
        catch { return "Error: DNS resolution failed"; }

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);

            using var req = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), uri);
            if (headers != null)
            {
                try
                {
                    using var hDoc = JsonDocument.Parse(headers);
                    foreach (var prop in hDoc.RootElement.EnumerateObject())
                        req.Headers.TryAddWithoutValidation(prop.Name, prop.Value.GetString());
                }
                catch { }
            }
            if (body != null && (method is "POST" or "PUT" or "PATCH"))
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (respBody.Length > 50000)
                respBody = respBody[..50000] + $"\n... (truncated)";
            return $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n\n{respBody}";
        }
        catch (Exception ex) { return $"HTTP request failed: {ex.Message}"; }
    }

    // ── HTML Helpers ──────────────────────────────────────────────────

    private static string StripHtml(string html)
    {
        if (html.IndexOf('<') < 0 && html.IndexOf('&') < 0)
            return html.Trim();

        var sb = new StringBuilder(html.Length);
        bool inTag = false, inScript = false, inStyle = false;

        for (int i = 0; i < html.Length; i++)
        {
            var c = html[i];
            if (inScript)
            {
                if (c == '<' && EndTagAt(html, i, "script")) { inScript = false; i += 8 + SkipToEnd(html, i + 8); if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); }
                continue;
            }
            if (inStyle)
            {
                if (c == '<' && EndTagAt(html, i, "style")) { inStyle = false; i += 6 + SkipToEnd(html, i + 6); if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); }
                continue;
            }
            if (c == '<')
            {
                inTag = true;
                var next = html.AsSpan(i + 1);
                if (next.StartsWith("script", StringComparison.OrdinalIgnoreCase) && (next[6] is '>' or ' ')) inScript = true;
                else if (next.StartsWith("style", StringComparison.OrdinalIgnoreCase) && (next[5] is '>' or ' ')) inStyle = true;
                continue;
            }
            if (c == '>' && inTag) { inTag = false; if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            if (!inTag && !inScript && !inStyle)
            {
                if (c == '&') { var (entity, consumed) = DecodeEntity(html, i); if (entity != null) { sb.Append(entity); i += consumed; continue; } }
                if (char.IsWhiteSpace(c)) { if (sb.Length == 0 || sb[^1] != ' ') sb.Append(' '); continue; }
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    private static bool EndTagAt(string html, int i, string tag) =>
        i + tag.Length + 2 < html.Length &&
        html[i + 1] == '/' &&
        html.AsSpan(i + 2, tag.Length).Equals(tag, StringComparison.OrdinalIgnoreCase);

    private static int SkipToEnd(string html, int start)
    {
        int i = start;
        while (i < html.Length && html[i] != '>') i++;
        return i - start;
    }

    private static (string? Entity, int Consumed) DecodeEntity(string html, int start)
    {
        var end = html.IndexOf(';', start + 1);
        if (end < 0 || end - start > 12) return (null, 0);
        var entity = html.AsSpan(start + 1, end - start - 1);
        if (entity.Length == 0) return (null, 0);
        if (entity[0] == '#')
        {
            if (entity.Length > 1 && (entity[1] == 'x' || entity[1] == 'X') &&
                int.TryParse(entity[2..], System.Globalization.NumberStyles.HexNumber, null, out var cp))
                return (char.ConvertFromUtf32(cp), end - start);
            if (int.TryParse(entity[1..], out cp))
                return (char.ConvertFromUtf32(cp), end - start);
            return (null, 0);
        }
        return entity switch
        {
            "amp" => ("&", end - start), "lt" => ("<", end - start),
            "gt" => (">", end - start), "quot" => ("\"", end - start),
            "apos" => ("'", end - start), "nbsp" => (" ", end - start),
            "ndash" => ("–", end - start), "mdash" => ("—", end - start),
            "hellip" => ("…", end - start), "bull" => ("•", end - start),
            _ => (null, 0)
        };
    }

    private static string StripHtmlTags(string html) => HtmlTagRx.Replace(html, "");

    private static bool IsPrivateHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return true;
        if (host is "127.0.0.1" or "localhost" or "::1" or "[::1]" or "0.0.0.0" or "[::]") return true;
        if (System.Net.IPAddress.TryParse(host, out var ip)) return IsPrivateIP(ip);
        return false;
    }

    private static bool IsPrivateIP(System.Net.IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4)
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || b[0] == 192 && b[1] == 168 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || b[0] == 0;
        if (b.Length == 16)
        {
            if (b[10] == 0xff && b[11] == 0xff)
                return b[12] == 10 || (b[12] == 172 && b[13] >= 16 && b[13] <= 31) || (b[12] == 192 && b[13] == 168) || b[12] == 127 || (b[12] == 169 && b[13] == 254) || b[12] == 0;
            return (b[0] & 0xfe) == 0xfc || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80);
        }
        return false;
    }
}
