using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Planning.Planning;

public sealed class GtsmPlanner
{
    private readonly ILogger<GtsmPlanner> _logger;
    private readonly Dictionary<string, double> _scoreCache = new();
    private readonly List<GTSMTrajectory> _history = new();
    private readonly Random _rng = new();

    public static readonly Dictionary<string, List<string>> ActionCatalog = new()
    {
        ["eia"] = new() { "collect_site_data", "identify_pollutants", "model_dispersion", "assess_impact", "propose_mitigation", "compile_report" },
        ["code"] = new() { "understand_requirements", "design_architecture", "implement_core", "write_tests", "refactor", "document" },
        ["research"] = new() { "formulate_question", "search_literature", "extract_findings", "synthesize", "draw_conclusions" },
        ["general"] = new() { "analyze", "search", "compute", "verify", "summarize" }
    };

    public static readonly double[] NoiseSchedule = { 0.8, 0.5, 0.3, 0.15, 0.05 };

    private static readonly Lazy<GtsmPlanner> _instance = new(
        () => new GtsmPlanner(NullLogger<GtsmPlanner>.Instance));

    public static GtsmPlanner Instance => _instance.Value;

    private GtsmPlanner(ILogger<GtsmPlanner> logger)
    {
        _logger = logger;
    }

    public GTSMTrajectory Plan(
        string task,
        GTSMMode mode,
        string domain = "general",
        int maxSteps = 8,
        Func<string, string, CancellationToken, Task<string>>? llmCall = null,
        CancellationToken cancellationToken = default)
    {
        var actions = ActionCatalog.GetValueOrDefault(domain, ActionCatalog["general"]);

        var resolvedMode = mode == GTSMMode.Auto
            ? actions.Count <= 4 ? GTSMMode.Tree
            : actions.Count > 8 ? GTSMMode.Flow
            : GTSMMode.Hybrid
            : mode;

        List<GTSMStep> steps = resolvedMode switch
        {
            GTSMMode.Tree => _planTree(task, domain, maxSteps),
            GTSMMode.Flow => _planFlow(task, domain, maxSteps),
            GTSMMode.Hybrid => _planHybrid(task, domain, maxSteps),
            _ => _planFlow(task, domain, maxSteps)
        };

        var totalScore = ComputeGtsmScore(steps);

        var trajectory = new GTSMTrajectory
        {
            Task = task,
            Mode = resolvedMode,
            Steps = steps,
            TotalScore = totalScore,
            TreeDepth = steps.Count > 0 ? steps.Max(s => s.TreeDepth) : 0,
            DiffusionSteps = resolvedMode == GTSMMode.Flow ? NoiseSchedule.Length : 1
        };

        _logger.LogInformation(
            "GTSM plan generated | task={Task} mode={Mode} steps={Steps} score={Score:F3}",
            task[..Math.Min(task.Length, 60)], resolvedMode, steps.Count, totalScore);

        return trajectory;
    }

    private List<GTSMStep> _planTree(string task, string domain, int maxSteps)
    {
        var actions = ActionCatalog.GetValueOrDefault(domain, ActionCatalog["general"]);
        var used = new HashSet<string>();
        var result = new List<GTSMStep>();
        var index = 0;

        void Expand(int depth, ref int remaining)
        {
            if (remaining <= 0 || depth > 4)
                return;

            var candidates = actions
                .Where(a => !used.Contains(a))
                .OrderByDescending(a => ScoreFunction(a, null))
                .ToList();

            foreach (var action in candidates)
            {
                if (remaining <= 0)
                    break;

                var score = ScoreFunction(action, null);
                used.Add(action);

                result.Add(new GTSMStep
                {
                    Index = index++,
                    Action = action,
                    Tool = GuessTool(action),
                    Params = new Dictionary<string, object?> { ["task"] = task, ["depth"] = depth },
                    TreeDepth = depth,
                    NoiseStd = 0.0,
                    ScoreGradient = score,
                    Confidence = score
                });

                remaining--;

                if (remaining > 0 && depth < 3)
                    Expand(depth + 1, ref remaining);
            }
        }

        var rem = maxSteps;
        Expand(0, ref rem);

        return result;
    }

