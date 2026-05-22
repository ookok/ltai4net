using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using LTAI.Agent.Models;

namespace LTAI.Agent.Session;

public sealed class ConnectionPool : IDisposable
{
    private readonly PoolConfig _config;
    private HttpClient _client;
    private readonly ConcurrentDictionary<string, ProviderPoolStats> _providerStats = new();
    private readonly object _lock = new();
    private readonly ILogger<ConnectionPool>? _logger;
    private readonly ConcurrentQueue<double> _latencyRing = new();
    private readonly ConcurrentQueue<bool> _reuseRing = new();
    private int _recreateCount;
    private int _consecutiveErrors;
    private DateTime _lastRecreate = DateTime.UtcNow;
    private bool _disposed;

    private const int RING_SIZE = 200;
    private const double BACKOFF_BASE_MS = 500;
    private const double BACKOFF_MAX_MS = 10000;
    private const int MAX_CONSECUTIVE_ERRORS = 5;

    public ConnectionPool(PoolConfig? config = null, ILogger<ConnectionPool>? logger = null)
    {
        _config = config ?? new PoolConfig();
        _logger = logger;
        _client = CreateSession();
    }

    public HttpClient GetClient()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPool));
        return _client;
    }

    private HttpClient CreateSession()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = _config.MaxConnectionsPerHost,
            PooledConnectionLifetime = TimeSpan.FromSeconds(_config.KeepaliveTimeoutSeconds),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(_config.KeepaliveTimeoutSeconds),
            AllowAutoRedirect = true
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    private async Task RecreateSession()
    {
        lock (_lock)
        {
            _recreateCount++;
            var backoffMs = Math.Min(BACKOFF_BASE_MS * Math.Pow(2, _consecutiveErrors), BACKOFF_MAX_MS);
            var elapsed = (DateTime.UtcNow - _lastRecreate).TotalMilliseconds;
            if (elapsed < backoffMs) return;
            _lastRecreate = DateTime.UtcNow;

            try { _client.CancelPendingRequests(); } catch { /* non-fatal */ }
            _client.Dispose();
            _client = CreateSession();
        }

        await Task.Delay(100);
    }

    public async Task<(int StatusCode, string Body, double LatencyMs)> RequestAsync(
        string providerName, HttpMethod method, string url,
        Dictionary<string, string>? headers = null, string? jsonPayload = null, int timeoutMs = 30000)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPool));

        var stats = _providerStats.GetOrAdd(providerName, _ => new ProviderPoolStats { Provider = providerName });
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var client = _client;
            if (client == null)
            {
                await RecreateSession();
                client = _client;
                if (client == null) return (503, "", 0);
            }

            var request = new HttpRequestMessage(method, url);

            if (headers != null)
                foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);

            if (jsonPayload != null)
                request.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(timeoutMs);
            var response = await client.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            sw.Stop();
            var latencyMs = sw.Elapsed.TotalMilliseconds;

            stats.Requests++;
            stats.Latencies.Add(latencyMs);
            if (stats.Latencies.Count > RING_SIZE) stats.Latencies.RemoveAt(0);

            _latencyRing.Enqueue(latencyMs);
            while (_latencyRing.Count > RING_SIZE) _latencyRing.TryDequeue(out _);
            _reuseRing.Enqueue(true);
            while (_reuseRing.Count > RING_SIZE) _reuseRing.TryDequeue(out _);

            Interlocked.Exchange(ref _consecutiveErrors, 0);

            return ((int)response.StatusCode, body, latencyMs);
        }
        catch (Exception ex)
        {
            sw.Stop();
            stats.Requests++;
            stats.Failures++;
            stats.ErrorFlags.Add(true);
            if (stats.ErrorFlags.Count > 100) stats.ErrorFlags.RemoveAt(0);

            var errors = Interlocked.Increment(ref _consecutiveErrors);
            if (errors >= MAX_CONSECUTIVE_ERRORS)
            {
                _logger?.LogWarning("ConnectionPool: {Provider} has {Errors} consecutive errors, recreating session",
                    providerName, errors);
                await RecreateSession();
                Interlocked.Exchange(ref _consecutiveErrors, 0);
            }

            _logger?.LogDebug("ConnectionPool: {Provider} request failed: {Message}", providerName, ex.Message);
            return (0, "", sw.Elapsed.TotalMilliseconds);
        }
    }

    public async IAsyncEnumerable<string> StreamRequestAsync(
        string providerName, string url,
        Dictionary<string, string>? headers = null, string? jsonPayload = null, int timeoutMs = 120000)
    {
        if (_disposed) yield break;

        var client = _client;
        if (client == null) yield break;

        var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (headers != null)
            foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);

        if (jsonPayload != null)
            request.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(timeoutMs);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0) break;
            yield return new string(buffer, 0, read);
        }
    }

    public async Task WarmupAsync(List<(string Provider, string BaseUrl)> providers)
    {
        var tasks = providers.Select(async p =>
        {
            try
            {
                var url = $"{p.BaseUrl}/models";
                var result = await RequestAsync(p.Provider, HttpMethod.Get, url, timeoutMs: 5000);
                _logger?.LogDebug("ConnectionPool: Warmed up {Provider} ({Code})", p.Provider, result.StatusCode);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("ConnectionPool: Warmup failed for {Provider}: {Message}", p.Provider, ex.Message);
            }
        });

        await Task.WhenAll(tasks);
    }

    public PoolStats GetStats()
    {
        int active = 0, idle = 0;
        try
        {
            if (_client != null)
            {
                var handler = _client.GetType().GetField("_handler",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                    .GetValue(_client);
                if (handler != null)
                {
                    var pool = handler.GetType().GetProperty("Pool",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
                        .GetValue(handler);
                    if (pool != null)
                    {
                        var activeProp = pool.GetType().GetProperty("TotalConnectionCount")?.GetValue(pool);
                        if (activeProp is int ac) active = ac;
                    }
                }
            }
        }
        catch { /* non-fatal */ }

        return new PoolStats
        {
            ActiveConnections = active,
            IdleConnections = idle,
            TotalRequests = _providerStats.Values.Sum(s => s.Requests),
            TotalFailures = _providerStats.Values.Sum(s => s.Failures),
            AvgLatencyMs = _latencyRing.Count > 0 ? _latencyRing.Average() : 0,
            ReusedRatio = _reuseRing.Count > 0 ? (double)_reuseRing.Count(r => r) / _reuseRing.Count : 0,
            Recreations = _recreateCount
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _client.CancelPendingRequests(); } catch { /* non-fatal */ }
        _client.Dispose();
    }
}
