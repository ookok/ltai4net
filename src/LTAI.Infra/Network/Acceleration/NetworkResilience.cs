using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Infra.Network.Acceleration;

public sealed record MirrorMapping
{
    public string Domain { get; init; } = string.Empty;
    public string MirrorUrl { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public int Priority { get; init; }
}

public sealed record DomainIP
{
    public string IP { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public int LatencyMs { get; init; }
    public double SuccessRate { get; init; }
    public DateTime LastTested { get; init; } = DateTime.UtcNow;

    public bool IsHealthy() => SuccessRate >= 0.5;

    public double Score()
    {
        const int maxLatency = 2000;
        double latencyNorm = Math.Clamp((double)LatencyMs / maxLatency, 0.0, 1.0);
        return 0.6 * (1.0 - latencyNorm) + 0.4 * SuccessRate;
    }
}

public sealed record ProxyEntry
{
    public string Address { get; init; } = string.Empty;
    public string Protocol { get; init; } = "http";
    public int LatencyMs { get; init; }
    public double SuccessRate { get; init; }
    public DateTime LastTested { get; init; } = DateTime.UtcNow;
    public string Source { get; init; } = string.Empty;
    public int FailureCount { get; init; }
}

public sealed record FetchResult
{
    public bool Success { get; init; }
    public string? Content { get; init; }
    public int StatusCode { get; init; }
    public long LatencyMs { get; init; }
    public string? UsedProxy { get; init; }
    public string? UsedMirror { get; init; }
    public string? UsedIP { get; init; }

    public static FetchResult Failed(int statusCode, long latencyMs) => new()
    {
        Success = false,
        StatusCode = statusCode,
        LatencyMs = latencyMs
    };

    public static FetchResult Succeeded(string content, int statusCode, long latencyMs,
        string? usedProxy = null, string? usedMirror = null, string? usedIP = null) => new()
    {
        Success = true,
        Content = content,
        StatusCode = statusCode,
        LatencyMs = latencyMs,
        UsedProxy = usedProxy,
        UsedMirror = usedMirror,
        UsedIP = usedIP
    };
}

public sealed class NetworkResilience : IDisposable
{
    private static readonly Lazy<NetworkResilience> _instance = new(() => new NetworkResilience());
    public static NetworkResilience Instance => _instance.Value;

    private readonly HttpClient _http;
    private readonly List<MirrorMapping> _mirrors;
    private readonly ConcurrentDictionary<string, List<DomainIP>> _domainIPs;
    private readonly ConcurrentBag<ProxyEntry> _proxies;
    private readonly List<string> _proxySources;
    private readonly Dictionary<string, List<MirrorMapping>> _fallbackMirrors;
    private readonly Random _rng;
    private readonly ILogger<NetworkResilience> _logger;

    private const int MaxProxies = 200;
    private const int MaxConsecutiveFailures = 5;

    private NetworkResilience()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _mirrors = new List<MirrorMapping>();
        _domainIPs = new ConcurrentDictionary<string, List<DomainIP>>(StringComparer.OrdinalIgnoreCase);
        _proxies = new ConcurrentBag<ProxyEntry>();
        _proxySources = new List<string>();
        _fallbackMirrors = new Dictionary<string, List<MirrorMapping>>(StringComparer.OrdinalIgnoreCase);
        _rng = new Random();

        _logger = NullLogger<NetworkResilience>.Instance;

        InitializeMirrors();
        InitializeProxySources();
        PreSeedIPs();
    }

    public NetworkResilience(ILogger<NetworkResilience> logger) : this()
    {
        _logger = logger;
    }

    public void Dispose() { _http?.Dispose(); }

