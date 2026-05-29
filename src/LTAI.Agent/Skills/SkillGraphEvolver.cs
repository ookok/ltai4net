using System.Collections.Concurrent;
using LTAI.AI.Governors;
using LTAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Skills;

public sealed class SkillGraphEvolver
{
    private readonly SkillGraph _graph;
    private readonly SkillRegistry _registry;
    private readonly ILogger<SkillGraphEvolver> _logger;

    private readonly ConcurrentDictionary<string, double> _skillRewardCache = new();
    private readonly ConcurrentDictionary<(string, string), int> _coOccurrenceCounts = new();
    private const double DecayRate = 0.95;
    private const int MinCoOccurrenceForEdge = 5;
    private const double MinSuccessRateForEnhancement = 0.6;

    public SkillGraphEvolver(
        SkillGraph graph,
        SkillRegistry registry,
        ILogger<SkillGraphEvolver>? logger = null)
    {
        _graph = graph;
        _registry = registry;
        _logger = logger ?? NullLogger<SkillGraphEvolver>.Instance;
    }

    public void RecordSkillOutcome(string skillId, double reward, bool success)
    {
        var node = _graph.GetNode(skillId);
        if (node == null) return;

        node.UseCount++;
        node.LastUsedAt = DateTime.UtcNow;

        var oldRate = node.SuccessRate;
        var alpha = 1.0 / (node.UseCount + 1);
        node.SuccessRate = (1 - alpha) * oldRate + alpha * (success ? 1.0 : 0.0);

        _skillRewardCache[skillId] = reward;
    }

    public void RecordToolSequence(List<string> toolSequence, double totalReward, bool overallSuccess)
    {
        if (toolSequence.Count < 2) return;

        for (int i = 0; i < toolSequence.Count; i++)
        {
            var toolName = toolSequence[i];
            var skillNode = _graph.FindNodeByName(toolName)
                ?? _graph.FindNodeByName(NormalizeName(toolName));

            if (skillNode == null)
            {
                var normalizedId = $"skill_{NormalizeName(toolName)}";
                skillNode = new SkillNode
                {
                    Id = normalizedId,
                    Name = toolName,
                    Tags = new List<string> { toolName.ToLower() }
                };
                _graph.AddOrUpdateNode(skillNode);
            }

            RecordSkillOutcome(skillNode.Id, totalReward / toolSequence.Count, overallSuccess);
        }

        for (int i = 0; i < toolSequence.Count - 1; i++)
        {
            var srcNode = _graph.FindNodeByName(NormalizeName(toolSequence[i]));
            var tgtNode = _graph.FindNodeByName(NormalizeName(toolSequence[i + 1]));

            if (srcNode == null || tgtNode == null) continue;

            var key = (srcNode.Id, tgtNode.Id);
            _coOccurrenceCounts.AddOrUpdate(key, 1, (_, c) => c + 1);

            if (_coOccurrenceCounts[key] >= MinCoOccurrenceForEdge)
            {
                var boostedWeight = 1.0 + (_coOccurrenceCounts[key] - MinCoOccurrenceForEdge) * 0.1;
                _graph.AddOrUpdateEdge(srcNode.Id, tgtNode.Id,
                    SkillEdgeType.CoOccurrence, boostedWeight, _coOccurrenceCounts[key]);

                var edge = _graph.GetEdge(srcNode.Id, tgtNode.Id, SkillEdgeType.CoOccurrence);
                if (edge != null)
                {
                    if (overallSuccess)
                        edge.SuccessfulUses++;
                    else
                        edge.FailedUses++;
                }
            }
        }
    }

    public void LearnEnhancements(string baseSkillId, string enhancedSkillId,
        double rewardBefore, double rewardAfter)
    {
        var delta = rewardAfter - rewardBefore;
        if (delta > 0 && rewardAfter >= MinSuccessRateForEnhancement)
        {
            _graph.AddOrUpdateEdge(baseSkillId, enhancedSkillId,
                SkillEdgeType.Enhancement, Math.Clamp(delta, 0.1, 2.0), 1);
        }
    }

    public void LearnPrerequisites(string prerequisiteId, string dependentId)
    {
        _graph.AddOrUpdateEdge(prerequisiteId, dependentId,
            SkillEdgeType.Prerequisite, 1.5, 1);
    }

    public void LearnFromExperimentTraces(List<SkillGraphEvolver.ExperimentTrace> traces)
    {
        foreach (var trace in traces)
        {
            if (trace.ToolSequence.Count < 2) continue;

            RecordToolSequence(trace.ToolSequence, trace.Reward, trace.Success);

            foreach (var tool in trace.ToolSequence)
            {
                var skill = _graph.FindNodeByName(NormalizeName(tool));
                if (skill == null) continue;

                RecordSkillOutcome(skill.Id, trace.Reward, trace.Success);
            }
        }
    }

    public void EvolveFromRegistry()
    {
        foreach (var (_, skill) in _registry.All)
        {
            var existing = _graph.FindNodeByName(skill.Name);
            if (existing != null)
            {
                _graph.AddOrUpdateNode(new SkillNode
                {
                    Name = skill.Name,
                    LayerLevel = (int)skill.Layer,
                    Description = skill.Description ?? "",
                    Tags = skill.Tags ?? new List<string>(),
                    MarkdownPath = skill.SourceFile ?? ""
                });
                continue;
            }

            var node = new SkillNode
            {
                Id = $"skill_{NormalizeName(skill.Name)}",
                Name = skill.Name,
                LayerLevel = (int)skill.Layer,
                Description = skill.Description ?? "",
                Tags = skill.Tags ?? new List<string>(),
                MarkdownPath = skill.SourceFile ?? ""
            };

            _graph.AddOrUpdateNode(node);
        }
    }

    public void ApplyDecay()
    {
        var now = DateTime.UtcNow;
        var staleThreshold = TimeSpan.FromDays(30);

        foreach (var node in _graph.GetAllNodes())
        {
            if (now - node.LastUsedAt > staleThreshold)
            {
                node.UseCount = (int)(node.UseCount * DecayRate);
                node.SuccessRate *= DecayRate;
            }
        }
    }

    public async Task RunEpochAsync(List<SkillGraphEvolver.ExperimentTrace>? traces = null, CancellationToken ct = default)
    {
        _logger.LogInformation("SkillGraphEvolver epoch started");

        EvolveFromRegistry();

        if (traces != null)
            LearnFromExperimentTraces(traces);

        ApplyDecay();
        _graph.UpdateCentrality();

        _logger.LogInformation("SkillGraphEvolver epoch completed: {Nodes} nodes, {Edges} edges",
            _graph.NodeCount, _graph.EdgeCount);
    }

    private static string NormalizeName(string name) =>
        name.ToLower().Replace(' ', '_').Replace('-', '_').Replace('.', '_');

    /// <summary>Experiment trace record — replaces deleted ExperimentAnalyzer.</summary>
    public sealed record ExperimentTrace(string Task, bool Success, double Score, string Domain, List<string>? ToolSequence = null, double Reward = 0);
}