    private List<GTSMStep> _planFlow(string task, string domain, int maxSteps)
    {
        var actions = ActionCatalog.GetValueOrDefault(domain, ActionCatalog["general"]);
        var used = new HashSet<string>();
        var steps = new List<GTSMStep>();

        for (var i = 0; i < maxSteps; i++)
        {
            var action = actions[_rng.Next(actions.Count)];
            var isNew = used.Add(action);

            var score = ScoreFunction(action, null);
            steps.Add(new GTSMStep
            {
                Index = i,
                Action = action,
                Tool = GuessTool(action),
                Params = new Dictionary<string, object?> { ["task"] = task },
                TreeDepth = 0,
                NoiseStd = NoiseSchedule[0],
                ScoreGradient = score,
                Confidence = score
            });
        }

        foreach (var noise in NoiseSchedule)
        {
            if (_rng.NextDouble() < noise)
            {
                var swapIdx = _rng.Next(steps.Count);
                var unused = actions.Where(a => !used.Contains(a)).ToList();
                if (unused.Count > 0)
                {
                    var newAction = unused[_rng.Next(unused.Count)];
                    used.Remove(steps[swapIdx].Action);
                    used.Add(newAction);

                    steps[swapIdx].Action = newAction;
                    steps[swapIdx].Tool = GuessTool(newAction);
                    steps[swapIdx].NoiseStd = noise;
                }
            }

            var worstIdx = 0;
            var worstScore = double.MaxValue;
            for (var i = 0; i < steps.Count; i++)
            {
                steps[i].ScoreGradient = ScoreFunction(steps[i].Action, steps[i].Tool);
                if (steps[i].ScoreGradient < worstScore)
                {
                    worstScore = steps[i].ScoreGradient;
                    worstIdx = i;
                }
            }

            var bestAction = actions
                .Where(a => !used.Contains(a))
                .OrderByDescending(a => ScoreFunction(a, null))
                .FirstOrDefault();

            if (bestAction is not null)
            {
                used.Remove(steps[worstIdx].Action);
                used.Add(bestAction);
                steps[worstIdx].Action = bestAction;
                steps[worstIdx].Tool = GuessTool(bestAction);
                var bestScore = ScoreFunction(bestAction, null);
                steps[worstIdx].ScoreGradient = bestScore;
                steps[worstIdx].Confidence = bestScore;
            }

            for (var i = 0; i < steps.Count; i++)
                steps[i].Index = i;
        }

        return steps;
    }

    private List<GTSMStep> _planHybrid(string task, string domain, int maxSteps)
    {
        var treeBudget = Math.Min(maxSteps, 6);
        var treeSteps = _planTree(task, domain, treeBudget);

        var leafSteps = treeSteps
            .Where(s => s.TreeDepth >= 2 || s.TreeDepth == (treeSteps.Count > 0 ? treeSteps.Max(t => t.TreeDepth) : 0))
            .ToList();

        if (leafSteps.Count > 0 && maxSteps > treeSteps.Count)
        {
            var actions = ActionCatalog.GetValueOrDefault(domain, ActionCatalog["general"]);
            var used = treeSteps.Select(s => s.Action).ToHashSet();
            var remaining = maxSteps - treeSteps.Count;
            var idx = treeSteps.Count;

            foreach (var leaf in leafSteps)
            {
                if (remaining <= 0)
                    break;

                var refinementAction = actions
                    .Where(a => !used.Contains(a))
                    .OrderByDescending(a => ScoreFunction(a, null))
                    .FirstOrDefault();

                if (refinementAction is not null)
                {
                    used.Add(refinementAction);
                    var score = ScoreFunction(refinementAction, null);
                    treeSteps.Add(new GTSMStep
                    {
                        Index = idx++,
                        Action = refinementAction,
                        Tool = GuessTool(refinementAction),
                        Params = new Dictionary<string, object?> { ["task"] = task, ["parent"] = leaf.Action },
                        TreeDepth = leaf.TreeDepth + 1,
                        NoiseStd = 0.15,
                        ScoreGradient = score,
                        Confidence = score
                    });
                    remaining--;
                }
            }
        }

        for (var i = 0; i < treeSteps.Count; i++)
            treeSteps[i].Index = i;

        return treeSteps;
    }

    public double ComputeGtsmScore(List<GTSMStep> steps)
    {
        double total = 0;
        foreach (var step in steps)
            total += step.ScoreGradient * step.Confidence
                     - step.TreeDepth * 0.05
                     - step.NoiseStd * 0.1;
        return total;
    }

    public void LearnFromResult(GTSMTrajectory trajectory)
    {
        _history.Add(trajectory);
        if (_history.Count > 100)
            _history.RemoveAt(0);

        foreach (var step in trajectory.Steps)
        {
            var key = $"{step.Action}:{step.Tool}";
            var score = step.Confidence;
            _scoreCache[key] = _scoreCache.TryGetValue(key, out var existing)
                ? (existing + score) / 2.0
                : score;
        }

        _logger.LogDebug(
            "Learned from trajectory | task={Task} steps={Steps}",
            trajectory.Task[..Math.Min(trajectory.Task.Length, 60)], trajectory.Steps.Count);
    }

    public double ScoreFunction(string action, string? tool)
    {
        var key = $"{action}:{tool ?? ""}";
        if (_scoreCache.TryGetValue(key, out var score))
            return score;

        if (tool is not null && _scoreCache.TryGetValue($"{action}:", out score))
            return score;

        return 0.5;
    }

    public Dictionary<string, object?> GetStats()
    {
        return new()
        {
            ["history_count"] = _history.Count,
            ["cache_size"] = _scoreCache.Count,
            ["average_trajectory_score"] = _history.Count > 0
                ? _history.Average(t => t.TotalScore)
                : 0.0,
            ["domains"] = ActionCatalog.Keys.ToList()
        };
    }

    private static string GuessTool(string action)
    {
        return action switch
        {
            "search" or "search_literature" => "search_tool",
            "compute" or "model_dispersion" => "compute_tool",
            "verify" or "write_tests" => "test_tool",
            "document" or "compile_report" or "summarize" or "draw_conclusions" => "writer_tool",
            "implement_core" or "refactor" => "code_tool",
            "synthesize" or "assess_impact" => "analyst_tool",
            _ => "general_tool"
        };
    }
}
