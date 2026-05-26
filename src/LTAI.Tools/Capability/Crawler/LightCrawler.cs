using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Crawler;

public enum PageType { List, Table, Detail, Unknown }

public record LightPage(string Url, string Title, PageType PageType, List<Dictionary<string, string>> Items,
    List<AttachmentInfo> Attachments, int? NextPageNumber, long FetchMs);

public record AttachmentInfo(string Url, string Type, string Name, long? Size);

public record CrawlTask(string Url, Func<LightPage, Task>? Callback = null);

public record SpiderConfig(int MaxConcurrent = 5, int MaxPages = 100, int MaxDepth = 2,
    int DelayMs = 200, bool RespectRobots = true, bool RotateTls = true);

public sealed class LightCrawler
{
    private static readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    private readonly ILogger<LightCrawler> _logger;
    private static readonly string[] TlsProfiles =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15"
    };

    public LightCrawler(ILogger<LightCrawler>? logger = null)
    {
        _logger = logger ?? NullLogger<LightCrawler>.Instance;
    }

    public async Task<LightPage> FetchAsync(string url)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", RotateTlsProfile());
            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

            var response = await _client.SendAsync(request).ConfigureAwait(false);
            var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var title = Regex.Match(html, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var pageType = DetectPageType(html);
            var items = ExtractItems(html, pageType);
            var attachments = ExtractAttachments(html, url);
            var pagination = ExtractPagination(html);

            sw.Stop();
            return new LightPage(url, title, pageType, items, attachments, pagination, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fetch failed: {Url}", url);
            sw.Stop();
            return new LightPage(url, "Error: " + ex.Message.Split('\n')[0], PageType.Unknown,
                new(), new(), null, sw.ElapsedMilliseconds);
        }
    }

    public async Task<List<LightPage>> FetchMultipleAsync(List<string> urls, int maxConcurrency = 5)
    {
        var results = new ConcurrentBag<LightPage>();
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = urls.Select(async url =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try { results.Add(await FetchAsync(url)); }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OrderBy(r => urls.IndexOf(r.Url)).ToList();
    }

    private static PageType DetectPageType(string html)
    {
        var lower = html.ToLowerInvariant();
        var ulCount = Regex.Matches(lower, @"<ul[>\s]").Count;
        var tableCount = Regex.Matches(lower, @"<table[>\s]").Count;
        var articleCount = Regex.Matches(lower, @"<article[>\s]").Count;
        if (tableCount > 2) return PageType.Table;
        if (ulCount > 3) return PageType.List;
        if (articleCount > 0) return PageType.Detail;
        return PageType.Unknown;
    }

    private static List<Dictionary<string, string>> ExtractItems(string html, PageType pageType)
    {
        var items = new List<Dictionary<string, string>>();
        if (pageType == PageType.List || pageType == PageType.Unknown)
        {
            var liMatches = Regex.Matches(html, @"<li[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match m in liMatches.Take(50))
            {
                var content = StripHtml(m.Groups[1].Value);
                var linkMatch = Regex.Match(content, @"href=[""']([^""']+)[""']\s*[^>]*>([^<]+)", RegexOptions.IgnoreCase);
                items.Add(new Dictionary<string, string>
                {
                    ["text"] = Regex.Replace(content, @"<[^>]+>", "").Trim().Truncate(200),
                    ["link"] = linkMatch.Success ? linkMatch.Groups[1].Value : "",
                    ["link_text"] = linkMatch.Success ? linkMatch.Groups[2].Value.Trim() : ""
                });
            }
        }
        if (pageType == PageType.Table)
        {
            var rowMatches = Regex.Matches(html, @"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var headers = Regex.Matches(html, @"<th[^>]*>(.*?)</th>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(m => StripHtml(m.Groups[1].Value)).ToList();
            foreach (Match row in rowMatches.Skip(1).Take(20))
            {
                var cells = Regex.Matches(row.Groups[1].Value, @"<td[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                    .Select(m => StripHtml(m.Groups[1].Value)).ToList();
                var item = new Dictionary<string, string>();
                for (int i = 0; i < Math.Min(headers.Count, cells.Count); i++)
                    item[headers[i]] = cells[i];
                items.Add(item);
            }
        }
        return items;
    }

    private static List<AttachmentInfo> ExtractAttachments(string html, string baseUrl)
    {
        var attachments = new List<AttachmentInfo>();
        var matches = Regex.Matches(html, @"<a[^>]*href=[""']([^""']+\.(?:pdf|docx?|xlsx?|zip|rar))[""'][^>]*>([^<]*)</a>", RegexOptions.IgnoreCase);
        foreach (Match m in matches.Take(10))
        {
            var href = m.Groups[1].Value;
            if (!href.StartsWith("http") && Uri.TryCreate(new Uri(baseUrl), href, out var absolute)) href = absolute.ToString();
            attachments.Add(new AttachmentInfo(href, Path.GetExtension(href).TrimStart('.'), m.Groups[2].Value.Trim(), null));
        }
        return attachments;
    }

    private static int? ExtractPagination(string html)
    {
        var match = Regex.Match(html, @"(?:page|p)=(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var page)) return page + 1;
        return null;
    }

    private static string RotateTlsProfile() => TlsProfiles[Random.Shared.Next(TlsProfiles.Length)];
    private static string StripHtml(string html) => Regex.Replace(html, @"<[^>]+>", " ").Trim();
}

public sealed class Spider
{
    private readonly LightCrawler _crawler;
    private readonly SpiderConfig _config;
    private readonly HashSet<string> _visited = new();
    private readonly ConcurrentQueue<CrawlTask> _queue = new();
    private int _pageCount;

    public Spider(LightCrawler crawler, SpiderConfig? config = null)
    {
        _crawler = crawler; _config = config ?? new SpiderConfig();
    }

    public async Task CrawlAsync(string startUrl, Func<LightPage, Task>? parseCallback = null)
    {
        _queue.Enqueue(new CrawlTask(startUrl, parseCallback));
        var workers = new List<Task>();

        for (int i = 0; i < _config.MaxConcurrent; i++)
            workers.Add(WorkerAsync());

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task WorkerAsync()
    {
        while (_queue.TryDequeue(out var task))
        {
            if (_pageCount >= _config.MaxPages) break;
            var normalized = NormalizeUrl(task.Url);
            if (!_visited.Add(normalized)) continue;

            Interlocked.Increment(ref _pageCount);
            var page = await _crawler.FetchAsync(task.Url).ConfigureAwait(false);
            if (task.Callback != null) await task.Callback(page).ConfigureAwait(false);

            foreach (var item in page.Items)
            {
                if (item.TryGetValue("link", out var link) && !string.IsNullOrEmpty(link))
                    _queue.Enqueue(new CrawlTask(link));
            }

            if (_config.DelayMs > 0) await Task.Delay(_config.DelayMs).ConfigureAwait(false);
        }
    }

    private static string NormalizeUrl(string url)
    {
        try { var uri = new Uri(url); return uri.GetLeftPart(UriPartial.Path); }
        catch { return url; }
    }
}

public static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
