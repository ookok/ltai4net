using LTAI.Core.Messaging;

namespace LTAI.AI.Providers;

public sealed class ProviderHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _chatPath;

    public ProviderHttpClient(HttpClient http, string baseUrl, string apiKey, string chatPath)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _chatPath = chatPath;
    }

    public async Task<HttpResponseMessage> PostAsync(string jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{_chatPath}")
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    public void Dispose() => _http?.Dispose();
}

public sealed class StreamParser
{
    public static async IAsyncEnumerable<string> ParseStreamAsync(
        HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") yield break;

            var chunk = ParseChunk(data);
            if (chunk != null) yield return chunk;
        }
    }

    private static string? ParseChunk(string data)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var content))
                    return content.GetString();
            }
        }
        catch { }
        return null;
    }
}

public sealed class BudgetManager
{
    private decimal _dailySpent;
    private DateTime _lastResetUtc = DateTime.UtcNow.Date;
    private readonly object _lock = new();
    private readonly decimal _budgetUsd;

    public decimal Spent => _dailySpent;
    public decimal Remaining => _budgetUsd - _dailySpent;

    public BudgetManager(decimal dailyBudgetUsd)
    {
        _budgetUsd = dailyBudgetUsd;
    }

    public void CheckBudget()
    {
        if (_budgetUsd <= 0) return;
        lock (_lock)
        {
            var today = DateTime.UtcNow.Date;
            if (_lastResetUtc < today) { _dailySpent = 0; _lastResetUtc = today; }
            if (_dailySpent >= _budgetUsd)
                throw new InvalidOperationException($"Daily budget exceeded: {_dailySpent:F2}/{_budgetUsd} USD");
        }
    }

    public void RecordTokens(int tokens, double costPer1K = 0.002)
    {
        var cost = tokens / 1000.0 * costPer1K;
        lock (_lock) { _dailySpent += (decimal)cost; }
    }
}

public sealed class ReliabilityLayer
{
    private int _consecutiveFailures;
    private DateTime _circuitOpenUntil;
    private const int MaxRetries = 3;
    private const int CircuitThreshold = 5;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, string prompt, string operation, CancellationToken ct)
    {
        if (DateTime.UtcNow < _circuitOpenUntil)
            throw new InvalidOperationException($"Circuit breaker open until {_circuitOpenUntil:O}");

        Exception? lastEx = null;
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var result = await action(ct);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                if (failures >= CircuitThreshold)
                {
                    _circuitOpenUntil = DateTime.UtcNow + Cooldown;
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    throw new InvalidOperationException($"Circuit breaker open: {ex.Message}", ex);
                }
                if (attempt < MaxRetries)
                {
                    await Task.Delay(200 * (int)Math.Pow(2, attempt - 1), ct);
                }
            }
        }
        throw lastEx!;
    }
}
