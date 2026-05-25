using System.Text.RegularExpressions;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// Executes individual Skill steps: shell commands, regex matches,
/// code analysis, and sub-skill references.
/// </summary>
public sealed class SkillStepExecutor
{
    private readonly SkillRegistry _registry;
    private readonly SkillRuntime _runtime;
    private readonly SkillVarScope _scope;
    private readonly SkillExpressionEngine _expr;
    private readonly SkillHookEngine _hooks;
    private readonly ILogger<SkillStepExecutor> _logger;

    public SkillStepExecutor(
        SkillRegistry registry,
        SkillRuntime runtime,
        SkillVarScope scope,
        SkillExpressionEngine expr,
        SkillHookEngine hooks,
        ILogger<SkillStepExecutor>? logger = null)
    {
        _registry = registry;
        _runtime = runtime;
        _scope = scope;
        _expr = expr;
        _hooks = hooks;
        _logger = logger ?? new NullLogger<SkillStepExecutor>();
    }

    public async Task<SkillValue> ExecuteStepAsync(SkillStep step, CancellationToken ct)
    {
        _logger.LogDebug("SkillStep: {Index}. {Action}", step.Index, step.Action);

        var action = _expr.Interpolate(step.Action);

        SkillValue? result = null;

        if (step.SkillRef != null)
        {
            result = await ExecuteSubSkillAsync(step, action, ct).ConfigureAwait(false);
        }
        else if (step.ToolName != null)
        {
            result = await ExecuteToolAsync(step, action, ct).ConfigureAwait(false);
        }

        if (result != null)
        {
            var varName = ExtractCaptureVariable(action);
            if (varName != null)
                _scope.Set(varName, result.Value);
        }

        return result ?? SkillValue.FromString(action);
    }

    private async Task<SkillValue> ExecuteSubSkillAsync(SkillStep step, string action, CancellationToken ct)
    {
        var skillName = step.SkillRef!;

        var skill = _registry.Get(skillName);
        if (skill == null)
            return SkillValue.FromString($"[Skill not found: {skillName}]");

        var result = await _runtime.RunAsync(skill, ct).ConfigureAwait(false);
        return SkillValue.FromString(result.Output);
    }

    private async Task<SkillValue> ExecuteToolAsync(SkillStep step, string action, CancellationToken ct)
    {
        return step.ToolName switch
        {
            "shell" => await RunShellAsync(action, ct).ConfigureAwait(false),
            "regex" => RunRegex(action),
            _ => SkillValue.FromString($"[Unknown tool: {step.ToolName}]")
        };
    }

    private async Task<SkillValue> RunShellAsync(string command, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c \"{command}\"" : $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return SkillValue.FromString("[Process start failed]");

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var result = output;
            if (!string.IsNullOrEmpty(error))
                result += "\n" + error;

            return SkillValue.FromString(result.Trim());
        }
        catch (Exception ex)
        {
            return SkillValue.FromString($"[Shell error: {ex.Message}]");
        }
    }

    private SkillValue RunRegex(string action)
    {
        var parts = ParseRegexAction(action);
        if (parts.Pattern == null)
            return SkillValue.FromString("[Invalid regex pattern]");

        var input = parts.Input ?? "";
        try
        {
            var regex = new Regex(parts.Pattern, RegexOptions.Compiled);
            var matches = regex.Matches(input);
            var results = new List<SkillValue>();
            foreach (Match m in matches)
            {
                if (m.Groups.Count > 1)
                {
                    var groups = new Dictionary<string, SkillValue>();
                    for (int i = 1; i < m.Groups.Count; i++)
                        groups[$"g{i}"] = SkillValue.FromString(m.Groups[i].Value);
                    results.Add(SkillValue.FromMap(groups));
                }
                else
                {
                    results.Add(SkillValue.FromString(m.Value));
                }
            }

            return results.Count > 0
                ? SkillValue.FromList(results)
                : SkillValue.FromString(matches.Count > 0 ? matches[0].Value : "");
        }
        catch (Exception ex)
        {
            return SkillValue.FromString($"[Regex error: {ex.Message}]");
        }
    }

    private static (string? Pattern, string? Input) ParseRegexAction(string action)
    {
        var pattern = "";
        string? input = null;

        var patternMatch = Regex.Match(action, @"([^\s]+(?:\([^)]*\))?)");
        if (patternMatch.Success)
            pattern = patternMatch.Value;

        var fromMatch = Regex.Match(action, @"from\s+\$(\w[\w_.]*)");
        if (fromMatch.Success)
            input = fromMatch.Groups[1].Value;

        return (pattern, input);
    }

    public static string? ExtractCaptureVariable(string action)
    {
        var match = Regex.Match(action, @"→\s*\$(\w[\w_.]*)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
