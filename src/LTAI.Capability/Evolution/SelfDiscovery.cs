using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Capability.Evolution;

public record ToolProposal(string Name, string Category, string Command, string Description,
    string Pattern, Dictionary<string, object>? PipelineConfig, int OccurrenceCount,
    double AvgSuccessRate, bool AutoCreated, bool Notified, bool Created);

public record ToolPattern(string Signature, string Domain, List<string> PipelineSteps,
    int Count, List<double> SuccessRates, List<string> SampleQueries)
{
    public double AvgSuccess() => SuccessRates.Count > 0 ? SuccessRates.Average() : 0;
    public bool IsReady(int threshold = 5) => Count >= threshold && AvgSuccess() >= 0.6;
}

public sealed class SelfDiscovery
{
    private readonly Dictionary<string, ToolPattern> _patterns = new();
    private readonly List<ToolProposal> _proposals = new();
    private readonly int _patternThreshold;
    private readonly ILogger<SelfDiscovery> _logger;
    private readonly object _lock = new();

    public SelfDiscovery(int patternThreshold = 5, ILogger<SelfDiscovery>? logger = null)
    {
        _patternThreshold = patternThreshold;
        _logger = logger ?? NullLogger<SelfDiscovery>.Instance;
    }

    public void Observe(string domain, List<string> pipelineSteps, bool success, string? query = null)
    {
        var sig = MakeSignature(domain, pipelineSteps);
        lock (_lock)
        {
            if (!_patterns.TryGetValue(sig, out var pattern))
            {
                pattern = new ToolPattern(sig, domain, pipelineSteps, 0, new(), new());
                _patterns[sig] = pattern;
            }

            pattern = pattern with
            {
                Count = pattern.Count + 1,
                SuccessRates = pattern.SuccessRates.Append(success ? 1.0 : 0.0).TakeLast(30).ToList(),
                SampleQueries = (query != null ? pattern.SampleQueries.Append(query).TakeLast(5).ToList() : pattern.SampleQueries)
            };

            if (pattern.IsReady(_patternThreshold) && !_proposals.Any(p => p.Pattern == sig))
            {
                var proposal = CreateProposal(pattern);
                _proposals.Add(proposal);
                _logger.LogInformation("Discovered new tool pattern: {Name}", proposal.Name);
            }
        }
    }

    public List<ToolProposal> GetProposals()
    {
        lock (_lock) { return _proposals.ToList(); }
    }

    public List<ToolProposal> GetNewProposals()
    {
        lock (_lock) { return _proposals.Where(p => !p.Notified).ToList(); }
    }

    public void MarkNotified(string name)
    {
        lock (_lock)
        {
            var idx = _proposals.FindIndex(p => p.Name == name);
            if (idx >= 0) _proposals[idx] = _proposals[idx] with { Notified = true };
        }
    }

    public void MarkCreated(string name)
    {
        lock (_lock)
        {
            var idx = _proposals.FindIndex(p => p.Name == name);
            if (idx >= 0) _proposals[idx] = _proposals[idx] with { Created = true };
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["total_patterns"] = _patterns.Count,
                ["ready_patterns"] = _patterns.Values.Count(p => p.IsReady(_patternThreshold)),
                ["total_proposals"] = _proposals.Count,
                ["auto_created"] = _proposals.Count(p => p.AutoCreated),
                ["notified"] = _proposals.Count(p => p.Notified)
            };
        }
    }

    private ToolProposal CreateProposal(ToolPattern pattern)
    {
        var name = GenerateName(pattern.Domain, pattern.PipelineSteps);
        var pipelineSteps = string.Join(" + ", pattern.PipelineSteps);
        return new ToolProposal(name, pattern.Domain, $"/{name}",
            $"Auto-discovered pipeline: {pipelineSteps}", pattern.Signature,
            new Dictionary<string, object> { ["steps"] = pattern.PipelineSteps },
            pattern.Count, pattern.AvgSuccess(), true, false, false);
    }

    private static string MakeSignature(string domain, List<string> steps)
        => $"{domain}:{string.Join("+", steps)}";

    private static string GenerateName(string domain, List<string> steps)
    {
        var domainPrefix = domain.Length > 8 ? domain[..8] : domain;
        var stepNames = string.Join("_", steps.Take(3).Select(SanitizeStep));
        return $"{domainPrefix}_{stepNames}";
    }

    private static string SanitizeStep(string step)
    {
        step = step.ToLowerInvariant().Replace(" ", "_");
        return step.Length > 10 ? step[..10] : step;
    }

    public void SaveToDisk(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, ".livingtree", "self_discovery.json");
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        lock (_lock)
        {
            var data = System.Text.Json.JsonSerializer.Serialize(new { patterns = _patterns, proposals = _proposals, saved_at = DateTime.UtcNow.ToString("O") });
            File.WriteAllText(path, data);
        }
    }

    public void LoadFromDisk(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, ".livingtree", "self_discovery.json");
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            lock (_lock)
            {
                if (doc.RootElement.TryGetProperty("patterns", out var pats))
                    foreach (var p in pats.EnumerateArray())
                    {
                        var sig = p.GetProperty("signature").GetString() ?? "";
                        var tp = new ToolPattern(sig, p.GetProperty("domain").GetString() ?? "", new(), p.TryGetProperty("count", out var c) ? c.GetInt32() : 0, new(), new());
                        _patterns[sig] = tp;
                    }
                if (doc.RootElement.TryGetProperty("proposals", out var props))
                    foreach (var pr in props.EnumerateArray())
                    {
                        _proposals.Add(new ToolProposal(
                            pr.GetProperty("name").GetString() ?? "", pr.GetProperty("category").GetString() ?? "",
                            pr.GetProperty("command").GetString() ?? "", pr.GetProperty("description").GetString() ?? "",
                            pr.GetProperty("pattern").GetString() ?? "", null, 0, 0, false, false, false));
                    }
            }
        }
        catch { }
    }
}
