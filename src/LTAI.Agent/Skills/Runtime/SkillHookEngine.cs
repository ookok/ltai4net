using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// Lifecycle hooks for Skill execution.
/// </summary>
public sealed class SkillHookEngine
{
    private readonly List<SkillAction> _beforeEach = new();
    private readonly List<SkillAction> _afterEach = new();
    private readonly List<SkillAction> _onError = new();
    private readonly ILogger<SkillHookEngine> _logger;

    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;

    public SkillHookEngine(ILogger<SkillHookEngine>? logger = null)
    {
        _logger = logger ?? new NullLogger<SkillHookEngine>();
    }

    public void LoadFromSkill(string section, List<string> lines)
    {
        var actions = ParseActions(lines);

        switch (section.ToLowerInvariant())
        {
            case "before_each": _beforeEach.AddRange(actions); break;
            case "after_each": _afterEach.AddRange(actions); break;
            case "on_error": _onError.AddRange(actions); break;
        }
    }

    private static List<SkillAction> ParseActions(List<string> lines)
    {
        var actions = new List<SkillAction>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim().TrimStart('-').Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.StartsWith("记录") || trimmed.StartsWith("log"))
                actions.Add(new SkillAction(SkillActionType.Log, trimmed));
            else if (trimmed.Contains("重试"))
                actions.Add(new SkillAction(SkillActionType.Retry, trimmed));
            else
                actions.Add(new SkillAction(SkillActionType.Set, trimmed));
        }
        return actions;
    }

    public async Task<HookResult> RunBeforeStepAsync(int stepIndex, SkillVarScope scope, SkillExpressionEngine expr)
    {
        return await RunHooksAsync(_beforeEach, scope, expr).ConfigureAwait(false);
    }

    public async Task<HookResult> RunAfterStepAsync(int stepIndex, SkillVarScope scope, SkillExpressionEngine expr, SkillValue stepResult)
    {
        scope.Set("_last_result", stepResult);
        return await RunHooksAsync(_afterEach, scope, expr).ConfigureAwait(false);
    }

    public async Task<HookResult> RunOnErrorAsync(int stepIndex, SkillVarScope scope, SkillExpressionEngine expr, string error)
    {
        scope.Set("_error", SkillValue.FromString(error));
        scope.Set("_retry_count", SkillValue.FromNumber(RetryCount));
        return await RunHooksAsync(_onError, scope, expr).ConfigureAwait(false);
    }

    private async Task<HookResult> RunHooksAsync(List<SkillAction> hooks, SkillVarScope scope, SkillExpressionEngine expr)
    {
        foreach (var hook in hooks)
        {
            switch (hook.Type)
            {
                case SkillActionType.Retry when RetryCount < MaxRetries:
                    RetryCount++;
                    return new HookResult { ShouldRetry = true };

                case SkillActionType.Set:
                    var parts = hook.Content.Split("→", 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2)
                    {
                        var valExpr = expr.Interpolate(parts[1]);
                        var varName = parts[0].Replace("$", "").Trim();
                        scope.Set(varName, SkillValue.FromString(valExpr));
                    }
                    break;

                case SkillActionType.Log:
                    var msg = expr.Interpolate(hook.Content);
                    _logger.LogInformation("SkillHook: {Message}", msg);
                    break;
            }
        }

        await Task.CompletedTask;
        return new HookResult { ShouldRetry = false };
    }
}

public sealed record SkillAction(SkillActionType Type, string Content);
public enum SkillActionType { Set, Log, Retry }

public sealed record HookResult
{
    public bool ShouldRetry { get; init; }
}

internal sealed class NullLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}
