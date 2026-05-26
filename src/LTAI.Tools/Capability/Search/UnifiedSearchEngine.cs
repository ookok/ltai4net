using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Search;

public sealed class UnifiedSearchEngine : IDisposable
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "LTAI/5.5" } }
    };
    private readonly ILogger<UnifiedSearchEngine> _logger;

    public UnifiedSearchEngine(ILogger<UnifiedSearchEngine> logger)
    {
        _logger = logger;
    }

    public void Dispose() { }

    public async Task<List<SearchResult>> SearchAsync(
        string query,
        SearchSource[]? sources = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        sources ??= new[] { SearchSource.Web, SearchSource.Wikipedia, SearchSource.Documentation };
        var results = new List<SearchResult>();

        var tasks = new List<Task<List<SearchResult>>>();
        foreach (var source in sources)
        {
            tasks.Add(source switch
            {
                SearchSource.Wikipedia => SearchWikipediaAsync(query, maxResults, cancellationToken),
                SearchSource.Documentation => SearchDocsAsync(query, maxResults, cancellationToken),
                SearchSource.Web => SearchWebAsync(query, maxResults, cancellationToken),
                _ => Task.FromResult(new List<SearchResult>())
            });
        }

        var allResults = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var r in allResults)
            results.AddRange(r);

        return results
            .OrderByDescending(r => r.Relevance)
            .Take(maxResults)
            .ToList();
    }

    private async Task<List<SearchResult>> SearchWikipediaAsync(
        string query, int max, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={encoded}&format=json&srlimit={Math.Min(max, 10)}";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);
            var results = new List<SearchResult>();

            if (doc.RootElement.TryGetProperty("query", out var q) &&
                q.TryGetProperty("search", out var search))
            {
                foreach (var item in search.EnumerateArray())
                {
                    results.Add(new SearchResult
                    {
                        Title = item.GetProperty("title").GetString() ?? "",
                        Snippet = item.TryGetProperty("snippet", out var s) ? StripHtmlTags(s.GetString() ?? "") : "",
                        Url = $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(item.GetProperty("title").GetString() ?? "")}",
                        Source = "Wikipedia",
                        Relevance = 0.8
                    });
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wikipedia search failed");
            return new List<SearchResult>();
        }
    }

    private async Task<List<SearchResult>> SearchWebAsync(
        string query, int max, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://html.duckduckgo.com/html/?q={encoded}";
            var html = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

            var results = new List<SearchResult>();
            var linkMatches = Regex.Matches(html, @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>([^<]+)</a>");
            var snippetMatches = Regex.Matches(html, @"<a[^>]*class=""result__snippet""[^>]*>([^<]+)</a>");

            for (var i = 0; i < Math.Min(linkMatches.Count, max); i++)
            {
                results.Add(new SearchResult
                {
                    Title = linkMatches[i].Groups[2].Value.Trim(),
                    Url = linkMatches[i].Groups[1].Value,
                    Snippet = i < snippetMatches.Count ? snippetMatches[i].Groups[1].Value.Trim() : "",
                    Source = "DuckDuckGo",
                    Relevance = 1.0 - i * 0.1
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Web search failed");
            return new List<SearchResult>();
        }
    }

    private async Task<List<SearchResult>> SearchDocsAsync(
        string query, int max, CancellationToken ct)
    {
        var encoded = Uri.EscapeDataString(query);
        var url = $"https://api.search.mdn.org/query?q={encoded}&limit={Math.Min(max, 5)}";

        try
        {
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);
            var results = new List<SearchResult>();

            if (doc.RootElement.TryGetProperty("documents", out var docs))
            {
                foreach (var d in docs.EnumerateArray())
                {
                    results.Add(new SearchResult
                    {
                        Title = d.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Snippet = d.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                        Url = d.TryGetProperty("mdn_url", out var u) ? u.GetString() ?? "" : "",
                        Source = "MDN",
                        Relevance = 0.7
                    });
                }
            }

            return results;
        }
        catch
        {
            return new List<SearchResult>
            {
                new()
                {
                    Title = query,
                    Url = $"https://www.google.com/search?q={encoded}",
                    Snippet = "Search the web for more information.",
                    Source = "Web",
                    Relevance = 0.5
                }
            };
        }
    }

    private static string StripHtmlTags(string html) =>
        Regex.Replace(html, "<[^>]+>", " ").Trim();
}

public enum SearchSource { Web, Wikipedia, Documentation, Academic, News }

public sealed class SearchResult
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string Source { get; init; } = "";
    public double Relevance { get; init; }
}
