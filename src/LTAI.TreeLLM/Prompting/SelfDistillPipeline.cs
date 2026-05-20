using LTAI.Core.System;
using LTAI.TreeLLM.Session;
using LTAI.Vector.Knowledge;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Prompting;

public enum DistillStage { ColdStart, ConstraintDiscovery, ProgressiveEnforcement, Converged }

public sealed record ParallelTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> BranchPrompts { get; init; } = new();
    public int TypicalBranches { get; init; }
    public int TypicalDepth { get; init; }
    public double AvgBranchScore { get; set; }
    public int UsageCount { get; set; }
    public List<string> TopologicalConstraints { get; init; } = new();
    public DistillStage DiscoveredAt { get; init; } = DistillStage.ColdStart;
}

public sealed record DistillRound
{
    public int Round { get; init; }
    public DistillStage Stage { get; init; }
    public int TemplatesDiscovered { get; init; }
    public int BranchesAnalyzed { get; init; }
    public double AvgBranchQuality { get; init; }
    public double ConstraintStrictness { get; init; }
    public List<string> NewConstraints { get; init; } = new();
    public long DurationMs { get; init; }
}

public sealed record SelfDistillResult
{
    public List<ParallelTemplate> DiscoveredTemplates { get; init; } = new();
    public List<DistillRound> DistillHistory { get; init; } = new();
    public int TotalTemplates { get; init; }
    public DistillStage FinalStage { get; init; }
    public double ElapsedMs { get; init; }
}

public sealed class SelfDistillPipeline
{
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<SelfDistillPipeline>? _logger;

    private readonly List<ParallelTemplate> _templates = new();
    private readonly List<DistillRound> _distillHistory = new();
    private readonly List<string> _globalConstraints = new();
    private DistillStage _currentStage = DistillStage.ColdStart;
    private readonly object _lock = new();

    private const int ColdStartMinSamples = 10;
    private const int ConstraintMinEvidence = 5;

    public SelfDistillPipeline(
        PromptBuilder promptBuilder,
        ILogger<SelfDistillPipeline>? logger = null)
    {
        _promptBuilder = promptBuilder;
        _logger = logger;
    }

    public DistillStage CurrentStage => _currentStage;

    public async Task<SelfDistillResult> RunPipelineAsync(
        List<ParallelGraphResult> executionTraces,
        Func<string, Task<string>>? chatFn = null,
        int maxRounds = 10)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allTraces = new List<ParallelGraphResult>(executionTraces);

        for (int round = 0; round < maxRounds; round++)
        {
            DistillStage stage;

            if (allTraces.Count < ColdStartMinSamples)
            {
                stage = DistillStage.ColdStart;
            }
            else if (_templates.Count < 3)
            {
                stage = DistillStage.ColdStart;
            }
            else if (_templates.Any(t => t.UsageCount < ConstraintMinEvidence))
            {
                stage = DistillStage.ConstraintDiscovery;
            }
            else
            {
                stage = DistillStage.ProgressiveEnforcement;
            }

            _currentStage = stage;

            var roundResult = ProcessRound(stage, allTraces, chatFn);
            _distillHistory.Add(roundResult);

            await AddNoiseFilter(allTraces);

            if (stage == DistillStage.ProgressiveEnforcement && round >= 2)
            {
                var lastTwo = _distillHistory.TakeLast(2).ToList();
                if (lastTwo.Count >= 2 &&
                    Math.Abs(lastTwo[0].AvgBranchQuality - lastTwo[1].AvgBranchQuality) < 0.01)
                {
                    _currentStage = DistillStage.Converged;
                    break;
                }
            }
        }

        sw.Stop();

        _logger?.LogInformation(
            "SelfDistill: {TemplateCount} templates, {RoundCount} rounds, stage={Stage}, {Ms}ms",
            _templates.Count, _distillHistory.Count, _currentStage, sw.ElapsedMilliseconds);