    private void InitializeMirrors()
    {
        var builtIn = new List<MirrorMapping>
        {
            new() { Domain = "github.com", MirrorUrl = "https://ghproxy.com/https://github.com", Category = "git", Priority = 1 },
            new() { Domain = "github.com", MirrorUrl = "https://gitclone.com/github.com", Category = "git", Priority = 2 },
            new() { Domain = "github.com", MirrorUrl = "https://ghproxy.net/https://github.com", Category = "git", Priority = 3 },
            new() { Domain = "github.com", MirrorUrl = "https://hub.fastgit.xyz", Category = "git", Priority = 4 },
            new() { Domain = "raw.githubusercontent.com", MirrorUrl = "https://raw.fastgit.org", Category = "raw", Priority = 1 },
            new() { Domain = "raw.githubusercontent.com", MirrorUrl = "https://ghproxy.com/https://raw.githubusercontent.com", Category = "raw", Priority = 2 },
            new() { Domain = "pypi.org", MirrorUrl = "https://pypi.tuna.tsinghua.edu.cn", Category = "pypi", Priority = 1 },
            new() { Domain = "pypi.org", MirrorUrl = "https://mirrors.aliyun.com/pypi", Category = "pypi", Priority = 2 },
            new() { Domain = "files.pythonhosted.org", MirrorUrl = "https://pypi.tuna.tsinghua.edu.cn/packages", Category = "pypi", Priority = 1 },
            new() { Domain = "registry.npmjs.org", MirrorUrl = "https://registry.npmmirror.com", Category = "npm", Priority = 1 },
            new() { Domain = "registry.npmjs.org", MirrorUrl = "https://registry.npm.taobao.org", Category = "npm", Priority = 2 },
            new() { Domain = "huggingface.co", MirrorUrl = "https://hf-mirror.com", Category = "huggingface", Priority = 1 },
            new() { Domain = "huggingface.co", MirrorUrl = "https://hf.xeduapi.com", Category = "huggingface", Priority = 2 },
            new() { Domain = "arxiv.org", MirrorUrl = "https://xxx.itp.ac.cn", Category = "arxiv", Priority = 1 },
            new() { Domain = "arxiv.org", MirrorUrl = "https://arxiv.xixiaoyao.cn", Category = "arxiv", Priority = 2 },
            new() { Domain = "arxiv.org", MirrorUrl = "https://cn.arxiv.org", Category = "arxiv", Priority = 3 },
            new() { Domain = "ipfs.io", MirrorUrl = "https://cloudflare-ipfs.com", Category = "ipfs", Priority = 1 },
            new() { Domain = "ipfs.io", MirrorUrl = "https://ipfs.fleek.co", Category = "ipfs", Priority = 2 },
            new() { Domain = "docker.io", MirrorUrl = "https://docker.mirrors.ustc.edu.cn", Category = "docker", Priority = 1 },
            new() { Domain = "docker.io", MirrorUrl = "https://hub-mirror.c.163.com", Category = "docker", Priority = 2 },
            new() { Domain = "registry-1.docker.io", MirrorUrl = "https://docker.mirrors.ustc.edu.cn", Category = "docker", Priority = 1 },
        };

        _mirrors.AddRange(builtIn);

        _fallbackMirrors["git"] = new List<MirrorMapping>
        {
            new() { Domain = "github.com", MirrorUrl = "https://ghproxy.com/https://github.com", Category = "git", Priority = 10 },
            new() { Domain = "github.com", MirrorUrl = "https://ghproxy.net/https://github.com", Category = "git", Priority = 11 },
            new() { Domain = "github.com", MirrorUrl = "https://gitclone.com/github.com", Category = "git", Priority = 12 },
        };

        _fallbackMirrors["pypi"] = new List<MirrorMapping>
        {
            new() { Domain = "pypi.org", MirrorUrl = "https://pypi.tuna.tsinghua.edu.cn/simple", Category = "pypi", Priority = 10 },
            new() { Domain = "pypi.org", MirrorUrl = "https://mirrors.aliyun.com/pypi/simple", Category = "pypi", Priority = 11 },
            new() { Domain = "pypi.org", MirrorUrl = "https://pypi.mirrors.ustc.edu.cn/simple", Category = "pypi", Priority = 12 },
        };

        _fallbackMirrors["npm"] = new List<MirrorMapping>
        {
            new() { Domain = "registry.npmjs.org", MirrorUrl = "https://registry.npmmirror.com", Category = "npm", Priority = 10 },
            new() { Domain = "registry.npmjs.org", MirrorUrl = "https://registry.npm.taobao.org", Category = "npm", Priority = 11 },
            new() { Domain = "registry.npmjs.org", MirrorUrl = "https://mirrors.cloud.tencent.com/npm", Category = "npm", Priority = 12 },
        };

        _fallbackMirrors["huggingface"] = new List<MirrorMapping>
        {
            new() { Domain = "huggingface.co", MirrorUrl = "https://hf-mirror.com", Category = "huggingface", Priority = 10 },
            new() { Domain = "huggingface.co", MirrorUrl = "https://hf.xeduapi.com", Category = "huggingface", Priority = 11 },
            new() { Domain = "huggingface.co", MirrorUrl = "https://hf.bytespider.eu.org", Category = "huggingface", Priority = 12 },
        };

        _fallbackMirrors["arxiv"] = new List<MirrorMapping>
        {
            new() { Domain = "arxiv.org", MirrorUrl = "https://xxx.itp.ac.cn", Category = "arxiv", Priority = 10 },
            new() { Domain = "arxiv.org", MirrorUrl = "https://arxiv.xixiaoyao.cn", Category = "arxiv", Priority = 11 },
            new() { Domain = "arxiv.org", MirrorUrl = "https://cn.arxiv.org", Category = "arxiv", Priority = 12 },
        };

        _fallbackMirrors["ipfs"] = new List<MirrorMapping>
        {
            new() { Domain = "ipfs.io", MirrorUrl = "https://cloudflare-ipfs.com", Category = "ipfs", Priority = 10 },
            new() { Domain = "ipfs.io", MirrorUrl = "https://ipfs.fleek.co", Category = "ipfs", Priority = 11 },
            new() { Domain = "ipfs.io", MirrorUrl = "https://dweb.link", Category = "ipfs", Priority = 12 },
            new() { Domain = "ipfs.io", MirrorUrl = "https://gateway.pinata.cloud", Category = "ipfs", Priority = 13 },
        };
    }

