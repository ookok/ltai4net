using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Network.Acceleration;

public sealed record ExternalSearchResult
{
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Snippet { get; init; } = string.Empty;
    public string Engine { get; init; } = string.Empty;
    public double Relevance { get; init; }
}

public sealed record GitHubRelease
{
    public string Repo { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime PublishedAt { get; init; }
    public List<string> Assets { get; init; } = new();
}

public sealed record DnsRecord
{
    public string Domain { get; init; } = string.Empty;
    public List<string> IPs { get; init; } = new();
    public int Ttl { get; init; }
    public string Provider { get; init; } = string.Empty;
    public DateTime CachedAt { get; init; } = DateTime.UtcNow;

    public bool IsExpired => DateTime.UtcNow > CachedAt.AddSeconds(Ttl);
}

public enum LearningStrategy
{
    ArxivMirror,
    SciHub,
    WaybackMachine,
    IpfsGateway,
    SemanticScholar,
    CloudflareWarp,
    OfflineDocCache
}

public sealed class ExternalAccess
{
    private static readonly Lazy<ExternalAccess> _instance = new(() => new ExternalAccess());
    public static ExternalAccess Instance => _instance.Value;

    private readonly HttpClient _http;
    private readonly List<string> _searchEngines;
    private readonly List<string> _gitHubMirrors;
    private readonly List<string> _dohProviders;
    private readonly ConcurrentDictionary<string, DnsRecord> _dnsCache;
    private readonly ConcurrentDictionary<string, (List<ExternalSearchResult> Results, DateTime Cached)> _searchCache;
    private readonly ConcurrentDictionary<string, GitHubRelease> _releaseCache;
    private readonly string[] _arxiveMirrors;
    private readonly string[] _sciHubDomains;
    private readonly string[] _ipfsGateways;
    private readonly string _waybackUrl;
    private readonly ILogger<ExternalAccess> _logger;

    private static readonly TimeSpan DnsCacheTtl = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromSeconds(3600);

    private ExternalAccess()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _searchEngines = new List<string> { "duckduckgo", "searxng", "bing" };
        _gitHubMirrors = new List<string>
        {
            "https://ghproxy.com/https://raw.githubusercontent.com",
            "https://ghproxy.net/https://raw.githubusercontent.com",
            "https://gitclone.com/github.com",
            "https://raw.fastgit.org",
            "https://hub.fastgit.xyz",
            "https://raw.githubusercontent.com",
        };
        _dohProviders = new List<string> { "cloudflare", "google", "quad9" };
        _dnsCache = new ConcurrentDictionary<string, DnsRecord>(StringComparer.OrdinalIgnoreCase);
        _searchCache = new ConcurrentDictionary<string, (List<ExternalSearchResult>, DateTime)>();
        _releaseCache = new ConcurrentDictionary<string, GitHubRelease>();

        _arxiveMirrors = new[]
        {
            "https://xxx.itp.ac.cn",
            "https://arxiv.xixiaoyao.cn",
            "https://cn.arxiv.org",
        };

        _sciHubDomains = new[]
        {
            "https://sci-hub.se",
            "https://sci-hub.st",
            "https://sci-hub.ru",
            "https://sci-hub.ee",
            "https://sci-hub.wf",
        };

        _ipfsGateways = new[]
        {
            "https://cloudflare-ipfs.com",
            "https://ipfs.fleek.co",
            "https://dweb.link",
            "https://gateway.pinata.cloud",
            "https://ipfs.io",
        };

        _waybackUrl = "https://web.archive.org";

        _logger = NullLogger<ExternalAccess>.Instance;
    }

    public ExternalAccess(ILogger<ExternalAccess> logger) : this()
    {
        _logger = logger;
    }

