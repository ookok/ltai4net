using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools;

/// <summary>
/// Web search (DuckDuckGo primary, Brave fallback) and fetch tools.
/// DuckDuckGo requires NO API key — works out of the box.
/// </summary>
public sealed class WebTools
{
    private readonly IHttpClientFactory _httpFactory;

    public WebTools(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

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

            // 1st: DuckDuckGo (no API key needed)
            var result = await TryDuckDuckGoAsync(http, query, topK);
            if (result != null) return result;

            // 2nd: Brave (if BRAVE_API_KEY set)
            result = await TryBraveSearchAsync(http, query, topK);
            if (result != null) return result;

            return "No search results available.";
        }
        catch (Exception ex)
        {
            return $"Search failed: {ex.Message}";
        }
    }

    [Description("Fetch a URL and return its text content")]
    public async Task<string> WebFetch(
        [Description("URL to download")] string url,
        [Description("Maximum characters to return")] int maxChars = 50000)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"Error: Invalid URL '{url}'";

        if (uri.Scheme is not "http" and not "https")
            return "Error: Only http/https URLs are supported";

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var response = await http.GetAsync(uri);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();

            if (response.Content.Headers.ContentType?.MediaType?.Contains("text/html") == true)
                content = StripHtml(content);

            if (content.Length > maxChars)
                content = content[..maxChars] +
                    $"\n... (truncated, {content.Length - maxChars} more chars)";

            return content;
        }
        catch (Exception ex)
        {
            return $"Fetch failed: {ex.Message}";
        }
    }

    /// <summary>
    /// DuckDuckGo search via HTML endpoint — free, no API key.
    /// Parses the non-JSON HTML results page for titles, URLs, snippets.
    /// </summary>
    private async Task<string?> TryDuckDuckGoAsync(HttpClient http, string query, int topK)
    {
        try
        {
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var html = await resp.Content.ReadAsStringAsync();

            // Parse result block: <a rel="nofollow" class="result__a" href="...">Title</a>
            var resultMatches = Regex.Matches(html,
                @"<a\s+rel=""nofollow""\s+class=""result__a""\s+href=""([^""]+)"">(.*?)</a>",
                RegexOptions.IgnoreCase);

            // Parse snippet: <a class="result__snippet" ...>text</a>
            var snippetMatches = Regex.Matches(html,
                @"<a\s+class=""result__snippet""[^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase);

            if (resultMatches.Count == 0)
                return null;

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
        catch
        {
            return null;
        }
    }

    private async Task<string?> TryBraveSearchAsync(HttpClient http, string query, int topK)
    {
        var apiKey = Environment.GetEnvironmentVariable("BRAVE_API_KEY");
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={topK}";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("X-Subscription-Token", apiKey);

            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
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
        catch
        {
            return null;
        }
    }

    private static string StripHtml(string html)
    {
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[^>]*>.*?</nav>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<footer[^>]*>.*?</footer>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        html = Regex.Replace(html, @"&nbsp;", " ");
        html = Regex.Replace(html, @"&amp;", "&");
        html = Regex.Replace(html, @"&lt;", "<");
        html = Regex.Replace(html, @"&gt;", ">");
        html = Regex.Replace(html, @"&quot;", "\"");
        html = Regex.Replace(html, @"\s+", " ");
        return html.Trim();
    }

    private static string StripHtmlTags(string html)
    {
        return Regex.Replace(html, @"<[^>]+>", "");
    }
}
