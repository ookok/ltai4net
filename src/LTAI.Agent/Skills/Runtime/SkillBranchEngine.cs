using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Skills.Runtime;

/// <summary>
/// Evaluates ## 分支 when conditions and selects execution paths.
/// </summary>
public sealed class SkillBranchEngine
{
    private readonly SkillExpressionEngine _expr;
    private readonly ILogger<SkillBranchEngine> _logger;

    public SkillBranchEngine(SkillExpressionEngine expr, ILogger<SkillBranchEngine>? logger = null)
    {
        _expr = expr;
        _logger = logger ?? new NullLogger<SkillBranchEngine>();
    }

    public record BranchBlock(string Condition, List<SkillStep> Steps);

    /// <summary>
    /// Parse ## 分支 when blocks from skill markdown.
    /// </summary>
    public static List<BranchBlock> ParseBranches(List<string> lines)
    {
        var branches = new List<BranchBlock>();
        BranchBlock? current = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("## 分支") && line.Contains("when"))
            {
                if (current != null) branches.Add(current);

                var condStart = line.IndexOf("when") + 4;
                var condition = line[condStart..].Trim();
                current = new BranchBlock(condition, new List<SkillStep>());
            }
            else if (current != null && line.StartsWith("##"))
            {
                branches.Add(current);
                current = null;
            }
            else if (current != null && char.IsDigit(line.TrimStart()[0]))
            {
                var step = ParseStepLine(line, current.Steps.Count + 1);
                if (step != null) current.Steps.Add(step);
            }
        }

        if (current != null) branches.Add(current);
        return branches;
    }

    /// <summary>
    /// Evaluate all branches and return the first matching one.
    /// </summary>
    public BranchBlock? SelectBranch(List<BranchBlock> branches)
    {
        foreach (var branch in branches)
        {
            var result = _expr.Evaluate(branch.Condition);
            _logger.LogDebug("Branch: when {Condition} = {Result}", branch.Condition, result.Bool);
            if (result.Bool) return branch;
        }

        return null;
    }

    private static SkillStep? ParseStepLine(string line, int index)
    {
        var trimmed = line.Trim();
        var dotIdx = trimmed.IndexOf('.');

        if (dotIdx <= 0 || !char.IsDigit(trimmed[0]))
            return null;

        var action = trimmed[(dotIdx + 1)..].Trim();

        string? refSkill = null;
        string? toolName = null;

        var refMatch = System.Text.RegularExpressions.Regex.Match(action, @"→\s*(\w[\w_]*)");
        if (refMatch.Success)
        {
            refSkill = refMatch.Groups[1].Value;
            action = action.Replace(refMatch.Value, "").Trim();
        }

        if (action.StartsWith("shell:")) toolName = "shell";
        else if (action.StartsWith("regex:")) toolName = "regex";

        return new SkillStep
        {
            Index = index,
            Action = action,
            SkillRef = refSkill,
            ToolName = toolName
        };
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