    private void InitializeProxySources()
    {
        _proxySources.AddRange(new[]
        {
            "https://raw.githubusercontent.com/proxifly/free-proxy-list/main/proxies/all/data.txt",
            "https://raw.githubusercontent.com/TheSpeedX/PROXY-List/master/http.txt",
            "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/http.txt",
            "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/socks4.txt",
            "https://raw.githubusercontent.com/ShiftyTR/Proxy-List/master/socks5.txt",
            "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/http.txt",
            "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/socks4.txt",
            "https://raw.githubusercontent.com/monosans/proxy-list/main/proxies/socks5.txt",
            "https://raw.githubusercontent.com/hookzof/socks5_list/master/proxy.txt",
            "https://raw.githubusercontent.com/jetkai/proxy-list/main/online-proxies/txt/proxies.txt",
            "https://raw.githubusercontent.com/roosterkid/openproxylist/main/HTTPS_RAW.txt",
            "https://raw.githubusercontent.com/roosterkid/openproxylist/main/SOCKS5_RAW.txt",
            "https://raw.githubusercontent.com/roosterkid/openproxylist/main/SOCKS4_RAW.txt",
            "https://raw.githubusercontent.com/ALIILAPRO/Proxy/main/http.txt",
        });
    }

    public string? GetMirrorUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var domain = uri.Host.ToLowerInvariant();

            foreach (var mirror in _mirrors)
            {
                if (domain.Contains(mirror.Domain, StringComparison.OrdinalIgnoreCase))
                    return _rewriteUrl(url, mirror.MirrorUrl);
            }

            foreach (var kvp in _fallbackMirrors)
            {
                if (domain.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    var fb = kvp.Value.OrderBy(m => m.Priority).FirstOrDefault();
                    if (fb is not null)
                        return _rewriteUrl(url, fb.MirrorUrl);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get mirror URL for {Url}", url);
            return null;
        }
    }

