// ──────────────────────────────────────────────────────────────
//  GrammarCheckStep — 数据结构 + 配置选项
//  GrammarError, GrammarErrorSeverity, GrammarCheckOptions
// ──────────────────────────────────────────────────────────────

using System.Text.RegularExpressions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed partial class GrammarCheckStep
{
    internal static readonly Regex s_usingPattern = new(@"using\s+([\w.]+)\s*;", RegexOptions.Compiled);
    internal static readonly Regex s_httpUrlPattern = new(@"https?://[^\s""'》<>]+", RegexOptions.Compiled);
    internal static readonly Regex s_configKeyPattern = new(@"(?:getenv|env|config)\s*[\(:]\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record MinedRulesFile(DateTime MinedAt, int TotalFailures, System.Collections.Generic.List<MinedRuleEntry> rules);
    private sealed record MinedRuleEntry(string Title, string Description, string Pattern, int Frequency);
}

/// <summary>语法检查错误/警告的记录。</summary>
public sealed record GrammarError(
    string File,
    int Line,
    int Column,
    GrammarErrorSeverity Severity,
    string Category,
    string Code,
    string Message,
    string Source)
{
    /// <summary>是否为语法错误（会打断生成）。</summary>
    public bool IsError => Severity == GrammarErrorSeverity.Error;
}

/// <summary>语法检查的严重度级别。</summary>
public enum GrammarErrorSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>GrammarCheckStep 的配置选项。</summary>
public sealed class GrammarCheckOptions
{
    public bool EnableQuickParse { get; set; } = true;
    public bool EnableRuleEngine { get; set; } = true;
    public bool EnableClr { get; set; } = true;
    public bool EnableLspDiag { get; set; } = true;
}
