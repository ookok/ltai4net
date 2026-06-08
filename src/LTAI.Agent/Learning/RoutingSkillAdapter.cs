// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  RoutingSkillAdapter — integrates IRoutingSkillStore with
//  ExecutionEngine plan/execute cycle
//
//  Two integration points:
//    1. PlanAsync: adjusts confidence thresholds based on history
//    2. ExecuteAsync: records outcome after each handoff
//
//  This adapter wraps IExecutionEngine to transparently add
//  skill-based routing optimization.
// ═══════════════════════════════════════════════════════════════

using LTAI.Agent.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Learning;

/// <summary>
/// Decorator that adds routing skill adaptation to an IExecutionEngine.
/// Wraps PlanAsync to adjust confidence and ExecuteAsync to record outcomes.
/// </summary>
public sealed class RoutingSkillAdapter : IExecutionEngine, IDisposable
{
    private readonly IExecutionEngine _inner;
    private readonly IRoutingSkillStore _skillStore;
    private readonly ILogger<RoutingSkillAdapter> _logger;
    private bool _disposed;

    /// <inheritdoc />
    public event Action<Execution.ExecutionSpan>? OnSpan
    {
        add => _inner.OnSpan += value;
        remove => _inner.OnSpan -= value;
    }

    public RoutingSkillAdapter(
        IExecutionEngine inner,
        IRoutingSkillStore skillStore,
        ILogger<RoutingSkillAdapter>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _skillStore = skillStore ?? throw new ArgumentNullException(nameof(skillStore));
        _logger = logger ?? NullLogger<RoutingSkillAdapter>.Instance;
    }

    /// <inheritdoc />
    public async Task<Execution.ExecutionPlan> PlanAsync(string query, CancellationToken ct = default)
    {
        // Get base plan
        var plan = await _inner.PlanAsync(query, ct).ConfigureAwait(false);

        // Adjust confidence based on routing skill history
        var queryType = _skillStore.DetectQueryType(query);

        // Adjust confidence: if we have good history for this query type's
        // preferred specialist, boost the confidence
        var topForType = _skillStore.GetTopForQueryType(queryType, 1);
        if (topForType.Count > 0)
        {
            var (bestSpecialist, score) = topForType[0];
            var boost = _skillStore.GetConfidenceBoost(bestSpecialist);

            // Adjust specific handoff steps
            var adjustedSteps = plan.Steps.Select(step =>
            {
                if (step is Execution.HandoffStep hs &&
                    string.Equals(hs.SpecialistName, bestSpecialist, StringComparison.OrdinalIgnoreCase))
                {
                    // Boost this specialist's position
                    _logger.LogDebug(
                        "RoutingSkill: boosting '{Specialist}' confidence by {Boost:F2} " +
                        "(queryType={QueryType}, historyScore={Score:F2})",
                        bestSpecialist, boost, queryType, score);
                }
                return step;
            }).ToList();

            plan = plan with
            {
                Confidence = Math.Clamp(plan.Confidence + (float)boost, 0.1f, 1.0f),
                Steps = adjustedSteps,
            };
        }

        return plan;
    }

    /// <inheritdoc />
    public async Task<Execution.ExecutionResult> ExecuteAsync(
        Execution.ExecutionPlan plan, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var result = await _inner.ExecuteAsync(plan, ct).ConfigureAwait(false);

        // Record outcomes for each handoff step
        foreach (var step in plan.Steps)
        {
            if (step is Execution.HandoffStep hs)
            {
                var latencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
                var success = result.Success;
                var tokensUsed = result.Spans.Sum(s => s.InputTokens + s.OutputTokens);

                _skillStore.RecordOutcome(
                    plan.Query,
                    hs.SpecialistName,
                    success,
                    latencyMs,
                    tokensUsed);

                _logger.LogDebug(
                    "RoutingSkill: recorded outcome for '{Specialist}': " +
                    "success={Success}, latency={Latency}ms, tokens={Tokens}",
                    hs.SpecialistName, success, latencyMs, tokensUsed);
            }
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_skillStore is IDisposable d) d.Dispose();
    }
}