    public List<MirrorMapping> GetAllMirrors(string domain)
    {
        return _mirrors
            .Where(m => domain.Contains(m.Domain, StringComparison.OrdinalIgnoreCase)
                        || m.Domain.Contains(domain, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Priority)
            .ToList();
    }

    private string _rewriteUrl(string originalUrl, string mirrorBase)
    {
        try
        {
            var uri = new Uri(originalUrl);

            if (mirrorBase.Contains("https://github.com", StringComparison.OrdinalIgnoreCase)
                || mirrorBase.Contains("/github.com", StringComparison.OrdinalIgnoreCase))
            {
                var baseUri = new Uri(mirrorBase.TrimEnd('/'));
                return $"{baseUri.GetLeftPart(UriPartial.Authority)}{uri.AbsolutePath}";
            }

            if (mirrorBase.EndsWith("/packages", StringComparison.OrdinalIgnoreCase))
            {
                var baseUri = new Uri(mirrorBase);
                var pathParts = uri.AbsolutePath.Split('/');
                if (pathParts.Length >= 4)
                {
                    var packagePath = string.Join("/", pathParts.Skip(3));
                    return $"{baseUri.AbsoluteUri.TrimEnd('/')}/{packagePath}";
                }
            }

            var mirrorUri = new Uri(mirrorBase);
            var rewritten = mirrorUri.AbsoluteUri.TrimEnd('/') + uri.AbsolutePath;
            return rewritten;
        }
        catch
        {
            return originalUrl;
        }
    }

    public DomainIP? GetBestIP(string domain)
    {
        if (!_domainIPs.TryGetValue(domain, out var ips) || ips.Count == 0)
            return null;

        return ips.Where(ip => ip.IsHealthy())
                  .OrderByDescending(ip => ip.Score())
                  .FirstOrDefault();
    }

    public List<DomainIP> GetAllIPs(string domain)
    {
        return _domainIPs.TryGetValue(domain, out var ips) ? ips.ToList() : new List<DomainIP>();
    }

    public void PreSeedIPs()
    {
        var seedData = new Dictionary<string, List<(string IP, int LatencyMs)>>
        {
            ["github.com"] = new()
            {
                ("140.82.121.3", 180), ("140.82.121.4", 175), ("140.82.112.3", 190),
                ("140.82.112.4", 185), ("140.82.113.3", 195), ("140.82.113.4", 188),
                ("140.82.114.3", 200), ("140.82.114.4", 178), ("140.82.116.3", 210),
                ("140.82.116.4", 192), ("20.205.243.166", 220), ("20.205.243.165", 225),
            },
            ["huggingface.co"] = new()
            {
                ("13.225.142.71", 160), ("13.225.142.78", 155),
                ("13.225.142.36", 170), ("13.225.142.109", 165),
                ("13.225.142.40", 175),
            },
            ["docker.io"] = new()
            {
                ("34.205.13.154", 170), ("34.198.92.250", 175),
                ("3.226.152.13", 180), ("54.83.44.36", 185),
            },
            ["pypi.org"] = new()
            {
                ("151.101.0.223", 150), ("151.101.64.223", 145),
                ("151.101.128.223", 155), ("151.101.192.223", 148),
                ("2a04:4e42:1a::223", 160), ("2a04:4e42:400::223", 158),
            },
            ["google.com"] = new()
            {
                ("142.250.80.78", 100), ("142.250.80.110", 105),
                ("142.250.80.46", 110), ("142.250.80.14", 108),
                ("142.250.80.142", 112),
            },
            ["youtube.com"] = new()
            {
                ("142.250.80.14", 105), ("142.250.80.78", 100),
                ("142.250.80.46", 115), ("142.250.80.110", 108),
                ("142.250.80.142", 112),
            },
            ["stackoverflow.com"] = new()
            {
                ("151.101.1.69", 140), ("151.101.65.69", 145),
                ("151.101.129.69", 150), ("151.101.193.69", 148),
            },
            ["npmjs.com"] = new()
            {
                ("104.16.26.35", 130), ("104.16.27.35", 135),
                ("104.16.28.35", 132), ("104.16.29.35", 138),
            },
            ["rubygems.org"] = new()
            {
                ("151.101.0.70", 145), ("151.101.64.70", 150),
                ("151.101.128.70", 148), ("151.101.192.70", 152),
            },
            ["maven.org"] = new()
            {
                ("151.101.0.216", 155), ("151.101.64.216", 160),
                ("151.101.128.216", 158),
            },
            ["crates.io"] = new()
            {
                ("52.205.88.12", 165), ("3.214.53.157", 170),
                ("34.199.72.122", 168),
            },
        };

        foreach (var (domain, ipList) in seedData)
        {
            var entries = ipList.Select(ip => new DomainIP
            {
                IP = ip.IP,
                Domain = domain,
                LatencyMs = ip.LatencyMs,
                SuccessRate = 0.9,
                LastTested = DateTime.UtcNow
            }).ToList();

            _domainIPs[domain] = entries;
            _logger.LogInformation("Pre-seeded {Count} IPs for {Domain}", entries.Count, domain);
        }
    }

    public void UpdateIP(string domain, string ip, bool success, int latencyMs)
    {
        if (!_domainIPs.TryGetValue(domain, out var ips))
            return;

        var existing = ips.FirstOrDefault(e => e.IP == ip);
        if (existing is null)
        {
            ips.Add(new DomainIP
            {
                IP = ip,
                Domain = domain,
                LatencyMs = latencyMs,
                SuccessRate = success ? 0.8 : 0.3,
                LastTested = DateTime.UtcNow
            });
            return;
        }

        const double emaAlpha = 0.3;
        var newSuccessRate = emaAlpha * (success ? 1.0 : 0.0) + (1 - emaAlpha) * existing.SuccessRate;

        var updated = existing with
        {
            LatencyMs = (int)(0.5 * latencyMs + 0.5 * existing.LatencyMs),
            SuccessRate = Math.Clamp(newSuccessRate, 0.0, 1.0),
            LastTested = DateTime.UtcNow
        };

        var idx = ips.FindIndex(e => e.IP == ip);
        if (idx >= 0)
            ips[idx] = updated;
    }

    public string? GetSniOverride(string domain)
    {
        return domain switch
        {
            string d when d.Contains("github.com", StringComparison.OrdinalIgnoreCase) => "github.com",
            string d when d.Contains("huggingface", StringComparison.OrdinalIgnoreCase) => "huggingface.co",
            string d when d.Contains("docker", StringComparison.OrdinalIgnoreCase) => "registry-1.docker.io",
            string d when d.Contains("pypi", StringComparison.OrdinalIgnoreCase) => "pypi.org",
            _ => domain,
        };
    }

    public ProxyEntry? GetBestProxy()
    {
        var healthy = _proxies
            .Where(p => p.FailureCount < MaxConsecutiveFailures && p.SuccessRate >= 0.3)
            .OrderByDescending(p =>
            {
                const int maxLatency = 2000;
                double latencyNorm = Math.Clamp((double)p.LatencyMs / maxLatency, 0.0, 1.0);
                return 0.6 * (1.0 - latencyNorm) + 0.4 * p.SuccessRate;
            })
            .FirstOrDefault();

        return healthy;
    }

    public ProxyEntry? GetRandomProxy()
    {
        var healthy = _proxies
            .Where(p => p.FailureCount < MaxConsecutiveFailures && p.SuccessRate >= 0.3)
            .ToList();

        if (healthy.Count == 0)
            return null;

        return healthy[_rng.Next(healthy.Count)];
    }

    public void MarkProxySuccess(string address)
    {
        var entry = _proxies.FirstOrDefault(p => p.Address == address);
        if (entry is null)
            return;

        var healthy = _proxies.Where(p => p.Address != address).ToList();
        var updated = entry with
        {
            SuccessRate = Math.Min(1.0, entry.SuccessRate + 0.1),
            FailureCount = 0,
            LastTested = DateTime.UtcNow
        };
        healthy.Add(updated);

        _proxies.Clear();
        foreach (var p in healthy)
            _proxies.Add(p);
    }

    public void MarkProxyFailure(string address)
    {
        var entry = _proxies.FirstOrDefault(p => p.Address == address);
        if (entry is null)
            return;

        var newFailureCount = entry.FailureCount + 1;
        if (newFailureCount >= MaxConsecutiveFailures)
        {
            var remaining = _proxies.Where(p => p.Address != address).ToList();
            _proxies.Clear();
            foreach (var p in remaining)
                _proxies.Add(p);
            _logger.LogInformation("Removed proxy {Address} after {Count} consecutive failures", address, newFailureCount);
            return;
        }

        var healthy = _proxies.Where(p => p.Address != address).ToList();
        var updated = entry with
        {
            SuccessRate = Math.Max(0.0, entry.SuccessRate - 0.15),
            FailureCount = newFailureCount,
            LastTested = DateTime.UtcNow
        };
        healthy.Add(updated);

        _proxies.Clear();
        foreach (var p in healthy)
            _proxies.Add(p);
    }

    public async Task RefreshProxiesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Refreshing proxies from {Count} sources", _proxySources.Count);

        var tasks = _proxySources.Select(source => FetchProxySourceAsync(source, cancellationToken));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var allProxies = results.SelectMany(r => r).DistinctBy(p => p.Address).ToList();

        var testTasks = allProxies.Take(300).Select(async proxy =>
        {
            var (success, latencyMs) = await TestProxyAsync(proxy.Address, cancellationToken).ConfigureAwait(false);
            return proxy with
            {
                LatencyMs = success ? latencyMs : 5000,
                SuccessRate = success ? 0.5 : 0.0,
                FailureCount = success ? 0 : 1,
                LastTested = DateTime.UtcNow
            };
        });

        var tested = await Task.WhenAll(testTasks).ConfigureAwait(false);
        var valid = tested.Where(p => p.SuccessRate > 0).ToList();

        _proxies.Clear();
        foreach (var p in valid.Take(MaxProxies))
            _proxies.Add(p);

        _logger.LogInformation("Proxy refresh complete: {Valid} valid out of {Total} fetched",
            valid.Count, allProxies.Count);
    }