        return new SelfDistillResult
        {
            DiscoveredTemplates = _templates.ToList(),
            DistillHistory = _distillHistory.ToList(),
            TotalTemplates = _templates.Count,
            FinalStage = _currentStage,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    private DistillRound ProcessRound(
        DistillStage stage,
        List<ParallelGraphResult> traces,
        Func<string, Task<string>>? chatFn)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int templatesDiscovered = 0;
        int branchesAnalyzed = 0;

        foreach (var trace in traces)
        {
            branchesAnalyzed += trace.BranchDecisions.Count;

            foreach (var decision in trace.BranchDecisions.Where(d => d.WasBeneficial))
            {
                var branchNodes = trace.AllNodes
                    .Where(n => n.ParentIds.Contains(decision.NodeId))
                    .ToList();

                if (branchNodes.Count < 2) continue;

                var template = DiscoverTemplate(decision, branchNodes, stage);
                if (template != null)
                {
                    template = template with { DiscoveredAt = stage };
                    UpdateOrAddTemplate(template);
                    templatesDiscovered++;
                }
            }
        }

        var constraints = ExtractConstraints(traces, stage);
        foreach (var c in constraints)
        {
            lock (_lock)
            {
                if (!_globalConstraints.Contains(c))
                    _globalConstraints.Add(c);
            }
        }

        var strictness = stage switch
        {
            DistillStage.ColdStart => 0.2,
            DistillStage.ConstraintDiscovery => 0.5,
            DistillStage.ProgressiveEnforcement => 0.8,
            DistillStage.Converged => 1.0,
            _ => 0.5
        };

        sw.Stop();

        return new DistillRound
        {
            Round = _distillHistory.Count + 1,
            Stage = stage,
            TemplatesDiscovered = templatesDiscovered,
            BranchesAnalyzed = branchesAnalyzed,
            AvgBranchQuality = traces.Count > 0
                ? traces.Average(t => t.BranchScores.Values.DefaultIfEmpty(0).Average())
                : 0,
            ConstraintStrictness = strictness,
            NewConstraints = constraints,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private ParallelTemplate? DiscoverTemplate(
        BranchDecision decision,
        List<ParallelNode> branchNodes,
        DistillStage stage)
    {
        if (decision.NumBranches < 2) return null;
        if (string.IsNullOrEmpty(decision.NodeTask)) return null;

        var branchTasks = branchNodes.Select(n => n.Task).ToList();
        var pattern = ClassifyTaskPattern(decision.NodeTask, branchTasks);

        var template = new ParallelTemplate
        {
            Name = $"{pattern}_{decision.NumBranches}branches_d{decision.Depth}",
            Description = decision.NodeTask[..Math.Min(200, decision.NodeTask.Length)],
            BranchPrompts = branchTasks.Take(5).ToList(),
            TypicalBranches = decision.NumBranches,
            TypicalDepth = decision.Depth,
            AvgBranchScore = branchNodes.Average(n => n.Confidence),
            UsageCount = 1,
            TopologicalConstraints = stage >= DistillStage.ProgressiveEnforcement
                ? GenerateConstraints(decision, branchNodes)
                : new()
        };

        return template;
    }

    private void UpdateOrAddTemplate(ParallelTemplate template)
    {
        lock (_lock)
        {
            var existing = _templates.FirstOrDefault(t =>
                t.TypicalBranches == template.TypicalBranches &&
                t.TypicalDepth == template.TypicalDepth &&
                Similarity(t.Name, template.Name) > 0.6);

            if (existing != null)
            {
                existing.UsageCount++;
                existing.AvgBranchScore = existing.AvgBranchScore * 0.8 + template.AvgBranchScore * 0.2;

                foreach (var bp in template.BranchPrompts)
                {
                    if (!existing.BranchPrompts.Contains(bp))
                        existing.BranchPrompts.Add(bp);
                }

                foreach (var constraint in template.TopologicalConstraints)
                {
                    if (!existing.TopologicalConstraints.Contains(constraint))
                        existing.TopologicalConstraints.Add(constraint);
                }
            }
            else
            {
                _templates.Add(template);
                _templates.Sort((a, b) => b.AvgBranchScore.CompareTo(a.AvgBranchScore));
                
                if (_templates.Count > 50)
                    _templates.RemoveRange(50, _templates.Count - 50);
            }
        }
    }

    private static List<string> GenerateConstraints(
        BranchDecision decision,
        List<ParallelNode> branchNodes)
    {
        var constraints = new List<string>();

        if (branchNodes.Count > 1)
        {
            var maxDepth = branchNodes.Max(n => n.Depth);
            var minDepth = branchNodes.Min(n => n.Depth);
            if (maxDepth - minDepth <= 1)
                constraints.Add($"depth_balanced: max depth difference <= 1 at node depth {decision.Depth}");
        }

        var confidences = branchNodes.Select(n => n.Confidence).ToList();
        var avgConf = confidences.Average();
        var variance = confidences.Average(c => Math.Pow(c - avgConf, 2));
        if (variance > 0.15)
            constraints.Add($"confidence_variance_warning: var={variance:F2} at depth {decision.Depth}");

        var taskLengths = branchNodes.Select(n => n.Task.Length).ToList();
        if (taskLengths.Count >= 2)
        {
            var maxLen = taskLengths.Max();
            var minLen = taskLengths.Where(l => l > 0).DefaultIfEmpty(1).Min();
            if (maxLen > minLen * 3)
                constraints.Add($"task_granularity: max/min length ratio > 3x at depth {decision.Depth}");
        }

        constraints.Add($"branch_count_{decision.NumBranches}: recommended {decision.NumBranches} branches at depth {decision.Depth}");
        constraints.Add($"max_children_per_node: {Math.Max(3, decision.NumBranches)}");

        return constraints;
    }

    private List<string> ExtractConstraints(List<ParallelGraphResult> traces, DistillStage stage)
    {
        var constraints = new List<string>();

        if (stage < DistillStage.ConstraintDiscovery) return constraints;

        var failedNodes = traces
            .SelectMany(t => t.AllNodes)
            .Where(n => n.State == NodeState.Failed)
            .ToList();

        if (failedNodes.Count > 0)
        {
            var avgFailedDepth = failedNodes.Average(n => n.Depth);
            constraints.Add($"fail_zone_depth: avg failure at depth {avgFailedDepth:F1}");
        }

        var stalledDecisions = traces
            .SelectMany(t => t.BranchDecisions)
            .Where(d => !d.WasBeneficial && d.NumBranches > 0)
            .ToList();

        if (stalledDecisions.Count >= 3)
        {
            var avgStallDepth = stalledDecisions.Average(d => d.Depth);
            constraints.Add($"stall_warning: {stalledDecisions.Count} unbeneficial branches detected near depth {avgStallDepth:F1}");
        }

        if (stage >= DistillStage.ProgressiveEnforcement)
        {
            var allNodes = traces.SelectMany(t => t.AllNodes).ToList();
            var maxObservedDepth = allNodes.Count > 0 ? allNodes.Max(n => n.Depth) : 0;
            constraints.Add($"hard_depth_limit: {Math.Min(8, maxObservedDepth + 1)}");

            var avgBranches = traces
                .SelectMany(t => t.BranchDecisions)
                .Where(d => d.WasBeneficial)
                .Select(d => d.NumBranches)
                .DefaultIfEmpty(3)
                .Average();

            constraints.Add($"recommended_branches: {(int)Math.Round(avgBranches)}");
        }

        return constraints;
    }

    private async Task AddNoiseFilter(List<ParallelGraphResult> traces)
    {
        var lowQualityTraces = traces
            .Where(t => t.BranchScores.Values.DefaultIfEmpty(0).Average() < 0.3)
            .ToList();

        foreach (var trace in lowQualityTraces)
        {
            var noisePattern = ExtractNoisePattern(trace);
            if (!string.IsNullOrEmpty(noisePattern))
            {
                lock (_lock)
                {
                    if (!_globalConstraints.Contains(noisePattern))
                        _globalConstraints.Add(noisePattern);
                }
            }
        }

        await System.Threading.Tasks.Task.CompletedTask;
    }

    private static string? ExtractNoisePattern(ParallelGraphResult trace)
    {
        var shortResults = trace.AllNodes
            .Where(n => n.Result != null && n.Result.Length < 20)
            .ToList();

        if (shortResults.Count > trace.AllNodes.Count * 0.3)
            return $"noise_filter: {shortResults.Count} short results at depth range";

        return null;
    }

    private static string ClassifyTaskPattern(string task, List<string> branches)
    {
        if (branches.Count >= 4) return "multi-perspective";

        var taskLower = task.ToLower();
        return ClassificationRegistry.TaskPattern.Classify(taskLower);

        return "general";
    }

    private static double Similarity(string a, string b)
    {
        var wa = new HashSet<string>(a.Split('_', StringSplitOptions.RemoveEmptyEntries));
        var wb = new HashSet<string>(b.Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (wa.Count == 0 || wb.Count == 0) return 0;
        var intersection = wa.Intersect(wb).Count();
        var union = wa.Union(wb).Count();
        return (double)intersection / union;
    }

    public List<ParallelTemplate> MatchTemplate(string task, DistillStage? minStage = null)
    {
        lock (_lock)
        {
            var filtered = _templates.AsEnumerable();
            if (minStage.HasValue)
                filtered = filtered.Where(t => t.DiscoveredAt >= minStage.Value);

            return filtered
                .OrderByDescending(t => Similarity(t.Description, task))
                .Take(5)
                .ToList();
        }
    }

    public List<string> GetActiveConstraints()
    {
        lock (_lock) { return _globalConstraints.ToList(); }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new()
            {
                ["stage"] = _currentStage.ToString(),
                ["templates"] = _templates.Count,
                ["rounds"] = _distillHistory.Count,
                ["constraints"] = _globalConstraints.Count,
                ["top_templates"] = _templates.Take(5).Select(t => new
                {
                    t.Name,
                    t.TypicalBranches,
                    t.TypicalDepth,
                    score = Math.Round(t.AvgBranchScore, 3),
                    t.UsageCount
                }).ToList()
            };
        }
    }
}
