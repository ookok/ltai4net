using LTAI.Tools.Skills;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record SkillGrowth
{
    public string SkillName { get; init; } = "";
    public SkillMaturity CurrentMaturity { get; init; }
    public int UsageCount { get; init; }
    public int SuccessCount { get; init; }
    public float SuccessRate { get; init; }
    public SkillMaturity NextMaturity { get; init; }
    public float ProgressToNext { get; init; }
}

public sealed record SkillExecutionPlan
{
    public List<SkillEntry> OrderedSkills { get; init; } = new();
    public List<string> ParallelGroups { get; init; } = new();
    public int EstimatedSteps { get; init; }
    public float Confidence { get; init; }
    public string Summary { get; init; } = "";
}

public sealed class SkillTree
{
    private readonly SkillCatalog _catalog;
    private readonly ILogger<SkillTree> _logger;
    private readonly Dictionary<string, SkillProgress> _progress = new();
    private readonly object _lock = new();

    private static readonly Dictionary<SkillMaturity, (int Usage, float Rate)> PromotionThresholds = new()
    {
        [SkillMaturity.Experimental] = (5, 0.6f),
        [SkillMaturity.Stable] = (20, 0.8f),
    };

    public SkillTree(SkillCatalog catalog, ILogger<SkillTree>? logger = null)
    {
        _catalog = catalog;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillTree>.Instance;
    }

    public List<SkillEntry> SuggestSkills(string query)
    {
        var matched = _catalog.Search(query);
        var scored = matched.Select(s => new
        {
            Skill = s,
            Score = ComputeSkillScore(s)
        })
        .OrderByDescending(x => x.Score)
        .Take(5)
        .Select(x => x.Skill)
        .ToList();

        var suggested = new List<SkillEntry>(scored);

        foreach (var skill in scored)
        {
            foreach (var dep in skill.Dependencies)
            {
                var depSkill = _catalog.GetSkill(dep);
                if (depSkill != null && !suggested.Contains(depSkill))
                {
                    suggested.Add(depSkill);
                }
            }
        }

        return suggested.OrderByDescending(s => ComputeSkillScore(s)).ToList();
    }

    public List<SkillEntry> SuggestSkillPipeline(string query)
    {
        var skills = SuggestSkills(query);
        if (skills.Count == 0) return new();

        var pipeline = new List<SkillEntry>();
        var added = new HashSet<string>();

        foreach (var skill in skills)
        {
            AddWithDependencies(skill, pipeline, added);
        }

        return pipeline;
    }

    public SkillExecutionPlan CreateExecutionPlan(string query)
    {
        var skills = SuggestSkillPipeline(query);
        if (skills.Count == 0)
        {
            return new SkillExecutionPlan { Confidence = 0f, Summary = "No skills matched query" };
        }

        var ordered = TopologicalSort(skills, _logger);
        var parallelGroups = DetectParallelGroups(ordered);

        var avgConfidence = ordered.Count > 0 ? ordered.Average(s => ComputeSkillScore(s)) : 0f;

        return new SkillExecutionPlan
        {
            OrderedSkills = ordered,
            ParallelGroups = parallelGroups,
            EstimatedSteps = parallelGroups.Count,
            Confidence = avgConfidence,
            Summary = $"Plan: {ordered.Count} skills in {parallelGroups.Count} steps (parallel groups: {string.Join(", ", parallelGroups)})"
        };
    }

