using System.Collections.Concurrent;
using System.Text.Json;

namespace LTAI.Agent.Resilience;

public sealed record FixInstinct
{
    public string DiagnosticCode { get; init; } = "";
    public string Pattern { get; set; } = "";
    public string Strategy { get; set; } = "";
    public int SuccessCount { get; set; }
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    public List<string> ExampleFiles { get; init; } = [];
}

public static class FixInstinctStore
{
    private static readonly ConcurrentDictionary<string, FixInstinct> _instincts = new();
    private static readonly ConcurrentDictionary<string, int> _attemptCounts = new();
    private static readonly ConcurrentDictionary<string, int> _successCounts = new();
    private static int _totalFixes, _totalAttempts, _emptyPatches;
    private static readonly object _healthLock = new();
    private static readonly string PersistPath = Path.Combine(AppContext.BaseDirectory, ".livingtree", "fix_instincts.json");

    static FixInstinctStore()
    {
        Load();
    }

    public static void RecordAttempt(string diagnosticCode)
    {
        lock (_healthLock) { _totalAttempts++; }
        _attemptCounts.AddOrUpdate(diagnosticCode, 1, (_, c) => c + 1);
    }

    public static void RecordSuccess(string diagnosticCode, string fixPattern, string strategy, string filePath)
    {
        lock (_healthLock) { _totalFixes++; }

        _successCounts.AddOrUpdate(diagnosticCode, 1, (_, c) => c + 1);

        var instinct = _instincts.GetOrAdd(diagnosticCode, _ => new FixInstinct
        {
            DiagnosticCode = diagnosticCode,
            Pattern = fixPattern,
            Strategy = strategy
        });

        lock (instinct)
        {
            instinct.SuccessCount++;
            instinct.LastUsed = DateTime.UtcNow;
            instinct.Pattern = fixPattern;
            instinct.Strategy = strategy;
            if (!instinct.ExampleFiles.Contains(filePath))
                instinct.ExampleFiles.Add(filePath);
            if (instinct.ExampleFiles.Count > 10)
                instinct.ExampleFiles.RemoveAt(0);
        }

        if (_instincts.Count % 5 == 0) Save();
    }

    public static void RecordEmptyPatch()
    {
        lock (_healthLock) { _emptyPatches++; }
    }

    public static string? GetInstinct(string diagnosticCode)
    {
        if (!_instincts.TryGetValue(diagnosticCode, out var instinct)) return null;
        if (instinct.SuccessCount < 3) return null;

        return $"[Instinct: {instinct.SuccessCount}x success] Strategy: {instinct.Strategy}\nPattern: {instinct.Pattern}";
    }

    public static string GetContextForDiagnostic(string diagnosticCode)
    {
        var sb = new System.Text.StringBuilder();
        var instinct = GetInstinct(diagnosticCode);
        if (instinct != null)
        {
            sb.AppendLine("Previous successful fixes for this error type:");
            sb.AppendLine(instinct);
        }

        var attemptCount = _attemptCounts.GetValueOrDefault(diagnosticCode, 0);
        var successCount = _successCounts.GetValueOrDefault(diagnosticCode, 0);
        if (attemptCount > 0)
        {
            var rate = (double)successCount / attemptCount;
            sb.AppendLine($"Historical fix rate for {diagnosticCode}: {successCount}/{attemptCount} ({rate:P0})");
        }

        return sb.ToString();
    }

    public static double GetHealthScore()
    {
        lock (_healthLock)
        {
            if (_totalAttempts == 0) return 100;
            var successRate = (double)_totalFixes / _totalAttempts;
            var emptyRate = (double)_emptyPatches / Math.Max(_totalAttempts, 1);
            var score = (successRate * 70) + ((1 - emptyRate) * 30);
            return Math.Clamp(score * 100, 0, 100);
        }
    }

    public static Dictionary<string, object> GetStats()
    {
        lock (_healthLock)
        {
            return new()
            {
                ["health_score"] = GetHealthScore(),
                ["total_fixes"] = _totalFixes,
                ["total_attempts"] = _totalAttempts,
                ["empty_patches"] = _emptyPatches,
                ["instincts_count"] = _instincts.Count,
                ["top_instincts"] = _instincts.Values
                    .OrderByDescending(i => i.SuccessCount)
                    .Take(5)
                    .Select(i => new { i.DiagnosticCode, i.SuccessCount, i.Strategy })
                    .ToList()
            };
        }
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(PersistPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var data = new
            {
                instincts = _instincts.ToDictionary(kv => kv.Key, kv => new
                {
                    kv.Value.DiagnosticCode, kv.Value.Pattern, kv.Value.Strategy,
                    kv.Value.SuccessCount, LastUsed = kv.Value.LastUsed.ToString("O")
                }),
                total_fixes = _totalFixes, total_attempts = _totalAttempts, empty_patches = _emptyPatches
            };
            File.WriteAllText(PersistPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FixInstinctStore save failed: {ex.Message}");
        }
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(PersistPath)) return;
            var json = File.ReadAllText(PersistPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("total_fixes", out var tf))
                lock (_healthLock) { _totalFixes = tf.GetInt32(); }
            if (root.TryGetProperty("total_attempts", out var ta))
                lock (_healthLock) { _totalAttempts = ta.GetInt32(); }
            if (root.TryGetProperty("empty_patches", out var ep))
                lock (_healthLock) { _emptyPatches = ep.GetInt32(); }

            if (root.TryGetProperty("instincts", out var instincts))
            {
                foreach (var prop in instincts.EnumerateObject())
                {
                    var i = prop.Value;
                    _instincts[prop.Name] = new FixInstinct
                    {
                        DiagnosticCode = i.GetProperty("DiagnosticCode").GetString() ?? prop.Name,
                        Pattern = i.TryGetProperty("Pattern", out var p) ? p.GetString() ?? "" : "",
                        Strategy = i.TryGetProperty("Strategy", out var s) ? s.GetString() ?? "" : "",
                        SuccessCount = i.TryGetProperty("SuccessCount", out var sc) ? sc.GetInt32() : 0,
                        LastUsed = i.TryGetProperty("LastUsed", out var lu) && DateTime.TryParse(lu.GetString(), out var d) ? d : DateTime.UtcNow
                    };
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FixInstinctStore load failed: {ex.Message}");
        }
    }
}
