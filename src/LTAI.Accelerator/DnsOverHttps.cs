using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LTAI.Accelerator;

public sealed class DnsOverHttps : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, DnsCacheEntry> _cache = new();

    public DnsOverHttps()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://1.1.1.1"),
            DefaultRequestHeaders = { { "accept", "application/dns-json" } },
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var ip))
            return [ip];

        var key = host.ToLowerInvariant();
        if (_cache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.UtcNow)
            return entry.Addresses;

        try
        {
            // Periodic cache eviction
            if (_cache.Count > 1000)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in _cache)
                    if (kv.Value.Expiry < now) _cache.TryRemove(kv.Key, out _);
            }

            var resp = await _http.GetFromJsonAsync<DnsJsonResponse>(
                $"/dns-query?name={Uri.EscapeDataString(host)}&type=A&ct=application/dns-json", ct);

            if (resp?.Answer == null || resp.Answer.Count == 0)
            {
                var v6resp = await _http.GetFromJsonAsync<DnsJsonResponse>(
                    $"/dns-query?name={Uri.EscapeDataString(host)}&type=AAAA&ct=application/dns-json", ct);
                if (v6resp?.Answer != null && v6resp.Answer.Count > 0)
                    return CacheResult(key, v6resp);
                return [];
            }

            return CacheResult(key, resp);
        }
        catch
        {
            return [];
        }
    }

    private IPAddress[] CacheResult(string key, DnsJsonResponse resp)
    {
        var ips = resp.Answer!
            .Where(a => a.Type == 1 || a.Type == 28)
            .Select(a => IPAddress.TryParse(a.Data, out var addr) ? addr : null)
            .Where(a => a != null)
            .Cast<IPAddress>()
            .ToArray();

        if (ips.Length == 0) return [];

        var ttl = Math.Max(resp.Answer!.Min(a => a.TTL), 60);
        _cache[key] = new DnsCacheEntry(ips, DateTime.UtcNow.AddSeconds(ttl));
        return ips;
    }

    public void ClearCache() => _cache.Clear();

    public void Dispose() => _http.Dispose();

    private sealed record DnsCacheEntry(IPAddress[] Addresses, DateTime Expiry);

    private sealed class DnsJsonResponse
    {
        [JsonPropertyName("Status")] public int Status { get; set; }
        [JsonPropertyName("Answer")] public List<DnsAnswer>? Answer { get; set; }
    }

    private sealed class DnsAnswer
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("type")] public int Type { get; set; }
        [JsonPropertyName("TTL")] public int TTL { get; set; }
        [JsonPropertyName("data")] public string Data { get; set; } = "";
    }
}
