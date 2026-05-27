using LTAI.Agent.Skills;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// The main Skill Runtime — orchestrates variable scope, expression evaluation,
/// step execution, branching, and hooks to execute a Skill from start to finish.
/// </summary>
public sealed class SkillRuntime
{
    private readonly SkillRegistry _registry;
    private readonly ILogger<SkillRuntime> _logger;
    private SkillVarScope _scope;
    private SkillExpressionEngine _expr;
    private SkillStepExecutor _executor;
    private SkillHookEngine _hooks;
    private SkillBranchEngine _branches;

    public SkillRuntime(SkillRegistry registry, ILogger<SkillRuntime>? logger = null)
    {
        _registry = registry;
        _logger = logger ?? new NullLogger<SkillRuntime>();

        _scope = new SkillVarScope();
        _expr = new SkillExpressionEngine(_scope);
        _hooks = new SkillHookEngine();
        _branches = new SkillBranchEngine(_expr);
        _executor = new SkillStepExecutor(_registry, this, _scope, _expr, _hooks);
    }

    public void InjectContext(string query, string domain, string model)
    {
        _scope.InjectContext(query, domain, model);
    }

    public async Task<SkillRunResult> RunAsync(Skill skill, CancellationToken ct = default)
    {
        var output = new System.Text.StringBuilder();
        var steps = new List<SkillStepResult>();
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("SkillRuntime: running {Name} ({Steps} steps)", skill.Name, skill.Steps.Count);

        try
        {
            var retryCount = 0;
            for (int i = 0; i < skill.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = skill.Steps[i];

                var beforeResult = await _hooks.RunBeforeStepAsync(i, _scope, _expr).ConfigureAwait(false);
                if (beforeResult.ShouldRetry)
                {
                    retryCount++;
                    await Task.Delay(Math.Min(100 * (1 << Math.Min(retryCount, 4)), 3000), ct).ConfigureAwait(false);
                    i--;
                    continue;
                }

                retryCount = 0;

                SkillValue result;
                try
                {
                    result = await _executor.ExecuteStepAsync(step, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var errResult = await _hooks.RunOnErrorAsync(i, _scope, _expr, ex.Message).ConfigureAwait(false);
                    if (errResult.ShouldRetry)
                    {
                        retryCount++;
                        await Task.Delay(Math.Min(200 * (1 << Math.Min(retryCount, 4)), 5000), ct).ConfigureAwait(false);
                        i--;
                        continue;
                    }

                    retryCount = 0;

                    steps.Add(new SkillStepResult(step.Index, false, ex.Message, TimeSpan.Zero));
                    output.AppendLine($"[Error: {ex.Message}]");
                    break;
                }

                var afterResult = await _hooks.RunAfterStepAsync(i, _scope, _expr, result).ConfigureAwait(false);

                var text = result.Text;
                if (!string.IsNullOrEmpty(text) && text.Length < 2000)
                    output.AppendLine(text);

                steps.Add(new SkillStepResult(step.Index, true, text, TimeSpan.Zero));

                if (afterResult.ShouldRetry && i > 0)
                {
                    retryCount++;
                    await Task.Delay(Math.Min(100 * (1 << Math.Min(retryCount, 4)), 3000), ct).ConfigureAwait(false);
                    i -= 2;
                }
            }

            foreach (var rule in skill.Verification)
            {
                var finalOutput = output.ToString();
                if (rule.MustContain != null && !finalOutput.Contains(rule.MustContain, StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(new SkillStepResult(-1, false, $"Verification failed: missing '{rule.MustContain}'", TimeSpan.Zero));
                }

                if (rule.MustNotContain != null && finalOutput.Contains(rule.MustNotContain, StringComparison.OrdinalIgnoreCase))
                {
                    steps.Add(new SkillStepResult(-1, false, $"Verification failed: found '{rule.MustNotContain}'", TimeSpan.Zero));
                }
            }
        }
        catch (OperationCanceledException)
        {
            steps.Add(new SkillStepResult(-1, false, "Cancelled", TimeSpan.Zero));
        }

        var allPassed = steps.All(s => s.Success);
        if (allPassed)
            skill.Evolution.RecordSuccess();
        else
            skill.Evolution.RecordFailure();

        if (skill.SourceFile != null)
            SkillLoader.SaveEvolution(skill.SourceFile, skill.Evolution);

        return new SkillRunResult(
            skill.Name,
            output.ToString().Trim(),
            steps,
            DateTime.UtcNow - startTime,
            steps.All(s => s.Success));
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}

public sealed record SkillStepResult(int Index, bool Success, string Output, TimeSpan Duration);

public sealed record SkillRunResult(
    string SkillName,
    string Output,
    List<SkillStepResult> Steps,
    TimeSpan Duration,
    bool AllPassed);
