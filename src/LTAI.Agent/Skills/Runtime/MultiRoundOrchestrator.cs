using LTAI.AI.Interfaces;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// Executes long user queries as ordered multi-round conversations.
/// Each round inherits context from previous rounds, matched Skills guide execution.
/// </summary>
public sealed class MultiRoundOrchestrator
{
    private readonly SkillAwareDecomposer _decomposer;
    private readonly SkillRegistry _registry;
    private readonly SkillRuntime _runtime;
    private readonly ILivingTreeSystem _lts;
    private readonly ILogger<MultiRoundOrchestrator> _logger;

    public MultiRoundOrchestrator(
        SkillAwareDecomposer decomposer,
        SkillRegistry registry,
        SkillRuntime runtime,
        ILivingTreeSystem lts,
        ILogger<MultiRoundOrchestrator>? logger = null)
    {
        _decomposer = decomposer;
        _registry = registry;
        _runtime = runtime;
        _lts = lts;
        _logger = logger ?? new NullLogger<MultiRoundOrchestrator>();
    }

    public async IAsyncEnumerable<MultiRoundEvent> ExecuteAsync(
        string query,
        string domain = "general",
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("MultiRoundOrchestrator: starting for query ({Len} chars)", query.Length);

        if (!_decomposer.NeedsDecomposition(query))
        {
            yield return new MultiRoundEvent(MultiRoundPhase.SingleRound, "直接执行", query);
            yield break;
        }

        yield return new MultiRoundEvent(MultiRoundPhase.Decomposing, "拆解任务...", "");

        var rounds = await _decomposer.DecomposeAsync(query, domain, ct).ConfigureAwait(false);

        if (rounds.Count <= 1)
        {
            yield return new MultiRoundEvent(MultiRoundPhase.SingleRound, "简单任务，直接执行", query);
            yield break;
        }

        _logger.LogInformation("MultiRoundOrchestrator: decomposed into {Count} rounds", rounds.Count);
        yield return new MultiRoundEvent(MultiRoundPhase.PlanReady, $"拆解为 {rounds.Count} 个步骤", "");

        var contextChain = new ContextChain(maxTokens: 8000);
        var allResults = new List<string>();

        for (int i = 0; i < rounds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var round = rounds[i];

            var skillHints = BuildSkillHints(round);
            var prompt = contextChain.BuildPrompt(round.Goal, skillHints);

            yield return new MultiRoundEvent(MultiRoundPhase.RoundStart,
                $"步骤 {round.Index}/{rounds.Count}: {round.Goal}", prompt);

            string result;
            try
            {
                result = await _lts.ChatAsync(prompt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Round {Index} failed", round.Index);
                result = $"[步骤执行失败: {ex.Message}]";
            }

            contextChain.AddRound(round.Index, round.Goal, result);
            allResults.Add(result);

            yield return new MultiRoundEvent(MultiRoundPhase.RoundComplete,
                $"步骤 {round.Index} 完成 ({result.Length} chars)", result);

            _logger.LogInformation("MultiRound: round {Index}/{Total} done ({Len} chars)",
                round.Index, rounds.Count, result.Length);
        }

        yield return new MultiRoundEvent(MultiRoundPhase.Synthesizing, "综合所有步骤结果...", "");

        var synthesisPrompt = contextChain.BuildSynthesisPrompt(query, domain);
        string finalResult;
        try
        {
            finalResult = await _lts.ChatAsync(synthesisPrompt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Synthesis failed");
            finalResult = string.Join("\n\n---\n\n", allResults);
        }

        yield return new MultiRoundEvent(MultiRoundPhase.Complete,
            $"完成 ({rounds.Count} 步骤, {finalResult.Length} chars)", finalResult);
    }

    private string? BuildSkillHints(RoundPlan round)
    {
        if (round.MatchedSkillIds.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("相关技能:");

        foreach (var skillId in round.MatchedSkillIds)
        {
            var skill = _registry.Get(skillId);
            if (skill != null)
            {
                sb.AppendLine($"  {skill.Name}: {skill.Intent}");
                foreach (var step in skill.Steps)
                    sb.AppendLine($"    {step.Index}. {step.Action}");
            }
        }

        return sb.ToString();
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}

public enum MultiRoundPhase
{
    Decomposing,
    PlanReady,
    SingleRound,
    RoundStart,
    RoundComplete,
    Synthesizing,
    Complete
}

public sealed record MultiRoundEvent(MultiRoundPhase Phase, string Description, string Content);