    public async Task<Dictionary<string, object>> ExecutePlanAsync(SkillExecutionPlan plan, Func<SkillEntry, Task<object>> executor, CancellationToken ct = default)
    {
        var results = new Dictionary<string, object>();
        var stepResults = new List<Dictionary<string, object>>();

        foreach (var group in plan.ParallelGroups)
        {
            var groupSkills = group.Split(',').Select(s => s.Trim()).ToList();
            var groupTasks = groupSkills.Select(async skillName =>
            {
                var skill = plan.OrderedSkills.FirstOrDefault(s => s.ModuleName == skillName);
                if (skill == null) return (skillName, result: (object?)null, success: false);

                try
                {
                    var result = await executor(skill).ConfigureAwait(false);
                    RecordUsage(skill.ModuleName, true);
                    return (skill.ModuleName, result, success: true);
                }
                catch
                {
                    RecordUsage(skill.ModuleName, false);
                    return (skill.ModuleName, result: (object?)null, success: false);
                }
            }).ToList();

            var groupResults = await Task.WhenAll(groupTasks).ConfigureAwait(false);
            foreach (var (name, result, success) in groupResults)
            {
                if (result != null)
                    results[name] = result;

                stepResults.Add(new Dictionary<string, object>
                {
                    ["skill"] = name,
                    ["success"] = success,
                    ["has_result"] = result != null
                });
            }
        }

        return new Dictionary<string, object>
        {
            ["results"] = results,
            ["steps"] = stepResults,
            ["total_steps"] = plan.ParallelGroups.Count,
            ["successful_skills"] = results.Count
        };
    }

    private void AddWithDependencies(SkillEntry skill, List<SkillEntry> pipeline, HashSet<string> added)
    {
        if (added.Contains(skill.ModuleName)) return;

        foreach (var depName in skill.Dependencies)
        {
            var dep = _catalog.GetSkill(depName);
            if (dep != null)
            {
                AddWithDependencies(dep, pipeline, added);
            }
        }

        pipeline.Add(skill);
        added.Add(skill.ModuleName);
    }

    public void RecordUsage(string skillName, bool success)
    {
        lock (_lock)
        {
            if (!_progress.TryGetValue(skillName, out var progress))
            {
                progress = new SkillProgress { SkillName = skillName };
                _progress[skillName] = progress;
            }

            progress.UsageCount++;
            if (success) progress.SuccessCount++;

            CheckPromotion(progress);
        }
    }

    public SkillGrowth GetGrowth(string skillName)
    {
        lock (_lock)
        {
            var skill = _catalog.GetSkill(skillName);
            if (skill == null)
                return new SkillGrowth { SkillName = skillName };

            var progress = _progress.GetValueOrDefault(skillName, new SkillProgress { SkillName = skillName });
            var rate = progress.UsageCount > 0 ? (float)progress.SuccessCount / progress.UsageCount : 0f;

            var (nextMaturity, thresholdUsage, thresholdRate) = GetNextMaturity(skill.Maturity);
            var progressToNext = ComputeProgressToNext(progress, thresholdUsage, thresholdRate);

            return new SkillGrowth
            {
                SkillName = skillName,
                CurrentMaturity = skill.Maturity,
                UsageCount = progress.UsageCount,
                SuccessCount = progress.SuccessCount,
                SuccessRate = rate,
                NextMaturity = nextMaturity,
                ProgressToNext = progressToNext
            };
        }
    }

    public List<SkillGrowth> GetAllGrowth()
    {
        lock (_lock)
        {
            return _progress.Keys.Select(GetGrowth).OrderByDescending(g => g.ProgressToNext).ToList();
        }
    }

    private float ComputeSkillScore(SkillEntry skill)
    {
        lock (_lock)
        {
            var baseScore = skill.Maturity switch
            {
                SkillMaturity.Core => 1.0f,
                SkillMaturity.Stable => 0.7f,
                SkillMaturity.Experimental => 0.4f,
                _ => 0.3f
            };

            if (_progress.TryGetValue(skill.ModuleName, out var progress) && progress.UsageCount > 0)
            {
                var successRate = (float)progress.SuccessCount / progress.UsageCount;
                var usageBonus = Math.Min(progress.UsageCount / 50f, 0.3f);
                baseScore += successRate * 0.3f + usageBonus;
            }

            return Math.Clamp(baseScore, 0f, 1f);
        }
    }