    public async Task<List<ExternalSearchResult>> DeepSearchAsync(string query, int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        if (_searchCache.TryGetValue(query, out var cached) && DateTime.UtcNow - cached.Cached < SearchCacheTtl)
        {
            _logger.LogDebug("Search cache hit for: {Query}", query);
            return cached.Results;
        }

        var tasks = new List<Task<List<ExternalSearchResult>>>
        {
            _searchDuckDuckGo(query, maxResults, cancellationToken),
            _searchSearxng(query, maxResults, cancellationToken),
            _searchBing(query, maxResults, cancellationToken),
        };

        var results = await Task.WhenAll(tasks);
        var allResults = results.SelectMany(r => r).ToList();

        var unique = allResults
            .GroupBy(r => r.Url)
            .Select(g => g.OrderByDescending(r => r.Relevance).First())
            .ToList();

        var reranked = _rerankResults(query, unique);

        var final = reranked
            .OrderByDescending(r => r.Relevance)
            .Take(maxResults)
            .ToList();

        _searchCache[query] = (final, DateTime.UtcNow);
        _logger.LogInformation("Deep search for '{Query}' returned {Count} results", query, final.Count);
        return final;
    }

    private async Task<List<ExternalSearchResult>> _searchDuckDuckGo(string query, int max,
        CancellationToken cancellationToken)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://html.duckduckgo.com/html/?q={encoded}";
            var html = await _http.GetStringAsync(url, cancellationToken);

            var results = new List<ExternalSearchResult>();
            var linkMatches = Regex.Matches(html, @"<a[^>]+class=""result__a""[^>]+href=""([^""]+)""[^>]*>([^<]+)</a>");
            var snippetMatches = Regex.Matches(html, @"<a[^>]+class=""result__snippet""[^>]*>([^<]+)</a>");

            for (int i = 0; i < Math.Min(linkMatches.Count, max); i++)
            {
                var snippet = i < snippetMatches.Count
                    ? Regex.Replace(snippetMatches[i].Groups[1].Value, @"<[^>]+>", "").Trim()
                    : "";

                results.Add(new ExternalSearchResult
                {
                    Title = linkMatches[i].Groups[2].Value.Trim(),
                    Url = linkMatches[i].Groups[1].Value,
                    Snippet = snippet,
                    Engine = "DuckDuckGo",
                    Relevance = 0.7
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DuckDuckGo search failed");
            return new List<ExternalSearchResult>();
        }
    }

    private async Task<List<ExternalSearchResult>> _searchSearxng(string query, int max,
        CancellationToken cancellationToken)
    {
        var instances = new[]
        {
            "https://searx.be",
            "https://search.bus-hit.me",
            "https://searx.tiekoetter.com",
        };

        foreach (var instance in instances)
        {
            try
            {
                var encoded = Uri.EscapeDataString(query);
                var url = $"{instance}/search?q={encoded}&format=json&categories=general";
                var json = await _http.GetStringAsync(url, cancellationToken);

                using var doc = JsonDocument.Parse(json);
                var results = new List<ExternalSearchResult>();

                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var resultsArray))
                {
                    foreach (var item in resultsArray.EnumerateArray().Take(max))
                    {
                        var result = new ExternalSearchResult
                        {
                            Title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                            Url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                            Snippet = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                            Engine = "SearXNG",
                            Relevance = 0.65
                        };

                        if (!string.IsNullOrWhiteSpace(result.Url))
                            results.Add(result);
                    }
                }

                if (results.Count > 0)
                    return results;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SearXNG instance {Instance} failed", instance);
            }
        }

