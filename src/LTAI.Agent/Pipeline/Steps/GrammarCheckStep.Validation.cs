// ──────────────────────────────────────────────────────────────
//  GrammarCheckStep — 辅助方法 + CLR Validation
//  File extraction, error message building, rule engine helpers,
//  claim-level validation (imports, URLs, config keys).
// ──────────────────────────────────────────────────────────────

using System.Text;
using System.Text.RegularExpressions;
using LTAI.Agent.Tools.Review;

namespace LTAI.Agent.Pipeline.Steps;

public sealed partial class GrammarCheckStep
{
    private HashSet<string> ExtractWrittenFiles(MessageContext context)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, args, _) in context.ToolCalls)
            foreach (var path in ExtractPathsFromArgs(name, args))
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    files.Add(path);
        return files;
    }

    private static IEnumerable<string> ExtractPathsFromArgs(string toolName, string args)
    {
        var paths = new List<string>();
        foreach (System.Text.RegularExpressions.Match match in PipelineRegex.PathArgPattern().Matches(args))
            if (match.Success) { var p = match.Groups[1].Value; if (!string.IsNullOrEmpty(p)) paths.Add(p); }
        foreach (System.Text.RegularExpressions.Match match in PipelineRegex.JsonPathArgPattern().Matches(args))
            if (match.Success) { var p = match.Groups[1].Value; if (!string.IsNullOrEmpty(p)) paths.Add(p); }
        return paths;
    }

    private static List<Microsoft.Extensions.AI.ChatMessage> BuildGrammarErrorMessages(
        HashSet<string> writtenFiles, List<GrammarError> syntaxErrors,
        List<GrammarError> warnings, Dictionary<string, string>? deltaMap = null)
    {
        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();
        if (syntaxErrors.Count == 0 && warnings.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## ⚠️ 代码质量提示");
            foreach (var group in warnings.GroupBy(e => e.File))
            {
                sb.AppendLine($"\n### {group.Key}");
                foreach (var e in group)
                    sb.AppendLine($"  L{e.Line,-5} [{e.Code}] {e.Message}");
            }
            sb.AppendLine("\n以上为建议性提示，不影响编译。请酌情处理。");
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System, sb.ToString()));
            return messages;
        }
        var fixSb = new StringBuilder();
        fixSb.AppendLine("## ❌ 语法错误 — 请立即修复\n");
        fixSb.AppendLine("以下文件包含语法错误，**请自动修复后再继续**：\n");
        foreach (var group in syntaxErrors.GroupBy(e => e.File))
        {
            var deltaId = deltaMap?.GetValueOrDefault(group.Key);
            var deltaRef = deltaId != null ? $" `delta:{deltaId[..12]}`" : "";
            fixSb.AppendLine($"### {group.Key} ({group.Count()} 个错误){deltaRef}\n");
            foreach (var e in group)
                fixSb.AppendLine($"  L{e.Line}:{e.Column} [{e.Code}] {e.Message}");
            fixSb.AppendLine();
        }
        fixSb.AppendLine("### 修复指引\n1. 使用 edit 工具修正上述语法错误\n2. 修正后继续执行原任务\n3. 如不确定修复方式，可以询问用户");
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.System, fixSb.ToString()));
        if (warnings.Count > 0)
        {
            var warnSb = new StringBuilder();
            warnSb.AppendLine("## ⚠️ 附带代码质量提示");
            foreach (var group in warnings.GroupBy(e => e.File))
            {
                warnSb.AppendLine($"\n### {group.Key}");
                foreach (var e in group)
                    warnSb.AppendLine($"  L{e.Line,-5} [{e.Code}] {e.Message}");
            }
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.System, warnSb.ToString()));
        }
        return messages;
    }

    private static GrammarErrorSeverity MapRuleSeverity(string severity) => severity.ToLowerInvariant() switch
    {
        "error" => GrammarErrorSeverity.Error,
        "warning" => GrammarErrorSeverity.Warning,
        "info" => GrammarErrorSeverity.Info,
        _ => GrammarErrorSeverity.Info
    };

    private static string ExtractSnippet(string content, int row, int maxLen)
    {
        var lines = content.Split('\n');
        if (row >= 0 && row < lines.Length)
        {
            var line = lines[row].Trim();
            return line.Length <= maxLen ? line : line[..maxLen] + "...";
        }
        return "?";
    }

    private static void LoadMinedRules(ReviewRuleEngine engine)
    {
        try
        {
            var minedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".livingtree", "learn", "mined-rules.json");
            if (!File.Exists(minedPath)) return;
            var json = File.ReadAllText(minedPath);
            var mined = System.Text.Json.JsonSerializer.Deserialize<MinedRulesFile>(json);
            if (mined?.rules == null || mined.rules.Count == 0) return;
            foreach (var rule in mined.rules)
            {
                if (!string.IsNullOrEmpty(rule.Pattern))
                {
                    engine.AddRule(new ReviewRule
                    {
                        Id = $"MINE-{rule.Title.GetHashCode():x8}",
                        Name = rule.Title, Category = "mined",
                        Severity = "warning", Description = rule.Description,
                        Pattern = rule.Pattern, MessageTemplate = rule.Description
                    });
                }
            }
        }
        catch { }
    }

    private List<GrammarError> ValidateImports(string filePath, string content)
    {
        var errors = new List<GrammarError>();
        foreach (System.Text.RegularExpressions.Match m in s_usingPattern.Matches(content))
        {
            var ns = m.Groups[1].Value;
            if (!Directory.Exists(Path.Combine(_workspacePath, ns.Replace('.', Path.DirectorySeparatorChar)))
                && !ns.StartsWith("System", StringComparison.Ordinal))
            {
                errors.Add(new GrammarError(filePath, GetLineNumber(content, m.Index), 0,
                    GrammarErrorSeverity.Warning, "clr", "CLAIM_IMPORT_NOT_FOUND",
                    $"Import '{ns}' — no matching directory found", "CLR"));
            }
        }
        return errors;
    }

    private List<GrammarError> ValidateApiClaims(string content)
    {
        var errors = new List<GrammarError>();
        foreach (System.Text.RegularExpressions.Match m in s_httpUrlPattern.Matches(content))
        {
            var url = m.Groups[0].Value;
            if (url.Contains("localhost") || url.Contains("127.0.0.1"))
                errors.Add(new GrammarError("", 0, 0, GrammarErrorSeverity.Info,
                    "clr", "CLAIM_HARDCODED_URL",
                    $"Hardcoded URL '{url}' — consider making this configurable", "CLR"));
        }
        return errors;
    }

    private List<GrammarError> ValidateConfigClaims(string content)
    {
        var errors = new List<GrammarError>();
        foreach (System.Text.RegularExpressions.Match m in s_configKeyPattern.Matches(content))
        {
            var key = m.Groups[1].Value;
            if (key.Contains(" ") || key.Contains("\t"))
                errors.Add(new GrammarError("", 0, 0, GrammarErrorSeverity.Warning,
                    "clr", "CLAIM_INVALID_KEY",
                    $"Config key '{key}' contains whitespace — likely a typo", "CLR"));
        }
        return errors;
    }

    private static int GetLineNumber(string content, int index)
    {
        if (index <= 0) return 0;
        int line = 1;
        for (int i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }
}
