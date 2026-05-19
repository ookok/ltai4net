using LTAI.DNA.Models;

namespace LTAI.DNA.Meta;

public sealed class MetaMemory
{
    private readonly List<StrategyRecord> _records = new();
    private readonly List<(string name, bool recommended, bool success, string domain, DateTime time)> _gatingRecords = new();
    private readonly List<(string tool, string category, bool success, string sessionId, DateTime time)> _toolEvents = new();
    private readonly object _lock = new();

    public StrategyRecord Record(string strategyType, string strategyName, string domain, bool success,
        int tokensUsed, long timeSpentMs, double fitnessDelta, string? targetFile = null,
        string? context = null, string? notes = null)
    {
        var record = new StrategyRecord
        {
            StrategyType = strategyType,
            StrategyName = strategyName,
            Domain = domain,
            Context = context ?? domain,
            Success = success,
            FitnessDelta = fitnessDelta,
            TokensUsed = tokensUsed,
            TimeSpentMs = timeSpentMs,
            TargetFile = targetFile
        };

        lock (_lock)
        {
            _records.Add(record);
            if (_records.Count > 1000) _records.RemoveAt(0);
        }

        return record;
    }

    public List<Dictionary<string, object>> Recommend(string strategyType, string domain, int top = 3)
    {
        lock (_lock)
        {
            var matching = _records
                .Where(r => r.StrategyType == strategyType && r.Domain == domain)
                .GroupBy(r => r.StrategyName)
                .Select(g => new Dictionary<string, object>
                {
                    ["name"] = g.Key,
                    ["success_rate"] = g.Average(r => r.Success ? 1.0 : 0.0),
                    ["samples"] = g.Count(),
                    ["confidence"] = Math.Min(1.0, g.Count() / 10.0),
                    ["avg_fitness_delta"] = g.Average(r => r.FitnessDelta),
                    ["avg_tokens"] = g.Average(r => r.TokensUsed),
                    ["last_used"] = g.Max(r => r.CreatedAt).ToString("O")
                })
                .OrderByDescending(r => (double)r["success_rate"])
                .Take(top)
                .ToList();

            return matching;
        }
    }

    public string RecommendMutationDirection(string domain)
    {
        var mutations = new[] { "increase", "decrease", "new", "combine" };
        var scored = mutations
            .Select(m => new { direction = m, rate = GetSuccessRate("mutation", m, domain) })
            .OrderByDescending(x => x.rate)
            .ToList();

        return scored.FirstOrDefault(x => x.rate > 0)?.direction ?? mutations[Random.Shared.Next(mutations.Length)];
    }

    public double BestTemperature(string domain)
    {
        var records = _records
            .Where(r => r.Domain == domain && r.Success)
            .ToList();
        return records.Count > 0 ? records.Average(r => r.FitnessDelta * 0.7) : 0.7;
    }

    public List<string> UnderperformingStrategies(string domain, double threshold = 0.3)
    {
        return _records
            .Where(r => r.Domain == domain)
            .GroupBy(r => r.StrategyName)
            .Where(g => g.Average(r => r.Success ? 1.0 : 0.0) < threshold)
            .Select(g => g.Key)
            .Distinct()
            .ToList();
    }

    public List<Dictionary<string, object>> CrossDomainStrategies(string targetDomain, string sourceDomain)
    {
        var successful = _records
            .Where(r => r.Domain == sourceDomain && r.Success)
            .GroupBy(r => r.StrategyName)
            .Where(g => g.Average(x => x.Success ? 1.0 : 0.0) > 0.6)
            .Select(g => g.Key)
            .ToList();

        return successful.Select(s => new Dictionary<string, object>
        {
            ["strategy"] = s,
            ["source_domain"] = sourceDomain,
            ["target_domain"] = targetDomain,
            ["source_success_rate"] = GetSuccessRate("*", s, sourceDomain)
        }).ToList();
    }

    private double GetSuccessRate(string type, string name, string domain)
    {
        var relevant = _records
            .Where(r => r.Domain == domain && (type == "*" || r.StrategyType == type) && r.StrategyName == name)
            .ToList();
        return relevant.Count > 0 ? relevant.Average(r => r.Success ? 1.0 : 0.0) : 0;
    }