        return new List<ExternalSearchResult>();
    }

    private async Task<List<ExternalSearchResult>> _searchBing(string query, int max,
        CancellationToken cancellationToken)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://www.bing.com/search?q={encoded}&setlang=en";
            var html = await _http.GetStringAsync(url, cancellationToken);

            var results = new List<ExternalSearchResult>();
            var matches = Regex.Matches(html, @"<li class=""b_algo"">.*?<h2>.*?<a[^>]*href=""([^""]+)""[^>]*>([^<]+)</a>.*?<p[^>]*>([^<]+)</p>", RegexOptions.Singleline);

            foreach (Match match in matches.Take(max))
            {
                results.Add(new ExternalSearchResult
                {
                    Title = match.Groups[2].Value.Trim(),
                    Url = match.Groups[1].Value,
                    Snippet = Regex.Replace(match.Groups[3].Value, @"<[^>]+>", "").Trim(),
                    Engine = "Bing",
                    Relevance = 0.6
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bing search failed");
            return new List<ExternalSearchResult>();
        }
    }

    private List<ExternalSearchResult> _rerankResults(string query, List<ExternalSearchResult> results)
    {
        var queryTerms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var reranked = new List<ExternalSearchResult>(results.Count);
        foreach (var result in results)
        {
            var text = (result.Title + " " + result.Snippet).ToLowerInvariant();
            var matchCount = queryTerms.Count(term => text.Contains(term));
            var snippetScore = Math.Min(1.0, (double)result.Snippet.Length / 500.0);

            reranked.Add(result with { Relevance = 0.3 * matchCount + 0.4 * snippetScore + 0.3 * result.Relevance });
        }

        return reranked;
    }

    public async Task<string?> GitHubFetchAsync(string repoPath, string filePath,
        CancellationToken cancellationToken = default)
    {
        foreach (var mirror in _gitHubMirrors)
        {
            try
            {
                var url = $"{mirror.TrimEnd('/')}/{repoPath.Trim('/')}/{filePath.TrimStart('/')}";
                var content = await _http.GetStringAsync(url, cancellationToken);

                if (!string.IsNullOrWhiteSpace(content) && !content.Contains("404: Not Found"))
                {
                    _logger.LogInformation("GitHub fetch succeeded via {Mirror}", mirror);
                    return content;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GitHub mirror {Mirror} failed for {Repo}/{Path}", mirror, repoPath, filePath);
            }
        }

        _logger.LogWarning("All mirrors failed for {Repo}/{Path}", repoPath, filePath);
        return null;
    }

    private string _mirrorUrl(string originalUrl)
    {
        try
        {
            var uri = new Uri(originalUrl);
            var path = uri.AbsolutePath;

            foreach (var mirror in _gitHubMirrors)
            {
                if (mirror.Contains("raw.githubusercontent", StringComparison.OrdinalIgnoreCase)
                    && path.StartsWith("/raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                {
                    return mirror.TrimEnd('/') + path.Replace("/raw.githubusercontent.com", "");
                }
            }

            return _gitHubMirrors[0] + path;
        }
        catch
        {
            return originalUrl;
        }
    }

    public async Task<GitHubRelease?> WatchReleaseAsync(string repo, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await GitHubFetchAsync(repo, "releases/latest", cancellationToken);
            if (json is null)
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var release = new GitHubRelease
            {
                Repo = repo,
                Tag = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "",
                Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                PublishedAt = root.TryGetProperty("published_at", out var pub) && DateTime.TryParse(pub.GetString(), out var dt) ? dt : DateTime.UtcNow,
                Assets = new List<string>()
            };

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("browser_download_url", out var dl))
                        release.Assets.Add(dl.GetString() ?? "");
                }
            }

            _releaseCache[repo] = release;
            return release;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to watch release for {Repo}", repo);
            return null;
        }
    }

    public GitHubRelease? GetRelease(string repo)
    {
        return _releaseCache.TryGetValue(repo, out var release) ? release : null;
    }

    public async Task<DnsRecord?> DnsResolveAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (_dnsCache.TryGetValue(domain, out var cached) && !cached.IsExpired)
        {
            _logger.LogDebug("DNS cache hit for {Domain}", domain);
            return cached;
        }

        foreach (var provider in _dohProviders)
        {
            try
            {
                var record = await ResolveDohAsync(domain, provider, cancellationToken);
                if (record is not null && record.IPs.Count > 0)
                {
                    _dnsCache[domain] = record;
                    return record;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DoH {Provider} failed for {Domain}", provider, domain);
            }
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, cancellationToken);
            var record = new DnsRecord
            {
                Domain = domain,
                IPs = addresses.Select(a => a.ToString()).ToList(),
                Ttl = 300,
                Provider = "system",
                CachedAt = DateTime.UtcNow
            };
            _dnsCache[domain] = record;
            return record;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "All DNS resolution failed for {Domain}", domain);
            return null;
        }
    }

    private async Task<DnsRecord?> ResolveDohAsync(string domain, string provider, CancellationToken cancellationToken)
    {
        var dohUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cloudflare"] = "https://cloudflare-dns.com/dns-query",
            ["google"] = "https://dns.google/resolve",
            ["quad9"] = "https://dns.quad9.net/dns-query",
        };

        if (!dohUrls.TryGetValue(provider, out var dohUrl))
            return null;

        var url = $"{dohUrl}?name={Uri.EscapeDataString(domain)}&type=A";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));

        var response = await _http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var ips = new List<string>();
        if (root.TryGetProperty("Answer", out var answers))
        {
            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1
                    && answer.TryGetProperty("data", out var data))
                {
                    ips.Add(data.GetString() ?? "");
                }
            }
        }

        var ttl = 300;
        if (root.TryGetProperty("Answer", out var answerSection) && answerSection.GetArrayLength() > 0)
        {
            var first = answerSection[0];
            if (first.TryGetProperty("TTL", out var ttlVal))
                ttl = ttlVal.GetInt32();
        }

        return new DnsRecord
        {
            Domain = domain,
            IPs = ips,
            Ttl = ttl,
            Provider = provider,
            CachedAt = DateTime.UtcNow
        };
    }

    public async Task<Dictionary<string, DnsRecord?>> BatchResolveAsync(IEnumerable<string> domains,
        CancellationToken cancellationToken = default)
    {
        var domainList = domains.ToList();
        var tasks = domainList.Select(d => DnsResolveAsync(d, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);

        var dict = new Dictionary<string, DnsRecord?>();
        for (int i = 0; i < domainList.Count; i++)
        {
            dict[domainList[i]] = results[i];
        }

        return dict;
    }

    public async Task<string?> FetchPaperAsync(string? doi, string? url,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(doi))
        {
            var sciHubContent = await _trySciHub(doi, cancellationToken);
            if (sciHubContent is not null)
                return sciHubContent;
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            var uri = new Uri(url);
            if (uri.Host.Contains("arxiv.org", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("arxiv", StringComparison.OrdinalIgnoreCase))
            {
                var arxivId = ExtractArxivId(url);
                if (arxivId is not null)
                {
                    var arxivContent = await _tryArxivMirror(arxivId, cancellationToken);
                    if (arxivContent is not null)
                        return arxivContent;
                }
            }

            var waybackContent = await _tryWayback(url, cancellationToken);
            if (waybackContent is not null)
                return waybackContent;
        }

        _logger.LogWarning("Failed to fetch paper: DOI={Doi}, URL={Url}", doi, url);
        return null;
    }

    private async Task<string?> _trySciHub(string doi, CancellationToken cancellationToken)
    {
        foreach (var domain in _sciHubDomains)
        {
            try
            {
                var url = $"{domain.TrimEnd('/')}/{doi}";
                var response = await _http.GetAsync(url, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);

                    var pdfMatch = Regex.Match(html, @"(?:src|href)=""([^""]+\.pdf)""", RegexOptions.IgnoreCase);
                    if (pdfMatch.Success)
                    {
                        var pdfUrl = pdfMatch.Groups[1].Value;
                        if (!pdfUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            pdfUrl = domain.TrimEnd('/') + "/" + pdfUrl.TrimStart('/');

                        var pdfBytes = await _http.GetByteArrayAsync(pdfUrl, cancellationToken);
                        _logger.LogInformation("Sci-Hub resolved via {Domain}: {Doi}", domain, doi);
                        return Convert.ToBase64String(pdfBytes);
                    }
                }
                else
                {
                    _logger.LogDebug("Sci-Hub {Domain} returned {StatusCode} for {Doi}",
                        domain, response.StatusCode, doi);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sci-Hub {Domain} failed for {Doi}", domain, doi);
            }
        }

        return null;
    }

    private async Task<string?> _tryArxivMirror(string arxivId, CancellationToken cancellationToken)
    {
        foreach (var mirror in _arxiveMirrors)
        {
            try
            {
                var url = $"{mirror.TrimEnd('/')}/abs/{arxivId}";
                var response = await _http.GetAsync(url, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogInformation("arXiv mirror {Mirror} resolved {Id}", mirror, arxivId);
                    return html;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "arXiv mirror {Mirror} failed for {Id}", mirror, arxivId);
            }
        }

        return null;
    }

    private async Task<string?> _tryWayback(string url, CancellationToken cancellationToken)
    {
        try
        {
            var waybackUrl = $"{_waybackUrl}/web/{url}";
            var response = await _http.GetAsync(waybackUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(content) && !content.Contains("Wayback Machine doesn't have that page"))
                {
                    _logger.LogInformation("Wayback Machine resolved: {Url}", url);
                    return content;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wayback Machine failed for {Url}", url);
        }

        return null;
    }

    private async Task<string?> _tryIpfs(string cid, CancellationToken cancellationToken)
    {
        foreach (var gateway in _ipfsGateways)
        {
            try
            {
                var url = $"{gateway.TrimEnd('/')}/ipfs/{cid}";
                var response = await _http.GetAsync(url, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogInformation("IPFS gateway {Gateway} resolved {CID}", gateway, cid);
                    return content;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IPFS gateway {Gateway} failed for {CID}", gateway, cid);
            }
        }

        return null;
    }

    public async Task<List<ExternalSearchResult>> SearchAcademicAsync(string query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://api.semanticscholar.org/graph/v1/paper/search?query={encoded}&limit=20&fields=title,url,abstract";
            var json = await _http.GetStringAsync(url, cancellationToken);

            using var doc = JsonDocument.Parse(json);
            var results = new List<ExternalSearchResult>();

            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var paper in data.EnumerateArray())
                {
                    results.Add(new ExternalSearchResult
                    {
                        Title = paper.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Url = paper.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                        Snippet = paper.TryGetProperty("abstract", out var a) ? a.GetString() ?? "" : "",
                        Engine = "SemanticScholar",
                        Relevance = 0.8
                    });
                }
            }

            _logger.LogInformation("Semantic Scholar returned {Count} results for '{Query}'", results.Count, query);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Semantic Scholar search failed for {Query}", query);
            return new List<ExternalSearchResult>();
        }
    }

    public async Task<string?> FetchContentAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Direct fetch failed for {Url}", url);
        }

        var resilience = NetworkResilience.Instance;
        var fetchResult = await resilience.ResilientFetchAsync(url, cancellationToken);
        if (fetchResult.Success && fetchResult.Content is not null)
            return fetchResult.Content;

        try
        {
            var uri = new Uri(url);
            if (uri.AbsolutePath.Contains("/ipfs/", StringComparison.OrdinalIgnoreCase))
            {
                var cid = ExtractCid(url);
                if (cid is not null)
                {
                    var ipfsContent = await _tryIpfs(cid, cancellationToken);
                    if (ipfsContent is not null)
                        return ipfsContent;
                }
            }
        }
        catch { /* non-fatal */ }

        var waybackContent = await _tryWayback(url, cancellationToken);
        if (waybackContent is not null)
            return waybackContent;

        _logger.LogWarning("All strategies failed for {Url}", url);
        return null;
    }

    public Task PreCacheDocsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Background document pre-caching initiated");
        return Task.CompletedTask;
    }

    public Dictionary<LearningStrategy, string> GetStrategies()
    {
        return new Dictionary<LearningStrategy, string>
        {
            [LearningStrategy.ArxivMirror] = "Active",
            [LearningStrategy.SciHub] = "Active",
            [LearningStrategy.WaybackMachine] = "Active",
            [LearningStrategy.IpfsGateway] = "Active",
            [LearningStrategy.SemanticScholar] = "Active",
            [LearningStrategy.CloudflareWarp] = "NotConfigured",
            [LearningStrategy.OfflineDocCache] = "NotConfigured",
        };
    }

    private static string? ExtractArxivId(string url)
    {
        var match = Regex.Match(url, @"arxiv\.org.*?(\d{4}\.\d{4,5}|[a-z\-]+/\d{7})", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value;

        match = Regex.Match(url, @"(\d{4}\.\d{4,5})");
        if (match.Success)
            return match.Groups[1].Value;

        return null;
    }

    private static string? ExtractCid(string url)
    {
        var match = Regex.Match(url, @"/ipfs/([a-zA-Z0-9]{46,59})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public (int SearchCacheSize, int DnsCacheSize, int GitHubMirrorsAvailable) GetStats()
    {
        return (
            _searchCache.Count,
            _dnsCache.Count,
            _gitHubMirrors.Count(m => m.StartsWith("https://"))
        );
    }
}
