using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Providers;

public sealed record FanOutResult
{
    public string Answer { get; init; } = "";
    public string WinningProvider { get; init; } = "";
    public double LatencyMs { get; init; }
    public bool FallbackUsed { get; init; }
    public List<string> AttemptedProviders { get; init; } = new();
    public Dictionary<string, double> ProviderLatencies { get; init; } = new();
}

public sealed class ProviderFanOutRace
{
    private readonly IEnumerable<IChatClient> _clients;
    private readonly IEnumerable<string> _providerNames;
    private readonly string _primaryProvider;
    private readonly ILogger<ProviderFanOutRace>? _logger;
    private readonly ConcurrentDictionary<string, ProviderLatencyStats> _latencyStats = new();

    public ProviderFanOutRace(
        IEnumerable<IChatClient> clients,
        IEnumerable<string> providerNames,
        string primaryProvider,
        ILogger<ProviderFanOutRace>? logger = null)
    {
        var clientList = clients.ToList();
        var nameList = providerNames.ToList();

        if (clientList.Count != nameList.Count)
            throw new ArgumentException("Clients and provider names must have the same count");

        _clients = clientList;
        _providerNames = nameList;
        _primaryProvider = primaryProvider;
        _logger = logger;

        foreach (var name in nameList)
            _latencyStats.TryAdd(name, new ProviderLatencyStats());
    }

    public async Task<FanOutResult> RaceAsync(
        string prompt,
        int maxConcurrent = 3,
        CancellationToken cancellationToken = default)
    {
        var clients = _clients.ToList();
        var names = _providerNames.ToList();

        if (clients.Count == 0)
            return FailedResult("No providers available");

        var fanOutCount = Math.Min(maxConcurrent, clients.Count);
        var selected = SelectFastestProviders(fanOutCount);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var message = new ChatMessage(ChatRole.User, prompt);
        var messages = new List<ChatMessage> { message };
        var sw = Stopwatch.StartNew();
        var tasks = new List<Task<(string Provider, string Response, bool Success, double Latency)>>();

        for (var i = 0; i < selected.Count; i++)
        {
            var idx = i;
            tasks.Add(CallWithTimingAsync(selected[idx].Client, selected[idx].Name, messages, cts.Token));
        }

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            await cts.CancelAsync();

            var (provider, response, success, latency) = await completed;
            sw.Stop();

            if (success)
            {
                UpdateLatencyStats(provider, latency);

                _logger?.LogDebug("FanOutRace: {Provider} won in {Latency:F0}ms", provider, latency);

                return new FanOutResult
                {
                    Answer = response,
                    WinningProvider = provider,
                    LatencyMs = sw.Elapsed.TotalMilliseconds,
                    FallbackUsed = false,
                    AttemptedProviders = selected.Select(s => s.Name).ToList(),
                    ProviderLatencies = selected.ToDictionary(s => s.Name, _ => 0.0)
                };
            }
        }

        cts.Dispose();
        sw.Stop();

        var fallbackResult = await FallbackToPrimaryAsync(messages, cancellationToken);