    public void RecordGating(string strategyName, bool recommendedAsAppropriate, bool actualSuccess, string domain)
    {
        lock (_lock)
        {
            _gatingRecords.Add((strategyName, recommendedAsAppropriate, actualSuccess, domain, DateTime.UtcNow));
            if (_gatingRecords.Count > 500) _gatingRecords.RemoveAt(0);
        }
    }

    public (double precision, double recall, double calibration) GatingCalibration()
    {
        lock (_lock)
        {
            if (_gatingRecords.Count == 0) return (0.5, 0.5, 0.5);
            int tp = _gatingRecords.Count(r => r.recommended && r.success);
            int fp = _gatingRecords.Count(r => r.recommended && !r.success);
            int fn = _gatingRecords.Count(r => !r.recommended && r.success);
            double precision = tp + fp > 0 ? (double)tp / (tp + fp) : 0;
            double recall = tp + fn > 0 ? (double)tp / (tp + fn) : 0;
            double calibration = _gatingRecords.Count(r => r.recommended == r.success) /
                                 (double)_gatingRecords.Count;
            return (precision, recall, calibration);
        }
    }

    public List<string> MisgatedStrategies(int minSamples = 3)
    {
        return _gatingRecords
            .GroupBy(r => r.name)
            .Where(g =>
                g.Count() >= minSamples && g.Count(r => r.recommended != r.success) > g.Count() * 0.3)
            .Select(g => g.Key)
            .ToList();
    }

    public Dictionary<string, object> StrategyDecayTracker(string strategyName)
    {
        var records = _records
            .Where(r => r.StrategyName == strategyName)
            .OrderBy(r => r.CreatedAt)
            .ToList();

        var half = records.Count / 2;
        var early = records.Take(Math.Max(1, half))
            .Average(r => r.Success ? 1.0 : 0.0);
        var late = records.Skip(half)
            .DefaultIfEmpty(records.LastOrDefault())
            .Average(r => r?.Success == true ? 1.0 : 0.0);

        return new Dictionary<string, object>
        {
            ["strategy"] = strategyName,
            ["early_success_rate"] = early,
            ["late_success_rate"] = late,
            ["decay"] = early - late,
            ["total_samples"] = records.Count
        };
    }

    public void RecordToolEvent(string toolName, string category, bool success, string sessionId)
    {
        lock (_lock)
        {
            _toolEvents.Add((toolName, category, success, sessionId, DateTime.UtcNow));
            if (_toolEvents.Count > 500) _toolEvents.RemoveAt(0);
        }
    }

    public string CategorizeTool(string toolName)
    {
        return toolName.ToLowerInvariant() switch
        {
            var t when t.Contains("file") || t.Contains("read") || t.Contains("write") || t.Contains("edit") => "file",
            var t when t.Contains("git") || t.Contains("commit") || t.Contains("push") || t.Contains("pull") => "git",
            var t when t.Contains("build") || t.Contains("dotnet") || t.Contains("npm") || t.Contains("python") => "task",
            var t when t.Contains("err") || t.Contains("except") || t.Contains("catch") => "error",
            var t when t.Contains("env") || t.Contains("config") || t.Contains("path") => "env",
            var t when t.Contains("mcp") || t.Contains("protocol") => "mcp",
            _ => "generic"
        };
    }

    public Dictionary<string, object> GetToolStats(string sessionId)
    {
        lock (_lock)
        {
            var relevant = _toolEvents.Where(t => t.sessionId == sessionId).ToList();
            return new Dictionary<string, object>
            {
                ["total_calls"] = relevant.Count,
                ["success_rate"] = relevant.Count > 0 ? relevant.Average(t => t.success ? 1.0 : 0.0) : 0,
                ["by_category"] = relevant.GroupBy(t => t.category)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ["by_tool"] = relevant.GroupBy(t => t.tool)
                    .ToDictionary(g => g.Key, g => g.Count())
            };
        }
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_records"] = _records.Count,
                ["total_gating"] = _gatingRecords.Count,
                ["total_tool_events"] = _toolEvents.Count,
                ["gating_calibration"] = GatingCalibration(),
                ["domains"] = _records.Select(r => r.Domain).Distinct().Count(),
                ["strategies"] = _records.Select(r => r.StrategyName).Distinct().Count()
            };
        }
    }
}
