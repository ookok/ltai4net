using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Intelligence;

public sealed class FluidCollective
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly OrderedDictionary _traces = new();
    private readonly ConcurrentDictionary<string, TransientFormation> _formations = new();
    private readonly ConcurrentDictionary<string, List<string>> _domainIndex = new();
    private readonly ILogger<FluidCollective>? _logger;
    private readonly List<MobilityBudget> _mobilityHistory = new();
    private readonly string _persistPath;
    private int _maxTraces = 200;

    public FluidCollective(ILogger<FluidCollective>? logger = null, string? persistPath = null)
    {
        _logger = logger;
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "fluid_collective.json");
        Load();
    }

    public StigmergicTrace Deposit(string traceId, string model, string content,
        TraceType traceType, string domain, double confidence, double depthGrade,
        List<string>? parentTraceIds = null)
    {
        var trace = new StigmergicTrace
        {
            TraceId = traceId,
            Model = model,
            Content = content,
            TraceType = traceType,
            Domain = domain,
            Confidence = confidence,
            DepthGrade = depthGrade,
            ParentTraceIds = parentTraceIds ?? new List<string>()
        };

        lock (_traces)
        {
            _traces[traceId] = trace;

            while (_traces.Count > _maxTraces)
            {
                var oldest = _traces.Cast<System.Collections.DictionaryEntry>().First();
                _traces.Remove(oldest.Key);
            }
        }

        _domainIndex.AddOrUpdate(domain,
            _ => new List<string> { traceId },
            (_, list) => { lock (list) { list.Add(traceId); } return list; });

        EvaporatePeriodic(domain, 0.05);

        return trace;
    }

    public string RetrieveContext(string domain, int maxTraces = 10, double minRelevance = 0.1,
        List<TraceType>? traceTypes = null)
    {
        var traces = GetDomainTraces(domain);
        if (traces.Count == 0) return "";

        var relevant = traces
            .Where(t => t.Relevance > minRelevance)
            .Where(t => traceTypes == null || traceTypes.Count == 0 || traceTypes.Contains(t.TraceType))
            .OrderByDescending(t => t.Relevance)
            .Take(maxTraces)
            .ToList();

        if (relevant.Count == 0) return "";

        var parts = relevant.Select(t =>
            $"[{t.TraceType}] ({t.Confidence:P0} depth:{t.DepthGrade:F1}) {t.Content}"
        );

        return string.Join("\n", parts);
    }

    public List<(StigmergicTrace Trace, double Score)> UnifiedSearch(string query, string domain, int topK = 10)
    {
        var traces = GetDomainTraces(domain);
        if (traces.Count == 0) return new List<(StigmergicTrace, double)>();

        var queryWords = Tokenize(query);
        if (queryWords.Count == 0) return new List<(StigmergicTrace, double)>();

        var results = new ConcurrentDictionary<string, (StigmergicTrace Trace, double RrfScore)>();

        Parallel.ForEach(traces, trace =>
        {
            var bm25Score = ComputeBm25(queryWords, trace.Content);
            var vectorScore = trace.Relevance;
            var graphScore = trace.ParentTraceIds.Count > 0 ? 0.3 : 0.0;

            var rrfScore = 1.0 / (60 + 1.0 / Math.Max(bm25Score, 0.01)) +
                           1.0 / (60 + 1.0 / Math.Max(vectorScore, 0.01)) +
                           1.0 / (60 + 1.0 / Math.Max(graphScore, 0.01));

            results[trace.TraceId] = (trace, rrfScore);
        });

        return results.Values
            .OrderByDescending(r => r.RrfScore)
            .Take(topK)
            .ToList();
    }

    public int ConsolidateTraces(string domain)
    {
        var traces = GetDomainTraces(domain);
        if (traces.Count < 10) return 0;

        var topTraces = traces.OrderByDescending(t => t.Confidence).Take(5).ToList();
        var summary = string.Join(" | ", topTraces.Select(t => t.Content));
        if (summary.Length > 1000) summary = summary[..1000];

        var consolidatedId = "cons_" + Guid.NewGuid().ToString("N")[..8];
        Deposit(consolidatedId, "collective", summary, TraceType.Insight, domain, 0.85, 0.8);

        foreach (var t in topTraces)
            t.Evaporate(0.5);

        _logger?.LogDebug("FluidCollective: Consolidated {Count} traces in domain {Domain}", topTraces.Count, domain);
        return topTraces.Count;
    }

    public TransientFormation FormSwarm(string taskDescription, string domain,
        int maxSize, string strategy, List<string> availableModels)
    {
        List<string> selected;

        if (strategy == "cost_optimal")
        {
            var preferred = new[] { "flash", "free", "mini", "lite", "small", "tiny" };
            selected = availableModels
                .Where(m => preferred.Any(p => m.Contains(p, StringComparison.OrdinalIgnoreCase)))
                .Take(maxSize)
                .ToList();
            if (selected.Count == 0) selected = availableModels.Take(maxSize).ToList();
        }
        else if (strategy == "quality_max")
        {
            var preferred = new[] { "pro", "max", "deep", "plus", "ultra", "turbo" };
            selected = availableModels
                .Where(m => preferred.Any(p => m.Contains(p, StringComparison.OrdinalIgnoreCase)))
                .Take(maxSize)
                .ToList();
            if (selected.Count == 0) selected = availableModels.Take(maxSize).ToList();
        }
        else
        {
            var diversities = availableModels
                .GroupBy(m => m.Contains('?') ? "unknown" : m.Split('/').LastOrDefault()?.Split('-').FirstOrDefault() ?? m)
                .Select(g => g.First())
                .Take(maxSize)
                .ToList();
            selected = diversities;
        }

        var formation = new TransientFormation
        {
            FormationId = "swarm_" + Guid.NewGuid().ToString("N")[..8],
            Models = selected,
            TaskDescription = taskDescription,
            FormationStrategy = strategy,
            Status = "active",
            CreatedAt = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds
        };

        _formations[formation.FormationId] = formation;

        _logger?.LogDebug("FluidCollective: Formed swarm {Id} with {Count} models ({Strategy})",
            formation.FormationId, selected.Count, strategy);

        return formation;
    }

    public void DissolveSwarm(string formationId)
    {
        if (_formations.TryGetValue(formationId, out var formation))
            formation.Status = "dissolved";
    }

    public MobilityBudget AllocateMobilityBudget(double taskComplexity, bool contextAvailable,
        double costBudget, string preference = "balanced")
    {
        MobilityBudget budget;

        if (preference == "cost_optimal")
        {
            budget = new MobilityBudget
            {
                ModelSwitches = 3 + (int)(taskComplexity * 4),
                UniqueModelsUsed = 1 + (int)(taskComplexity * 1),
                Strategy = "cost_optimal"
            };
        }
        else if (preference == "quality_max")
        {
            budget = new MobilityBudget
            {
                ModelSwitches = 5 + (int)(taskComplexity * 6),
                UniqueModelsUsed = 2 + (int)(taskComplexity * 3),
                Strategy = "quality_max"
            };
        }
        else
        {
            budget = new MobilityBudget
            {
                ModelSwitches = 4 + (int)(taskComplexity * 4),
                UniqueModelsUsed = 2 + (int)(taskComplexity * 1),
                Strategy = "balanced"
            };
        }

        budget.TotalTokens = 4000 + (int)(taskComplexity * 16000);
        budget.CostYuan = costBudget;

        _mobilityHistory.Add(budget);
        if (_mobilityHistory.Count > 100) _mobilityHistory.RemoveAt(0);

        return budget;
    }

    private List<StigmergicTrace> GetDomainTraces(string domain)
    {
        if (!_domainIndex.TryGetValue(domain, out var ids)) return new List<StigmergicTrace>();

        var traces = new List<StigmergicTrace>();
        lock (ids)
        {
            foreach (var id in ids)
            {
                lock (_traces)
                {
                    if (_traces[id] is StigmergicTrace trace)
                        traces.Add(trace);
                }
            }
        }

        return traces;
    }

    private void EvaporatePeriodic(string domain, double rate)
    {
        var traces = GetDomainTraces(domain);
        foreach (var t in traces) t.Evaporate(rate);
    }

    private static double ComputeBm25(HashSet<string> queryTerms, string document)
    {
        double score = 0;
        var docTerms = Tokenize(document);
        if (docTerms.Count == 0) return 0;

        foreach (var term in queryTerms)
        {
            if (docTerms.Contains(term))
                score += 1.0;
        }

        return score / (docTerms.Count + 1.0);
    }

    private static HashSet<string> Tokenize(string text)
    {
        return text.ToLower()
            .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '(', ')', '[', ']', '{', '}', '!', '?' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2)
            .ToHashSet();
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["traces"] = _traces.Count,
            ["formations"] = _formations.Count,
            ["domains"] = _domainIndex.Count,
            ["mobility_history"] = _mobilityHistory.Count,
            ["active_formations"] = _formations.Values.Count(f => f.Status == "active")
        };
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var data = new
            {
                traces = _traces.Values.Cast<StigmergicTrace>().ToList(),
                formations = _formations.Values.ToList(),
                domain_index = _domainIndex.ToDictionary(kv => kv.Key, kv => kv.Value),
                saved_at = DateTime.UtcNow.ToString("O")
            };

            File.WriteAllText(_persistPath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("FluidCollective: Save failed: {Message}", ex.Message);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;

            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (data == null) return;

            if (data.TryGetValue("traces", out var traces))
            {
                var loaded = JsonSerializer.Deserialize<List<StigmergicTrace>>(traces.GetRawText());
                if (loaded != null)
                    foreach (var t in loaded)
                        lock (_traces) _traces[t.TraceId] = t;
            }

            if (data.TryGetValue("formations", out var formations))
            {
                var loaded = JsonSerializer.Deserialize<List<TransientFormation>>(formations.GetRawText());
                if (loaded != null)
                    foreach (var f in loaded) _formations[f.FormationId] = f;
            }

            if (data.TryGetValue("domain_index", out var di))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(di.GetRawText());
                if (loaded != null)
                    foreach (var kv in loaded) _domainIndex[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("FluidCollective: Load failed: {Message}", ex.Message);
        }
    }
}