    private void CheckPromotion(SkillProgress progress)
    {
        var skill = _catalog.GetSkill(progress.SkillName);
        if (skill == null) return;

        if (skill.Maturity == SkillMaturity.Core) return;

        var (nextMaturity, thresholdUsage, thresholdRate) = GetNextMaturity(skill.Maturity);
        var rate = (float)progress.SuccessCount / progress.UsageCount;

        if (progress.UsageCount >= thresholdUsage && rate >= thresholdRate)
        {
            _catalog.UpdateSkillMaturity(progress.SkillName, nextMaturity);
            _logger.LogInformation(
                "Skill promoted: {Skill} {From} -> {To} (usage={Usage}, rate={Rate:F2})",
                progress.SkillName, skill.Maturity, nextMaturity, progress.UsageCount, rate);
        }
    }

    private static (SkillMaturity Next, int Usage, float Rate) GetNextMaturity(SkillMaturity current)
    {
        return current switch
        {
            SkillMaturity.Experimental => (SkillMaturity.Stable, 5, 0.6f),
            SkillMaturity.Stable => (SkillMaturity.Core, 20, 0.8f),
            SkillMaturity.Core => (SkillMaturity.Core, 0, 1.0f),
            _ => (SkillMaturity.Experimental, 5, 0.6f)
        };
    }

    private static float ComputeProgressToNext(SkillProgress progress, int thresholdUsage, float thresholdRate)
    {
        if (thresholdUsage == 0) return 1.0f;

        var usageProgress = Math.Min((float)progress.UsageCount / thresholdUsage, 1.0f);
        var rate = progress.UsageCount > 0 ? (float)progress.SuccessCount / progress.UsageCount : 0f;
        var rateProgress = Math.Min(rate / thresholdRate, 1.0f);

        return usageProgress * 0.6f + rateProgress * 0.4f;
    }

    private static List<SkillEntry> TopologicalSort(List<SkillEntry> skills, ILogger? logger = null)
    {
        var inDegree = new Dictionary<string, int>();
        var graph = new Dictionary<string, List<string>>();

        foreach (var skill in skills)
        {
            inDegree[skill.ModuleName] = 0;
            graph[skill.ModuleName] = new();
        }

        foreach (var skill in skills)
        {
            foreach (var dep in skill.Dependencies)
            {
                if (graph.ContainsKey(dep))
                {
                    graph[dep].Add(skill.ModuleName);
                    inDegree[skill.ModuleName]++;
                }
            }
        }

        var queue = new Queue<string>(inDegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key));
        var sorted = new List<SkillEntry>();
        var skillMap = skills.ToDictionary(s => s.ModuleName);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (skillMap.TryGetValue(node, out var skill))
                sorted.Add(skill);

            foreach (var neighbor in graph[node])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        if (sorted.Count != skills.Count)
        {
            var cyclic = skills.Select(s => s.ModuleName).Except(sorted.Select(s => s.ModuleName)).ToList();
            logger?.LogWarning("Circular dependency detected in skills: {CyclicSkills}", string.Join(", ", cyclic));
        }

        return sorted.Count == skills.Count ? sorted : skills;
    }

    private static List<string> DetectParallelGroups(List<SkillEntry> orderedSkills)
    {
        if (orderedSkills.Count == 0) return new();

        var groups = new List<string>();
        var currentGroup = new List<string> { orderedSkills[0].ModuleName };

        for (int i = 1; i < orderedSkills.Count; i++)
        {
            var skill = orderedSkills[i];
            var hasDependencyInCurrentGroup = skill.Dependencies.Any(d => currentGroup.Contains(d));

            if (hasDependencyInCurrentGroup)
            {
                groups.Add(string.Join(",", currentGroup));
                currentGroup = new() { skill.ModuleName };
            }
            else
            {
                currentGroup.Add(skill.ModuleName);
            }
        }

        if (currentGroup.Count > 0)
            groups.Add(string.Join(",", currentGroup));

        return groups;
    }
}

internal sealed class SkillProgress
{
    public string SkillName { get; init; } = "";
    public int UsageCount { get; set; }
    public int SuccessCount { get; set; }
}
