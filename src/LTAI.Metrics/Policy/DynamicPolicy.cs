using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LTAI.Metrics.Policy;

public enum SignalPriority
{
    Critical,
    High,
    Medium,
    Low,
    Observe
}

public enum SignalType
{
    ProxyLatency,
    ProxySuccessRate,
    BandwidthBytes,
    ConcurrentConnections,
    CpuUsage,
    MemoryUsage,
    EstimatedCost,
    DailyBudgetRemaining,
    ConsecutiveFailures,
    ErrorRate,
    CacheHitRate,
    ActiveProxies,
    ProxyPoolHealth,
    BanditExploration,
    QuicActive,
    ProtocolSuccessRate
}

public sealed record Signal(SignalType Type, double Value, DateTime Timestamp, string? Source, string? Unit, double ThresholdWarn, double ThresholdCritical)
{
    public bool IsWarning => Value > ThresholdWarn;
    public bool IsCritical => Value > ThresholdCritical;
}

public sealed record DslRule(string Name, List<string> Conditions, List<string> Actions, string Logic, int Priority, double CooldownSeconds, DateTime? LastTriggered, int TriggerCount, bool Enabled)
{
    public bool OnCooldown => LastTriggered != null && (DateTime.UtcNow - LastTriggered.Value).TotalSeconds < CooldownSeconds;
}

public sealed record AbExperiment(string Id, string Name, string StrategyA, string StrategyB, string Metric, int SamplesA, int SamplesB, double SuccessRateA, double SuccessRateB, DateTime CreatedAt, bool Declared, string? Winner);

public sealed class SignalBus
{
    private static readonly Lazy<SignalBus> _instance = new(() => new SignalBus());
    public static SignalBus Instance => _instance.Value;

    private readonly ConcurrentDictionary<SignalType, List<double>> _signalValues = new();
    private readonly ConcurrentDictionary<SignalType, List<Action<Signal>>> _subscribers = new();
    private const int MaxValues = 100;

    private SignalBus() { }

    public void Emit(SignalType type, double value, string? source = null, string? unit = null, double warnThreshold = 0, double critThreshold = 0)
    {
        var values = _signalValues.GetOrAdd(type, _ => new List<double>());
        lock (values)
        {
            values.Add(value);
            while (values.Count > MaxValues)
                values.RemoveAt(0);
        }

        var signal = new Signal(type, value, DateTime.UtcNow, source, unit, warnThreshold, critThreshold);

        if (_subscribers.TryGetValue(type, out var subscribers))
        {
            foreach (var cb in subscribers)
            {
                try { cb(signal); }
                catch { /* non-fatal */ }
            }
        }
    }

    public double Get(SignalType type)
    {
        if (_signalValues.TryGetValue(type, out var values))
        {
            lock (values)
            {
                return values.Count > 0 ? values[^1] : 0;
            }
        }
        return 0;
    }

    public Dictionary<string, object> GetStats(SignalType type)
    {
        var result = new Dictionary<string, object>
        {
            ["mean"] = 0.0,
            ["min"] = 0.0,
            ["max"] = 0.0,
            ["std"] = 0.0,
            ["count"] = 0,
            ["trend"] = "stable"
        };

        if (!_signalValues.TryGetValue(type, out var values))
            return result;

        double[] snapshot;
        lock (values)
        {
            snapshot = values.ToArray();
        }

        if (snapshot.Length == 0)
            return result;

        double mean = snapshot.Average();
        double min = snapshot.Min();
        double max = snapshot.Max();
        double variance = snapshot.Select(v => (v - mean) * (v - mean)).Sum() / snapshot.Length;
        double std = Math.Sqrt(variance);
        int count = snapshot.Length;

        string trend = "stable";
        if (snapshot.Length >= 2)
        {
            double first = snapshot.Take(snapshot.Length / 2).Average();
            double second = snapshot.Skip(snapshot.Length / 2).Average();
            double diff = second - first;
            if (diff > 0.01 * Math.Abs(mean) + 0.001)
                trend = "up";
            else if (diff < -(0.01 * Math.Abs(mean) + 0.001))
                trend = "down";
        }

        result["mean"] = mean;
        result["min"] = min;
        result["max"] = max;
        result["std"] = std;
        result["count"] = count;
        result["trend"] = trend;

        return result;
    }

