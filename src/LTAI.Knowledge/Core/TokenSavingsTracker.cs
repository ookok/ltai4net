using System.Text.Json;

namespace LTAI.Knowledge.Core;

/// <summary>BM25 scorer result record — retained for backward compat with UnifiedBrainStore and DocumentStore.</summary>
public sealed record Bm25ScoredDoc
{
    public string Id { get; init; } = "";
    public string Content { get; init; } = "";
    public double Bm25Score { get; init; }
    public double FtsScore { get; init; }
    public double VectorScore { get; init; }
    public double RrfScore { get; init; }
    public double FinalScore { get; init; }
    public string Source { get; init; } = "";
}

public sealed class TokenSavingsTracker
{
    private static readonly string SavingsPath = Path.Combine(".livingtree", "token_savings.jsonl");
    private readonly object _lock = new();
    private long _todaySaved;
    private long _weekSaved;
    private long _totalSaved;
    private int _todayCalls;
    private int _weekCalls;
    private int _totalCalls;
    private DateTime _lastReset = DateTime.UtcNow.Date;

    public TokenSavingsTracker() { Load(); }

    public void Record(string searchQuery, long totalFileBytes, long snippetBytes, int resultCount)
    {
        var saved = (totalFileBytes - snippetBytes) / 4;
        if (saved <= 0) return;

        lock (_lock)
        {
            var today = DateTime.UtcNow.Date;
            if (_lastReset < today) { _todaySaved = 0; _todayCalls = 0; _lastReset = today; }

            _todaySaved += saved;
            _weekSaved += saved;
            _totalSaved += saved;
            _todayCalls++;
            _weekCalls++;
            _totalCalls++;

            File.AppendAllText(SavingsPath, JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("O"),
                query = searchQuery[..Math.Min(searchQuery.Length, 100)],
                totalFileBytes, snippetBytes, saved, resultCount
            }) + "\n");
        }
    }

    public TokenSavingsStats GetStats()
    {
        lock (_lock) return new TokenSavingsStats
        {
            TodaySaved = _todaySaved, WeekSaved = _weekSaved, TotalSaved = _totalSaved,
            TodayCalls = _todayCalls, WeekCalls = _weekCalls, TotalCalls = _totalCalls,
            AvgSavingRate = _totalCalls > 0 ? (double)_totalSaved / _totalCalls : 0
        };
    }

    private void Load()
    {
        if (!File.Exists(SavingsPath)) return;
        var weekAgo = DateTime.UtcNow - TimeSpan.FromDays(7);
        foreach (var line in File.ReadLines(SavingsPath))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<JsonElement>(line);
                var ts = entry.TryGetProperty("ts", out var t) ? t.GetDateTime() : DateTime.MinValue;
                var saved = entry.TryGetProperty("saved", out var s) ? s.GetInt64() : 0;
                _totalSaved += saved; _totalCalls++;
                if (ts > weekAgo) { _weekSaved += saved; _weekCalls++; }
                if (ts.Date == DateTime.UtcNow.Date) { _todaySaved += saved; _todayCalls++; }
            }
            catch { }
        }
    }
}

public sealed class TokenSavingsStats
{
    public long TodaySaved { get; init; }
    public long WeekSaved { get; init; }
    public long TotalSaved { get; init; }
    public int TodayCalls { get; init; }
    public int WeekCalls { get; init; }
    public int TotalCalls { get; init; }
    public double AvgSavingRate { get; init; }
}
