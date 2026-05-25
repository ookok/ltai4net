using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.Infra.Network.Infrastructure;

public sealed class SmartDnsResolver
{
    private readonly ConcurrentDictionary<string, (IPAddress[] Addresses, DateTime ExpiresAt)> _cache = new();
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<SmartDnsResolver> _logger;

    public SmartDnsResolver(ILogger<SmartDnsResolver> logger, TimeSpan? cacheTtl = null)
    {
        _logger = logger;
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5);
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(host, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
        {
            _logger.LogDebug("DNS cache hit: {Host}", host);
            return entry.Addresses;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            _cache[host] = (addresses, DateTime.UtcNow + _cacheTtl);
            _logger.LogInformation("DNS resolved: {Host} -> {Count} addresses", host, addresses.Length);
            return addresses;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DNS resolve failed: {Host}", host);

            if (_cache.TryGetValue(host, out var stale))
            {
                _logger.LogInformation("Using stale DNS cache for: {Host}", host);
                return stale.Addresses;
            }

            throw;
        }
    }

    public void Invalidate(string host)
    {
        _cache.TryRemove(host, out _);
        _logger.LogDebug("DNS cache invalidated: {Host}", host);
    }

    public IReadOnlyDictionary<string, int> GetCacheStats()
    {
        return _cache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Addresses.Length);
    }
}

public sealed class ProxyPool
{
    private readonly List<string> _proxies;
    private readonly HttpClient _http;
    private readonly ILogger<ProxyPool> _logger;
    private int _roundRobinIndex;

    public ProxyPool(IEnumerable<string> proxies, ILogger<ProxyPool> logger)
    {
        _proxies = proxies.ToList();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
    }

    public string? GetNextProxy()
    {
        if (_proxies.Count == 0) return null;

        var index = Interlocked.Increment(ref _roundRobinIndex) % _proxies.Count;
        var proxy = _proxies[Math.Abs(index)];
        _logger.LogDebug("Round-robin proxy: {Proxy}", proxy);
        return proxy;
    }

    public async Task<bool> ValidateProxyAsync(string proxy, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.GetAsync("http://httpbin.org/ip", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public int Count => _proxies.Count;
}
