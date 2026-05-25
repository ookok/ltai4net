using System.Text.RegularExpressions;

namespace LTAI.Agent.Resilience;

public sealed class DiagnosticInfo
{
    public string DiagnosticCode { get; init; } = "";
    public string SafetyLevel { get; init; } = "safe";

    public int LineNumber { get; init; }
    public string? FilePath { get; init; }
    public string? Message { get; init; }
}

public static class DiagnosticParser
{
    public static readonly Dictionary<string, List<string>> RepairCodeMap = new()
    {
        ["default"] = new() { "analyze-fix", "manual-fix" },
        ["CS8602"] = new() { "null-check", "conditional-access", "annotations-fix" },
        ["CS8600"] = new() { "null-check", "conditional-access" },
        ["CS8601"] = new() { "nullable-annotation", "type-fix" },
        ["CS8603"] = new() { "null-return-fix", "nullable-reference" },
        ["CS8604"] = new() { "argument-null-guard", "nullable-check" },
        ["CS0168"] = new() { "remove-unused", "discard-pattern" },
        ["CS0103"] = new() { "missing-import", "typo-fix", "class-creation" },
        ["CS0117"] = new() { "member-access-fix", "typo-fix" },
        ["CS0246"] = new() { "missing-using", "add-reference" },
        ["CS1061"] = new() { "member-access-fix", "extension-method" },
        ["CS1503"] = new() { "type-conversion", "argument-type-fix" },
        ["CS0161"] = new() { "return-path-fix", "default-return" },
        ["CS0029"] = new() { "explicit-cast", "type-conversion" },
        ["CS0030"] = new() { "explicit-cast", "collection-conversion" },
    };

    private static readonly Regex s_csharpErrorPattern = new(
        @"^(?<file>[^(]+)\((?<line>\d+),\d+\):\s*(?:error|warning)\s+(?<code>CS\d+):\s*(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private static readonly Regex s_dotnetBuildPattern = new(
        @"error\s+(?<code>CS\d+):\s*(?<message>.+?)(?:\s+\[.+\])?$",
        RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private static readonly Regex s_dotnetFileLinePattern = new(
        @"^(?<file>[^(]+)\((?<line>\d+),\d+\)",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex s_highRiskPattern = new(
        @"format|delete|rm |unlink|unsafe|security|injection|overflow",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static List<DiagnosticInfo> Parse(string tracebackText)
    {
        if (string.IsNullOrWhiteSpace(tracebackText))
            return new List<DiagnosticInfo>();

        var diagnostics = new List<DiagnosticInfo>();
        var seenCodes = new HashSet<string>();

        foreach (Match match in s_csharpErrorPattern.Matches(tracebackText))
        {
            var code = match.Groups["code"].Value;
            if (seenCodes.Add(code))
            {
                diagnostics.Add(new DiagnosticInfo
                {
                    DiagnosticCode = code,
                    SafetyLevel = ClassifySafetyLevel(match.Groups["message"].Value),
                    FilePath = match.Groups["file"].Value.Trim(),
                    LineNumber = int.TryParse(match.Groups["line"].Value, out var l) ? l : 0,
                    Message = match.Groups["message"].Value.Trim()
                });
            }
        }

        if (diagnostics.Count == 0)
        {
            foreach (Match match in s_dotnetBuildPattern.Matches(tracebackText))
            {
                var code = match.Groups["code"].Value;
                if (seenCodes.Add(code))
                {
                    diagnostics.Add(new DiagnosticInfo
                    {
                        DiagnosticCode = code,
                        SafetyLevel = ClassifySafetyLevel(match.Groups["message"].Value),
                        Message = match.Groups["message"].Value.Trim()
                    });
                }
            }
        }

        if (diagnostics.Count == 0)
        {
            var code = ExtractExceptionCode(tracebackText);
            if (!string.IsNullOrEmpty(code))
            {
                diagnostics.Add(new DiagnosticInfo
                {
                    DiagnosticCode = code,
                    SafetyLevel = "review",
                    Message = tracebackText.Length > 500 ? tracebackText[..500] : tracebackText
                });
            }
        }

        return diagnostics;
    }

    private static string ClassifySafetyLevel(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "safe";
        return s_highRiskPattern.IsMatch(message) ? "review" : "safe";
    }

    private static string ExtractExceptionCode(string traceback)
    {
        if (traceback.Contains("NullReferenceException")) return "NULLREF001";
        if (traceback.Contains("InvalidOperationException")) return "INVOP001";
        if (traceback.Contains("ArgumentException")) return "ARG001";
        if (traceback.Contains("IOException")) return "IO001";
        if (traceback.Contains("UnauthorizedAccessException")) return "AUTH001";
        if (traceback.Contains("TimeoutException")) return "TIMEOUT001";
        if (traceback.Contains("HttpRequestException")) return "HTTP001";
        return "";
    }

    public static string ToPromptContext(List<DiagnosticInfo> diagnostics)
    {
        if (diagnostics == null || diagnostics.Count == 0)
            return "";

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var d = diagnostics[i];
            sb.AppendLine($"- [{i + 1}] {d.DiagnosticCode}: {d.Message ?? "(no message)"}");
            if (!string.IsNullOrEmpty(d.FilePath))
                sb.AppendLine($"  File: {d.FilePath}, Line: {d.LineNumber}");
            sb.AppendLine($"  Safety: {d.SafetyLevel}");
        }
        return sb.ToString();
    }
}