        return new FanOutResult
        {
            Answer = fallbackResult.response,
            WinningProvider = _primaryProvider,
            LatencyMs = sw.Elapsed.TotalMilliseconds,
            FallbackUsed = true,
            AttemptedProviders = selected.Select(s => s.Name).ToList(),
            ProviderLatencies = selected.ToDictionary(s => s.Name, _ => 0.0)
        };
    }

    public Dictionary<string, double> GetProviderWeights()
    {
        var stats = _latencyStats.Values.ToList();
        if (stats.Count == 0 || stats.All(s => s.TotalCalls == 0))
        {
            return _providerNames.ToDictionary(n => n, _ => 1.0 / _providerNames.Count());
        }

        var minAvgLatency = stats.Where(s => s.TotalCalls > 0).Min(s => s.AvgLatencyMs);
        var weights = new Dictionary<string, double>();
        var totalWeight = 0.0;

        foreach (var (name, stat) in _latencyStats)
        {
            if (stat.TotalCalls == 0)
            {
                weights[name] = 0.5;
            }
            else
            {
                weights[name] = Math.Max(0.1, minAvgLatency / Math.Max(stat.AvgLatencyMs, 1));
            }
            totalWeight += weights[name];
        }

        foreach (var name in weights.Keys)
            weights[name] = Math.Round(weights[name] / totalWeight, 3);

        return weights;
    }

    public Dictionary<string, object> GetStats()
    {
        var stats = new Dictionary<string, object>
        {
            ["total_races"] = _latencyStats.Values.Sum(s => s.TotalCalls),
            ["providers"] = _latencyStats.Count
        };

        foreach (var (name, stat) in _latencyStats)
        {
            stats[$"{name}_avg_latency_ms"] = stat.AvgLatencyMs;
            stats[$"{name}_total_calls"] = stat.TotalCalls;
            stats[$"{name}_wins"] = stat.Wins;
        }

        return stats;
    }

    private List<(IChatClient Client, string Name)> SelectFastestProviders(int count)
    {
        var clients = _clients.ToList();
        var names = _providerNames.ToList();

        var scored = new List<(int Index, double Score)>();
        for (var i = 0; i < clients.Count; i++)
        {
            var stats = _latencyStats.GetOrAdd(names[i], _ => new ProviderLatencyStats());
            var score = stats.TotalCalls > 0 ? 1.0 / Math.Max(stats.AvgLatencyMs, 10) : 0.5;
            scored.Add((i, score));
        }

        var selected = scored.OrderByDescending(s => s.Score).Take(count)
            .Select(s => (clients[s.Index], names[s.Index]))
            .ToList();

        return selected;
    }

    private async Task<(string Provider, string Response, bool Success, double Latency)> CallWithTimingAsync(
        IChatClient client, string providerName, List<ChatMessage> messages, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client.GetResponseAsync(messages, null, ct);
            sw.Stop();
            return (providerName, response.Text ?? "", true, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return (providerName, "", false, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception)
        {
            sw.Stop();
            return (providerName, "", false, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<(string response, bool success)> FallbackToPrimaryAsync(
        List<ChatMessage> messages, CancellationToken ct)
    {
        var clients = _clients.ToList();
        var names = _providerNames.ToList();
        var primaryIdx = names.IndexOf(_primaryProvider);

        if (primaryIdx >= 0)
        {
            try
            {
                var response = await clients[primaryIdx].GetResponseAsync(messages, null, ct);
                return (response.Text ?? "", true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("FanOutRace: Primary fallback failed: {Message}", ex.Message);
            }
        }

        foreach (var (client, name) in clients.Zip(names))
        {
            if (name == _primaryProvider) continue;
            try
            {
                var response = await client.GetResponseAsync(messages, null, ct);
                return (response.Text ?? "", true);
            }
            catch
            {
            }
        }

        return ("", false);
    }

    private void UpdateLatencyStats(string provider, double latencyMs)
    {
        _latencyStats.AddOrUpdate(provider,
            _ => new ProviderLatencyStats
            {
                TotalCalls = 1,
                Wins = 1,
                TotalLatencyMs = latencyMs
            },
            (_, stats) =>
            {
                stats.TotalCalls++;
                stats.Wins++;
                stats.TotalLatencyMs += latencyMs;
                return stats;
            });
    }

    private static FanOutResult FailedResult(string reason) => new()
    {
        Answer = "",
        WinningProvider = reason,
        LatencyMs = 0,
        FallbackUsed = true
    };

    private sealed class ProviderLatencyStats
    {
        public int TotalCalls;
        public int Wins;
        public double TotalLatencyMs;
        public double AvgLatencyMs => TotalCalls > 0 ? TotalLatencyMs / TotalCalls : double.MaxValue;
    }
}
