using LTAI.DNA.Models;

namespace LTAI.DNA.Meta;

public sealed class MetaOptimizer
{
    private readonly Dictionary<string, ParamConfig> _params = new();
    private readonly HashSet<string> _domainsSeen = new();
    private string _domain = "general";
    private int _tuningEvents;
    private readonly object _lock = new();

    private static readonly Dictionary<string, double> Defaults = new()
    {
        ["mempo_alpha"] = 0.1,
        ["surprise_threshold"] = 0.4,
        ["habit_confidence_min"] = 0.7,
        ["exploration_rate"] = 0.15,
        ["temperature"] = 0.7,
        ["context_compression_ratio"] = 0.5,
        ["curiosity_bonus"] = 0.05,
        ["safety_margin"] = 0.3,
        ["learning_rate"] = 0.01,
        ["dopamine_decay"] = 0.95
    };

    public MetaOptimizer()
    {
        foreach (var (name, value) in Defaults)
            _params[name] = new ParamConfig { ParamName = name, CurrentValue = value };
    }

    public void RegisterParam(string name, double initialValue)
    {
        lock (_lock)
        {
            if (!_params.ContainsKey(name))
                _params[name] = new ParamConfig { ParamName = name, CurrentValue = initialValue };
        }
    }

    public void RecordPerformance(string paramName, double value, double performanceDelta, string? domain = null)
    {
        var d = domain ?? _domain;
        lock (_lock)
        {
            if (_params.TryGetValue(paramName, out var param))
                param.Record(value, performanceDelta, d);
            _domainsSeen.Add(d);
            _tuningEvents++;
        }
    }

    public double SuggestValue(string paramName, string? domain = null)
    {
        var d = domain ?? _domain;
        lock (_lock)
        {
            if (!_params.TryGetValue(paramName, out var param))
                return Defaults.GetValueOrDefault(paramName, 0.5);

            var topValues = param.TopKValues(d, k: 5);
            if (topValues.Count == 0) return param.CurrentValue;

            return topValues[0];
        }
    }

    public Dictionary<string, double> AutoTune(string? domain = null)
    {
        var d = domain ?? _domain;
        lock (_lock) { _domain = d; }

        var result = new Dictionary<string, double>();
        foreach (var (name, _) in _params)
            result[name] = SuggestValue(name, d);
        return result;
    }

    public void SetDomain(string domain)
    {
        lock (_lock) { _domain = domain; }
    }

    public Dictionary<string, object> IntrospectDomain(List<string> recentQueries)
    {
        var keywords = new Dictionary<string, string[]>
        {
            ["code"] = new[] { "code", "function", "bug", "refactor", "test", "debug", "compile" },
            ["analysis"] = new[] { "analyze", "evaluate", "assess", "review", "audit", "report" },
            ["creative"] = new[] { "create", "design", "generate", "write", "imagine", "story" },
            ["decision"] = new[] { "decide", "choose", "compare", "recommend", "best", "option" }
        };

        var scores = new Dictionary<string, int>
        {
            ["code"] = 0, ["analysis"] = 0, ["creative"] = 0, ["decision"] = 0
        };

        foreach (var query in recentQueries)
        foreach (var (domain, words) in keywords)
            foreach (var word in words)
                if (query.Contains(word, StringComparison.OrdinalIgnoreCase))
                    scores[domain]++;

        var best = scores.OrderByDescending(kv => kv.Value).First();
        return new Dictionary<string, object>
        {
            ["detected_domain"] = best.Key,
            ["confidence"] = Math.Min(1.0, best.Value / 5.0),
            ["scores"] = scores
        };
    }

    public Dictionary<string, object> Stats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["current_domain"] = _domain,
                ["domains_seen"] = _domainsSeen.Count,
                ["tuning_events"] = _tuningEvents,
                ["param_count"] = _params.Count,
                ["params"] = _params.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentValue)
            };
        }
    }
}
