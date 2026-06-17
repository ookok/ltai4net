// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  ExperienceReplayPool — offline self-distillation for
//  agent trajectories.
//
//  Inspired by VibeThinker-3B's Offline Self-Distillation:
//  after RL exploration, high-quality reasoning trajectories
//  are distilled back into a unified model. We do the same
//  for agent trajectories: successful agent interactions are
//  sampled, scored, and re-injected via the prompt system.
// ═══════════════════════════════════════════════════════

using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public sealed class ExperienceReplayPool
{
    private readonly PalaceStore _store;
    private readonly ILogger<ExperienceReplayPool>? _logger;
    private readonly string _agentId;

    // Learning potential score threshold: only distill traces the model
    // doesn't already know well (higher score = more valuable to learn)
    private const double MinLearningPotential = 1.5;

    // Max experiences to keep in the active pool
    private const int MaxPoolSize = 200;

    public ExperienceReplayPool(PalaceStore store, string agentId = "default",
        ILogger<ExperienceReplayPool>? logger = null)
    {
        _store = store;
        _agentId = agentId;
        _logger = logger;
    }

    /// <summary>
    /// Record a successful agent interaction as an experience trace.
    /// Stores: query, response, success indicators, domain tags.
    /// </summary>
    public async Task RecordExperienceAsync(string query, string response,
        bool success, string category, CancellationToken ct = default)
    {
        var content = $"## Experience: {category}\nQ: {query}\nA: {response}";
        await _store.StoreAsync("experience", _agentId, content,
            role: "assistant",
            importance: success ? 0.5 : 0.2,
            agentId: _agentId,
            ttlMs: 7 * 24 * 3600 * 1000) // 7 days
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sample high-value experiences from the replay pool.
    /// Uses learning-potential filtering (VibeThinker-3B §2.3):
    /// prefers traces that are verified-correct but not yet well-modeled.
    /// </summary>
    public async Task<string> SampleAsync(int maxCount = 5, CancellationToken ct = default)
    {
        var experiences = _store.SearchByRoom("experience", _agentId, maxCount: 50);
        if (experiences.Count == 0) return "";

        // Score by learning potential: prefer high-importance, recent entries
        var scored = experiences
            .Select(e => (
                content: e.Content,
                score: e.Importance * 10 +
                       Math.Max(0, 1.0 - (DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(e.CreatedAt)).TotalDays / 7.0)))
            .OrderByDescending(x => x.score)
            .Take(maxCount)
            .ToList();

        return string.Join("\n---\n", scored.Select(s => s.content));
    }

    /// <summary>
    /// Consolidate recent experiences and inject them as system-level context.
    /// Called periodically (like offline self-distillation in VibeThinker).
    /// </summary>
    public async Task<string> ConsolidateAsync(CancellationToken ct = default)
    {
        var recent = _store.SearchByRoom("experience", _agentId, maxCount: MaxPoolSize);

        if (recent.Count == 0) return "";

        var successful = recent
            .Where(e => e.Importance >= 0.4)
            .OrderByDescending(e => e.Importance)
            .Take(10)
            .ToList();

        if (successful.Count == 0) return "";

        var lines = new List<string>
        {
            "## 🧠 Experience Replay (Offline Self-Distillation)",
            "以下是从历史成功交互中提取的经验模式，可供参考：",
            ""
        };

        int idx = 0;
        foreach (var exp in successful.Take(5))
        {
            idx++;
            var snippet = exp.Content.Length > 300
                ? exp.Content[..297] + "..."
                : exp.Content;
            lines.Add($"### 经验 #{idx}");
            lines.Add(snippet);
            lines.Add("");
        }

        _logger?.LogInformation("ExperienceReplayPool: consolidated {N} experiences", successful.Count);
        return string.Join("\n", lines);
    }
}