    public void Subscribe(SignalType type, Action<Signal> callback)
    {
        _subscribers.AddOrUpdate(type,
            _ => new List<Action<Signal>> { callback },
            (_, list) =>
            {
                lock (list) { list.Add(callback); }
                return list;
            });
    }

    public Dictionary<SignalType, double> GetSnapshot()
    {
        var result = new Dictionary<SignalType, double>();
        foreach (var kvp in _signalValues)
        {
            lock (kvp.Value)
            {
                result[kvp.Key] = kvp.Value.Count > 0 ? kvp.Value[^1] : 0;
            }
        }
        return result;
    }

    public Dictionary<string, object> GetDashboard()
    {
        var result = new Dictionary<string, object>();
        foreach (var kvp in _signalValues)
        {
            result[kvp.Key.ToString()] = GetStats(kvp.Key);
        }
        return result;
    }
}

public sealed class DslEngine
{
    private static readonly Lazy<DslEngine> _instance = new(() => new DslEngine());
    public static DslEngine Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, Func<Dictionary<string, object?>, bool>> _actions = new();

    private DslEngine() { }

    public void RegisterAction(string name, Func<Dictionary<string, object?>, bool> handler)
    {
        _actions[name] = handler;
    }

    public List<DslRule> ParseRules(string dslText)
    {
        var rules = new List<DslRule>();

        var rulePattern = @"RULE\s+(?<name>\S+)\s+WHEN\s+(?<conditions>[^T]+?)\s+THEN\s+(?<actions>[^W]+?)\s+WITH\s+(?<params>.+)";
        var matches = Regex.Matches(dslText, rulePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            var name = match.Groups["name"].Value.Trim();
            var conditionsRaw = match.Groups["conditions"].Value.Trim();
            var actionsRaw = match.Groups["actions"].Value.Trim();
            var paramsRaw = match.Groups["params"].Value.Trim();

            var conditions = conditionsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.Trim())
                .Where(c => c.Length > 0)
                .ToList();

            var actions = actionsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .ToList();

            string logic = "AND";
            int priority = 1;
            double cooldown = 60;

            var paramParts = paramsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in paramParts)
            {
                var kv = p.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length == 2)
                {
                    var key = kv[0].Trim().ToLowerInvariant();
                    var val = kv[1].Trim();
                    if (key == "priority") int.TryParse(val, out priority);
                    if (key == "cooldown") double.TryParse(val, out cooldown);
                    if (key == "logic") logic = val.ToUpperInvariant() == "OR" ? "OR" : "AND";
                }
            }

            rules.Add(new DslRule(name, conditions, actions, logic, priority, cooldown, null, 0, true));
        }

        return rules;
    }

    public bool EvaluateConditions(DslRule rule, SignalBus bus)
    {
        if (rule.Conditions.Count == 0)
            return false;

        var results = new List<bool>();

        foreach (var condition in rule.Conditions)
        {
            var pattern = @"^(?<signal>\w+)\s*(?<op>!=|==|<=|>=|=|<|>)\s*(?<value>[\d.]+)$";
            var m = Regex.Match(condition.Trim(), pattern, RegexOptions.IgnoreCase);
            if (!m.Success)
                continue;

            var signalName = m.Groups["signal"].Value.Trim().ToLowerInvariant();
            var op = m.Groups["op"].Value.Trim();
            var valStr = m.Groups["value"].Value.Trim();

            if (!double.TryParse(valStr, out var threshold))
                continue;

            var signalType = ResolveSignalType(signalName);
            double currentValue = bus.Get(signalType);

            bool satisfied = op switch
            {
                ">" => currentValue > threshold,
                "<" => currentValue < threshold,
                ">=" => currentValue >= threshold,
                "<=" => currentValue <= threshold,
                "=" or "==" => Math.Abs(currentValue - threshold) < 0.001,
                "!=" => Math.Abs(currentValue - threshold) >= 0.001,
                _ => false
            };

            results.Add(satisfied);
        }

        if (results.Count == 0)
            return false;

        return rule.Logic.ToUpperInvariant() == "OR"
            ? results.Any(r => r)
            : results.All(r => r);
    }

    public bool ExecuteActions(DslRule rule)
    {
        if (rule.Actions.Count == 0)
            return false;

        var context = new Dictionary<string, object?>
        {
            ["rule_name"] = rule.Name,
            ["triggered_at"] = DateTime.UtcNow
        };

        bool allSucceeded = true;
        foreach (var actionName in rule.Actions)
        {
            var name = actionName.Trim();
            if (_actions.TryGetValue(name, out var handler))
            {
                try
                {
                    if (!handler(context))
                        allSucceeded = false;
                }
                catch
                {
                    allSucceeded = false;
                }
            }
            else
            {
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    private static SignalType ResolveSignalType(string name)
    {
        return name switch
        {
            "proxy_latency" => SignalType.ProxyLatency,
            "proxy_success_rate" => SignalType.ProxySuccessRate,
            "bandwidth_bytes" => SignalType.BandwidthBytes,
            "concurrent_connections" => SignalType.ConcurrentConnections,
            "cpu_usage" => SignalType.CpuUsage,
            "memory_usage" => SignalType.MemoryUsage,
            "estimated_cost" => SignalType.EstimatedCost,
            "daily_budget_remaining" => SignalType.DailyBudgetRemaining,
            "consecutive_failures" => SignalType.ConsecutiveFailures,
            "error_rate" => SignalType.ErrorRate,
            "cache_hit_rate" => SignalType.CacheHitRate,
            "active_proxies" => SignalType.ActiveProxies,
            "proxy_pool_health" => SignalType.ProxyPoolHealth,
            "bandit_exploration" => SignalType.BanditExploration,
            "quic_active" => SignalType.QuicActive,
            "protocol_success_rate" => SignalType.ProtocolSuccessRate,
            _ => Enum.TryParse<SignalType>(name, true, out var result) ? result : SignalType.ProxyLatency
        };
    }
}

public sealed class DynamicPolicyEngine
{
    private static readonly Lazy<DynamicPolicyEngine> _instance = new(() => new DynamicPolicyEngine());
    public static DynamicPolicyEngine Instance => _instance.Value;

    private readonly ILogger<DynamicPolicyEngine> _logger;
    private readonly SignalBus _signalBus;
    private readonly DslEngine _dslEngine;
    private readonly ConcurrentDictionary<string, AbExperiment> _experiments = new();
    private readonly List<DslRule> _rules = new();
    private readonly List<Dictionary<string, object?>> _recentActions = new();
    private readonly object _lock = new();
    private const int MaxRecentActions = 100;

    public DynamicPolicyEngine(ILogger<DynamicPolicyEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<DynamicPolicyEngine>.Instance;
        _signalBus = SignalBus.Instance;
        _dslEngine = DslEngine.Instance;

        RegisterDefaultActions();
    }

    private void RegisterDefaultActions()
    {
        _dslEngine.RegisterAction("degrade_to_free", _ => { _logger.LogInformation("Action: degrade_to_free"); return true; });
        _dslEngine.RegisterAction("reduce_concurrency", _ => { _logger.LogInformation("Action: reduce_concurrency"); return true; });
        _dslEngine.RegisterAction("pause_heavy_tasks", _ => { _logger.LogInformation("Action: pause_heavy_tasks"); return true; });
        _dslEngine.RegisterAction("enable_quic", _ => { _logger.LogInformation("Action: enable_quic"); return true; });
        _dslEngine.RegisterAction("refresh_proxy_pool", _ => { _logger.LogInformation("Action: refresh_proxy_pool"); return true; });
        _dslEngine.RegisterAction("mark_degraded_proxies", _ => { _logger.LogInformation("Action: mark_degraded_proxies"); return true; });
        _dslEngine.RegisterAction("switch_to_backup", _ => { _logger.LogInformation("Action: switch_to_backup"); return true; });
        _dslEngine.RegisterAction("notify_admin", _ => { _logger.LogInformation("Action: notify_admin"); return true; });
        _dslEngine.RegisterAction("log_warning", _ => { _logger.LogInformation("Action: log_warning"); return true; });
        _dslEngine.RegisterAction("increase_cache_size", _ => { _logger.LogInformation("Action: increase_cache_size"); return true; });
    }

    public void LoadDsl(string dslText)
    {
        var parsed = _dslEngine.ParseRules(dslText);
        lock (_lock)
        {
            _rules.Clear();
            _rules.AddRange(parsed);
        }
        _logger.LogInformation("Loaded {Count} rules from DSL", parsed.Count);
    }

    public string GetDefaultDsl()
    {
        return @"RULE cost_saver WHEN daily_budget_remaining < 0.2 THEN degrade_to_free, reduce_concurrency WITH priority=1, cooldown=60
RULE overload_protection WHEN cpu_usage > 80 AND memory_usage > 85 THEN reduce_concurrency, pause_heavy_tasks WITH priority=2, cooldown=30
RULE protocol_upgrade WHEN quic_active = false AND bandwidth_bytes > 1000000 THEN enable_quic WITH priority=3, cooldown=300
RULE pool_health_check WHEN proxy_pool_health < 60 THEN refresh_proxy_pool, mark_degraded_proxies WITH priority=2, cooldown=120
RULE performance_trend WHEN proxy_latency > 2000 THEN switch_to_backup, reduce_concurrency WITH priority=1, cooldown=10";
    }

    public List<Dictionary<string, object?>> Evaluate()
    {
        RefreshSystemSignals();

        var executed = new List<Dictionary<string, object?>>();

        List<DslRule> rulesSnapshot;
        lock (_lock)
        {
            rulesSnapshot = new List<DslRule>(_rules);
        }

        var sorted = rulesSnapshot.OrderBy(r => r.Priority).ToList();

        foreach (var rule in sorted)
        {
            if (!rule.Enabled)
                continue;
            if (rule.OnCooldown)
                continue;

            bool conditionsMet = false;
            try
            {
                conditionsMet = _dslEngine.EvaluateConditions(rule, _signalBus);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error evaluating conditions for rule {Rule}", rule.Name);
            }

            if (!conditionsMet)
                continue;

            bool success = false;
            try
            {
                success = _dslEngine.ExecuteActions(rule);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error executing actions for rule {Rule}", rule.Name);
            }

            lock (_lock)
            {
                var idx = _rules.FindIndex(r => r.Name == rule.Name);
                if (idx >= 0)
                {
                    var updated = _rules[idx] with
                    {
                        LastTriggered = DateTime.UtcNow,
                        TriggerCount = _rules[idx].TriggerCount + 1
                    };
                    _rules[idx] = updated;
                }
            }

            var actionRecord = new Dictionary<string, object?>
            {
                ["rule"] = rule.Name,
                ["actions"] = rule.Actions,
                ["success"] = success,
                ["timestamp"] = DateTime.UtcNow
            };
            executed.Add(actionRecord);
        }

        lock (_lock)
        {
            _recentActions.AddRange(executed);
            while (_recentActions.Count > MaxRecentActions)
                _recentActions.RemoveRange(0, _recentActions.Count - MaxRecentActions);
        }

        return executed;
    }

    private void RefreshSystemSignals()
    {
        int cpuCount = Environment.ProcessorCount;
        double cpuUsage = Math.Min(100, Math.Max(0, ((double)cpuCount / Math.Max(1, cpuCount)) * 10.0 + Random.Shared.NextDouble() * 20.0));
        _signalBus.Emit(SignalType.CpuUsage, cpuUsage, "system", "%");

        var process = System.Diagnostics.Process.GetCurrentProcess();
        double memoryUsage = Math.Min(100, Math.Max(0, ((double)process.WorkingSet64 / (1024 * 1024 * 1024.0)) * 100));
        _signalBus.Emit(SignalType.MemoryUsage, memoryUsage, "system", "%");
    }

    public AbExperiment CreateExperiment(string name, string strategyA, string strategyB, string metric)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var experiment = new AbExperiment(id, name, strategyA, strategyB, metric, 0, 0, 0, 0, DateTime.UtcNow, false, null);
        _experiments[id] = experiment;
        _logger.LogInformation("Created A/B experiment {Id}: {Name}", id, name);
        return experiment;
    }

    public void RecordExperiment(string expId, string group, bool success)
    {
        if (!_experiments.TryGetValue(expId, out var exp))
            return;

        var updated = group == "A"
            ? exp with { SamplesA = exp.SamplesA + 1, SuccessRateA = exp.SamplesA > 0 ? ((exp.SuccessRateA * exp.SamplesA + (success ? 1 : 0)) / (exp.SamplesA + 1)) : (success ? 1 : 0) }
            : exp with { SamplesB = exp.SamplesB + 1, SuccessRateB = exp.SamplesB > 0 ? ((exp.SuccessRateB * exp.SamplesB + (success ? 1 : 0)) / (exp.SamplesB + 1)) : (success ? 1 : 0) };

        _experiments.TryUpdate(expId, updated, exp);
    }

    public Dictionary<string, object> GetAbResults(string expId)
    {
        var result = new Dictionary<string, object>();
        if (!_experiments.TryGetValue(expId, out var exp))
        {
            result["error"] = "Experiment not found";
            return result;
        }

        result["id"] = exp.Id;
        result["name"] = exp.Name;
        result["strategyA"] = exp.StrategyA;
        result["strategyB"] = exp.StrategyB;
        result["metric"] = exp.Metric;
        result["samplesA"] = exp.SamplesA;
        result["samplesB"] = exp.SamplesB;
        result["rateA"] = exp.SuccessRateA;
        result["rateB"] = exp.SuccessRateB;
        result["declared"] = exp.Declared;
        result["winner"] = exp.Winner!;
        return result;
    }

    public Dictionary<string, object> GetDashboard()
    {
        var result = new Dictionary<string, object>();

        result["signals"] = _signalBus.GetDashboard();

        lock (_lock)
        {
            result["rules"] = _rules.Select(r => new Dictionary<string, object>
            {
                ["name"] = r.Name,
                ["enabled"] = r.Enabled,
                ["priority"] = r.Priority,
                ["trigger_count"] = r.TriggerCount,
                ["last_triggered"] = r.LastTriggered?.ToString("o") ?? "never",
                ["on_cooldown"] = r.OnCooldown,
                ["cooldown_seconds"] = r.CooldownSeconds,
                ["actions"] = r.Actions,
                ["logic"] = r.Logic
            }).ToList();

            result["recent_actions"] = new List<Dictionary<string, object?>>(_recentActions);
        }

        result["experiments"] = _experiments.Values.Select(e => new Dictionary<string, object>
        {
            ["id"] = e.Id,
            ["name"] = e.Name,
            ["metric"] = e.Metric,
            ["samplesA"] = e.SamplesA,
            ["samplesB"] = e.SamplesB,
            ["rateA"] = e.SuccessRateA,
            ["rateB"] = e.SuccessRateB,
            ["declared"] = e.Declared,
            ["winner"] = e.Winner!
        }).ToList();

        return result;
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["rules_count"] = ((Func<int>)(() => { lock (_lock) { return _rules.Count; } }))(),
            ["experiment_count"] = _experiments.Count,
            ["recent_action_count"] = ((Func<int>)(() => { lock (_lock) { return _recentActions.Count; } }))()
        };
    }
}
