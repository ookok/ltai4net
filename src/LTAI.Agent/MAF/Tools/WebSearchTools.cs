using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools;

[Description("Web page content extraction and search engine query tools")]
public sealed class WebSearchTools
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36" } }
    };

    [Description("Fetch and extract readable text content from a web page URL. Strips HTML tags and scripts.")]
    public static async Task<string> FetchPage(
        [Description("Full URL of the web page")] string url,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var html = await _http.GetStringAsync(url, cancellationToken);
            var title = ExtractTitle(html);
            var text = StripHtml(html);
            text = System.Web.HttpUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"[\s\r\n]+", " ").Trim();
            if (text.Length > 10000) text = text[..10000] + $"\n... (truncated, total {text.Length} chars)";

            var links = ExtractLinks(html, url).Take(30).ToList();

            return JsonSerializer.Serialize(new { url, title, text, textLength = text.Length, links = links.Select(l => new { text = l.Text, href = l.Href }) });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to fetch {url}: {ex.Message}" });
        }
    }

    [Description("Extract metadata from a web page: title, description, keywords, OG tags, and RSS/Atom feed links.")]
    public static async Task<string> ExtractMetadata(
        [Description("Full URL of the web page")] string url,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var html = await _http.GetStringAsync(url, cancellationToken);
            var meta = new Dictionary<string, string>();

            meta["title"] = ExtractTitle(html);
            meta["description"] = ExtractMeta(html, "description");
            meta["keywords"] = ExtractMeta(html, "keywords");
            meta["og:title"] = ExtractMeta(html, "og:title", "property");
            meta["og:description"] = ExtractMeta(html, "og:description", "property");
            meta["og:image"] = ExtractMeta(html, "og:image", "property");
            meta["og:type"] = ExtractMeta(html, "og:type", "property");
            meta["twitter:card"] = ExtractMeta(html, "twitter:card");
            meta["author"] = ExtractMeta(html, "author");
            meta["canonical"] = ExtractLinkRel(html, "canonical");
            meta["rss"] = ExtractLinkType(html, "application/rss+xml");
            meta["atom"] = ExtractLinkType(html, "application/atom+xml");

            meta = meta.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);

            return JsonSerializer.Serialize(new { url, metadata = meta });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    [Description("Perform a web search across multiple engines: Bing (best Chinese coverage) + DuckDuckGo fallback. No API key needed. Returns titles, snippets, and URLs.")]
    public static async Task<string> WebSearch(
        [Description("Search query")] string query,
        [Description("Max number of results, default 10")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WebSearchResult>();

        var isChinese = Regex.IsMatch(query, @"[\u4e00-\u9fff]");

        if (isChinese)
        {
            try
            {
                var bingResults = await SearchBingAsync(query, maxResults, cancellationToken);
                if (bingResults.Count > 0)
                {
                    results.AddRange(bingResults);
                }
                else
                {
                    var ddgResults = await SearchDuckDuckGoAsync(query, maxResults, cancellationToken);
                    results.AddRange(ddgResults);
                }
            }
            catch
            {
                var ddgResults = await SearchDuckDuckGoAsync(query, maxResults, cancellationToken);
                results.AddRange(ddgResults);
            }
        }
        else
        {
            try
            {
                var ddgResults = await SearchDuckDuckGoAsync(query, maxResults, cancellationToken);
                results.AddRange(ddgResults);
            }
            catch
            {
                var bingResults = await SearchBingAsync(query, maxResults, cancellationToken);
                results.AddRange(bingResults);
            }
        }

        var usedEngines = isChinese ? (results.Count > 0 ? "bing" : "duckduckgo_fallback") : "duckduckgo";

        return JsonSerializer.Serialize(new
        {
            query,
            engine = usedEngines,
            count = results.Count,
            results = results.Select(r => new { r.Title, r.Url, r.Snippet })
        });
    }

    private static async Task<List<WebSearchResult>> SearchBingAsync(string query, int maxResults, CancellationToken ct)
    {
        var results = new List<WebSearchResult>();
        var searchUrl = $"https://cn.bing.com/search?q={Uri.EscapeDataString(query)}&setlang=zh-cn&count={maxResults}&ensearch=0";
        var html = await _http.GetStringAsync(searchUrl, ct);

        var itemMatches = Regex.Matches(html, @"<li class=""b_algo"">(.*?)</li>", RegexOptions.Singleline);

        foreach (Match item in itemMatches.Take(maxResults))
        {
            var block = item.Groups[1].Value;
            var hrefMatch = Regex.Match(block, @"<a[^>]*href=""(https?://[^""]+)""");
            var titleMatch = Regex.Match(block, @"<a[^>]*>([^<]+)</a>");
            var snippetMatch = Regex.Match(block, @"(?:<p[^>]*>|<div class=""b_caption""[^>]*>)([^<]+(?:<[^>]+>[^<]*)*)</(?:p|div)>");

            if (hrefMatch.Success && titleMatch.Success)
            {
                results.Add(new WebSearchResult
                {
                    Title = System.Web.HttpUtility.HtmlDecode(titleMatch.Groups[1].Value),
                    Url = hrefMatch.Groups[1].Value,
                    Snippet = snippetMatch.Success
                        ? System.Web.HttpUtility.HtmlDecode(Regex.Replace(snippetMatch.Groups[1].Value, @"<[^>]+>", "")).Trim()
                        : ""
                });
            }
        }

        if (results.Count == 0)
        {
            var fallbackUrl = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&setlang=zh-cn&count={maxResults}";
            var fallbackHtml = await _http.GetStringAsync(fallbackUrl, ct);
            var fallbackMatches = Regex.Matches(fallbackHtml, @"<li class=""b_algo"">(.*?)</li>", RegexOptions.Singleline);

            foreach (Match item in fallbackMatches.Take(maxResults))
            {
                var block = item.Groups[1].Value;
                var hrefMatch = Regex.Match(block, @"<a[^>]*href=""(https?://[^""]+)""");
                var titleMatch = Regex.Match(block, @"<a[^>]*>([^<]+)</a>");
                if (hrefMatch.Success && titleMatch.Success)
                {
                    results.Add(new WebSearchResult
                    {
                        Title = System.Web.HttpUtility.HtmlDecode(titleMatch.Groups[1].Value),
                        Url = hrefMatch.Groups[1].Value,
                        Snippet = ""
                    });
                }
            }
        }

        return results;
    }

    private static async Task<List<WebSearchResult>> SearchDuckDuckGoAsync(string query, int maxResults, CancellationToken ct)
    {
        var results = new List<WebSearchResult>();
        var searchUrl = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        var html = await _http.GetStringAsync(searchUrl, ct);

        var linkMatches = Regex.Matches(html, @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>([^<]+)</a>");
        var snippetMatches = Regex.Matches(html, @"<a[^>]+class=""result__snippet""[^>]*>([^<]+)</a>");

        for (int i = 0; i < Math.Min(linkMatches.Count, maxResults); i++)
        {
            results.Add(new WebSearchResult
            {
                Title = System.Web.HttpUtility.HtmlDecode(linkMatches[i].Groups[2].Value),
                Url = linkMatches[i].Groups[1].Value,
                Snippet = i < snippetMatches.Count
                    ? System.Web.HttpUtility.HtmlDecode(snippetMatches[i].Groups[1].Value).Trim()
                    : ""
            });
        }

        return results;
    }

    private static string ExtractTitle(string html)
    {
        var match = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
        return match.Success ? System.Web.HttpUtility.HtmlDecode(match.Groups[1].Value).Trim() : "";
    }

    private static string ExtractMeta(string html, string name, string attr = "name")
    {
        var pattern = $@"<meta[^>]+{attr}=[""']{Regex.Escape(name)}[""'][^>]+content=[""']([^""']+)[""']";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            pattern = $@"<meta[^>]+content=[""']([^""']+)[""'][^>]+{attr}=[""']{Regex.Escape(name)}[""']";
            match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        }
        return match.Success ? System.Web.HttpUtility.HtmlDecode(match.Groups[1].Value) : "";
    }

    private static string ExtractLinkRel(string html, string rel)
    {
        var match = Regex.Match(html, $@"<link[^>]+rel=[""']{rel}[""'][^>]+href=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string ExtractLinkType(string html, string type)
    {
        var match = Regex.Match(html, $@"<link[^>]+type=[""']{Regex.Escape(type)}[""'][^>]+href=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static List<(string Text, string Href)> ExtractLinks(string html, string baseUrl)
    {
        var matches = Regex.Matches(html, @"<a[^>]+href=[""']([^""']+)[""'][^>]*>([^<]*)</a>", RegexOptions.IgnoreCase);
        var uri = new Uri(baseUrl);
        return matches.Select(m =>
        {
            var href = m.Groups[1].Value;
            var text = System.Web.HttpUtility.HtmlDecode(m.Groups[2].Value).Trim();
            if (!href.StartsWith("http") && !href.StartsWith("//"))
            {
                try { href = new Uri(uri, href).ToString(); } catch { }
            }
            return (text.Length > 80 ? text[..80] + "..." : text, href);
        }).Where(l => !string.IsNullOrWhiteSpace(l.href) && !string.IsNullOrWhiteSpace(l.Item1)).ToList();
    }

    private static string StripHtml(string html)
    {
        html = Regex.Replace(html, @"<(script|style|noscript|iframe|svg)[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"</?[^>]+>", " ");
        html = Regex.Replace(html, @"&nbsp;|&#160;", " ");
        html = Regex.Replace(html, @"&[a-z]+;", m => System.Web.HttpUtility.HtmlDecode(m.Value));
        return html;
    }

    private sealed class WebSearchResult
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Snippet { get; set; } = "";
    }
}