    private async Task<List<ProxyEntry>> FetchProxySourceAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _http.GetStringAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
            return _parseProxiesFromSource(response, sourceUrl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch proxy source {Source}", sourceUrl);
            return new List<ProxyEntry>();
        }
    }

    private List<ProxyEntry> _parseProxiesFromSource(string response, string source)
    {
        var entries = new List<ProxyEntry>();
        var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            var ipPortMatch = Regex.Match(trimmed, @"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}):(\d{2,5})");
            if (!ipPortMatch.Success)
                continue;

            var address = ipPortMatch.Value;
            var protocol = DetermineProtocol(source, trimmed);

            if (entries.Count(e => e.Address == address) == 0)
            {
                entries.Add(new ProxyEntry
                {
                    Address = address,
                    Protocol = protocol,
                    LatencyMs = 5000,
                    SuccessRate = 0.0,
                    Source = source,
                    FailureCount = 0,
                    LastTested = DateTime.UtcNow
                });
            }
        }

        return entries;
    }

    private static string DetermineProtocol(string source, string line)
    {
        if (source.Contains("socks5", StringComparison.OrdinalIgnoreCase)
            || line.Contains("socks5", StringComparison.OrdinalIgnoreCase))
            return "socks5";
        if (source.Contains("socks4", StringComparison.OrdinalIgnoreCase)
            || line.Contains("socks4", StringComparison.OrdinalIgnoreCase))
            return "socks4";
        return "http";
    }

    public async Task<(bool Success, int LatencyMs)> TestProxyAsync(string address, CancellationToken cancellationToken = default)
    {
        try
        {
            var parts = address.Split(':');
            if (parts.Length != 2)
                return (false, 0);

            var host = parts[0];
            var port = int.Parse(parts[1]);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            return (true, (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            return (false, 0);
        }
    }

    public async Task<FetchResult> ResilientFetchAsync(string url, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var uri = new Uri(url);
            var domain = uri.Host;

            var bestIP = GetBestIP(domain);
            if (bestIP is not null)
            {
                var sniOverride = GetSniOverride(domain);
                var ipResult = await FetchWithIPAsync(url, bestIP.IP, sniOverride, cancellationToken).ConfigureAwait(false);
                if (ipResult.Success)
                    return ipResult;
            }

            var mirrorResult = await FetchWithMirrorAsync(url, cancellationToken).ConfigureAwait(false);
            if (mirrorResult.Success)
                return mirrorResult;

            var proxy = GetBestProxy();
            if (proxy is not null)
            {
                var proxyResult = await FetchWithProxyAsync(url, proxy, cancellationToken).ConfigureAwait(false);
                if (proxyResult.Success)
                    return proxyResult;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            UpdateIP(domain, uri.Host, response.IsSuccessStatusCode, (int)sw.ElapsedMilliseconds);

            return new FetchResult
            {
                Success = response.IsSuccessStatusCode,
                Content = content,
                StatusCode = (int)response.StatusCode,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Resilient fetch failed for {Url}", url);
            return new FetchResult
            {
                Success = false,
                StatusCode = 0,
                LatencyMs = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<FetchResult> FetchWithIPAsync(string url, string ip, string? sniOverride, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(url);
            var rewritten = url.Replace(uri.Host, ip);

            using var request = new HttpRequestMessage(HttpMethod.Get, rewritten);
            request.Headers.Host = sniOverride ?? uri.Host;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            UpdateIP(uri.Host, ip, response.IsSuccessStatusCode, (int)sw.ElapsedMilliseconds);

            return new FetchResult
            {
                Success = response.IsSuccessStatusCode,
                Content = content,
                StatusCode = (int)response.StatusCode,
                LatencyMs = sw.ElapsedMilliseconds,
                UsedIP = ip
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IP fetch failed: {IP} for {Url}", ip, url);
            UpdateIP(new Uri(url).Host, ip, false, 5000);
            return FetchResult.Failed(0, 0);
        }
    }

    public async Task<FetchResult> FetchWithProxyAsync(string url, ProxyEntry proxy, CancellationToken cancellationToken = default)
    {
        try
        {
            var handler = new HttpClientHandler();
            handler.Proxy = new WebProxy(proxy.Address);
            handler.UseProxy = true;

            using var proxyClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await proxyClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (response.IsSuccessStatusCode)
                MarkProxySuccess(proxy.Address);
            else
                MarkProxyFailure(proxy.Address);

            return new FetchResult
            {
                Success = response.IsSuccessStatusCode,
                Content = content,
                StatusCode = (int)response.StatusCode,
                LatencyMs = sw.ElapsedMilliseconds,
                UsedProxy = proxy.Address
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Proxy fetch failed: {Proxy}", proxy.Address);
            MarkProxyFailure(proxy.Address);
            return FetchResult.Failed(0, 0);
        }
    }

    public async Task<FetchResult> FetchWithMirrorAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(url);
            var domain = uri.Host;
            var mirrors = GetAllMirrors(domain);

            foreach (var mirror in mirrors)
            {
                var mirrorUrl = _rewriteUrl(url, mirror.MirrorUrl);

                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var response = await _http.GetAsync(mirrorUrl, cancellationToken).ConfigureAwait(false);
                    var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    sw.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        return new FetchResult
                        {
                            Success = true,
                            Content = content,
                            StatusCode = (int)response.StatusCode,
                            LatencyMs = sw.ElapsedMilliseconds,
                            UsedMirror = mirror.MirrorUrl
                        };
                    }

                    _logger.LogDebug("Mirror {Mirror} returned {StatusCode} for {Url}",
                        mirror.MirrorUrl, response.StatusCode, url);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Mirror {Mirror} failed for {Url}", mirror.MirrorUrl, url);
                }
            }

            return FetchResult.Failed(0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "All mirrors failed for {Url}", url);
            return FetchResult.Failed(0, 0);
        }
    }

    public Dictionary<string, string> GetMirrorEnv()
    {
        return new Dictionary<string, string>
        {
            ["PIP_INDEX_URL"] = "https://pypi.tuna.tsinghua.edu.cn/simple",
            ["NPM_REGISTRY"] = "https://registry.npmmirror.com",
            ["HF_ENDPOINT"] = "https://hf-mirror.com",
            ["HF_MIRROR"] = "https://hf-mirror.com",
        };
    }

    public (int MirrorCount, int IPPoolSize, int ProxyCount, int HealthyProxyCount) GetStats()
    {
        var healthyProxies = _proxies.Count(p => p.FailureCount < MaxConsecutiveFailures && p.SuccessRate >= 0.3);
        var totalIPs = _domainIPs.Values.Sum(list => list.Count);

        return (_mirrors.Count, totalIPs, _proxies.Count, healthyProxies);
    }
}
