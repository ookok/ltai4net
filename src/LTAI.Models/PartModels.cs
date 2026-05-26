namespace LTAI.Models;

public abstract record Part(string Id)
{
    public string MessageId { get; init; } = "";
    public int Seq { get; init; }
}

public sealed record TextPart(string Id, string Text) : Part(Id);
public sealed record ReasoningPart(string Id, string Text, bool Collapsible = true) : Part(Id);

public sealed record ToolInvocationPart(
    string Id, string ToolName, object? Input,
    ToolState State = ToolState.Pending,
    object? Output = null, string? Error = null
) : Part(Id);

public sealed record DiagnosticInfo
{
    public string FilePath { get; init; } = "";
    public int LineNumber { get; init; }
    public int ColumnNumber { get; init; }
    public string Severity { get; init; } = "";  // error, warning, info
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed record FilePart(string Id, string Path, string? Content = null,
    string? ChangeType = null, DiagnosticInfo[]? Diagnostics = null) : Part(Id);

public sealed record AgentPart(string Id, string AgentName, string SessionId,
    string? Summary = null) : Part(Id);

public enum ToolState { Pending, Executing, Completed, Error }

public static class DiagnosticParser
{
    private static readonly (string label, string regex)[] Patterns =
    {
        ("dotnet/tsc", @"^(.+?)\((\d+),(\d+)\):\s*(error|warning)\s+(\w+):\s*(.+)$"),
        ("python/mypy", @"^(.+?):(\d+):(\d+)?:\s*(error|warning):\s*(.+?)?\s*\[(\w[\w-]*)\]\s*$"),
        ("pylint/flake8", @"^(.+?):(\d+):(\d+)?:\s*(\w)(\d+)\s+(.+)$"),
        ("gcc/clang", @"^(.+?):(\d+):(\d+):\s*(fatal error|error|warning):\s*(.+)$"),
        ("go", @"^(.+?):(\d+):(\d+):\s*(.+)$"),
        ("eslint/tslint", @"^\s*(.+?)\s+(\d+):(\d+)\s+(error|warning)\s+(.+?)\s+(\S[\w-]*)$"),
        ("rust", @"^\s*(error|warning)\[(\w+)\]:\s*(.+)$"),
    };

    public static string DetectLanguage(string workspaceRoot)
    {
        try
        {
            if (File.Exists(Path.Combine(workspaceRoot, "*.csproj").Replace("*", "")))
                foreach (var f in Directory.GetFiles(workspaceRoot, "*.csproj", SearchOption.TopDirectoryOnly))
                    return "dotnet";
            if (File.Exists(Path.Combine(workspaceRoot, "package.json")) && File.Exists(Path.Combine(workspaceRoot, "tsconfig.json")))
                return "tsc";
            if (File.Exists(Path.Combine(workspaceRoot, "pyproject.toml")) || File.Exists(Path.Combine(workspaceRoot, "setup.py")))
                return "python";
            if (File.Exists(Path.Combine(workspaceRoot, "go.mod")))
                return "go";
            if (File.Exists(Path.Combine(workspaceRoot, "Cargo.toml")))
                return "rust";
            if (File.Exists(Path.Combine(workspaceRoot, "Makefile")) || File.Exists(Path.Combine(workspaceRoot, "CMakeLists.txt")))
                return "gcc";
            return "auto";
        }
        catch { return "auto"; }
    }

    public static DiagnosticInfo[] ParseBuildOutput(string buildOutput, string? language = null)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var lines = buildOutput.Split('\n');

        foreach (var line in lines)
        {
            DiagnosticInfo? diag = null;

            foreach (var (label, regex) in Patterns)
            {
                if (language != null && language != "auto" && !label.StartsWith(language))
                    continue;

                var match = System.Text.RegularExpressions.Regex.Match(line.Trim(), regex);
                if (!match.Success) continue;

                diag = label switch
                {
                    "dotnet/tsc" => new DiagnosticInfo
                    {
                        FilePath = match.Groups[1].Value,
                        LineNumber = int.Parse(match.Groups[2].Value),
                        ColumnNumber = int.Parse(match.Groups[3].Value),
                        Severity = match.Groups[4].Value,
                        Code = match.Groups[5].Value,
                        Message = match.Groups[6].Value
                    },
                    "python/mypy" => new DiagnosticInfo
                    {
                        FilePath = match.Groups[1].Value,
                        LineNumber = int.Parse(match.Groups[2].Value),
                        ColumnNumber = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 1,
                        Severity = match.Groups[4].Value,
                        Code = match.Groups[5].Value,
                        Message = match.Groups[5].Value
                    },
                    "pylint/flake8" => new DiagnosticInfo
                    {
                        FilePath = match.Groups[1].Value,
                        LineNumber = int.Parse(match.Groups[2].Value),
                        ColumnNumber = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 1,
                        Severity = match.Groups[4].Value == "E" || match.Groups[4].Value == "F" ? "error" : "warning",
                        Code = match.Groups[4].Value + match.Groups[5].Value,
                        Message = match.Groups[6].Value
                    },
                    "gcc/clang" => new DiagnosticInfo
                    {
                        FilePath = match.Groups[1].Value,
                        LineNumber = int.Parse(match.Groups[2].Value),
                        ColumnNumber = int.Parse(match.Groups[3].Value),
                        Severity = match.Groups[4].Value.Contains("fatal") ? "error" : match.Groups[4].Value,
                        Code = match.Groups[4].Value,
                        Message = match.Groups[5].Value
                    },
                    "go" => new DiagnosticInfo
                    {
                        FilePath = match.Groups[1].Value,
                        LineNumber = int.Parse(match.Groups[2].Value),
                        ColumnNumber = int.Parse(match.Groups[3].Value),
                        Severity = "error",
                        Code = "build",
                        Message = match.Groups[4].Value
                    },
                    "eslint/tslint" => new DiagnosticInfo
                    {
                        FilePath = match.Groups[1].Value,
                        LineNumber = int.Parse(match.Groups[2].Value),
                        ColumnNumber = int.Parse(match.Groups[3].Value),
                        Severity = match.Groups[4].Value == "error" ? "error" : "warning",
                        Code = match.Groups[6].Value,
                        Message = match.Groups[5].Value
                    },
                    "rust" => new DiagnosticInfo
                    {
                        FilePath = "", LineNumber = 0, ColumnNumber = 0,
                        Severity = match.Groups[1].Value, Code = match.Groups[2].Value,
                        Message = match.Groups[3].Value
                    },
                    _ => null
                };

                if (diag != null)
                {
                    diagnostics.Add(diag);
                    break;
                }
            }

            if (diag == null && language == "rust")
            {
                var ptrMatch = System.Text.RegularExpressions.Regex.Match(line.Trim(),
                    @"-->\s*(.+?):(\d+):(\d+)");
                if (ptrMatch.Success && diagnostics.Count > 0)
                {
                    var last = diagnostics[^1];
                    last = last with
                    {
                        FilePath = ptrMatch.Groups[1].Value,
                        LineNumber = int.Parse(ptrMatch.Groups[2].Value),
                        ColumnNumber = int.Parse(ptrMatch.Groups[3].Value)
                    };
                    diagnostics[^1] = last;
                }
            }
        }

        return diagnostics.ToArray();
    }

    public static string BuildDiagnosticContext(DiagnosticInfo[] diagnostics)
    {
        if (diagnostics.Length == 0) return "";
        var byFile = diagnostics.GroupBy(d => d.FilePath).Take(10);
        var lines = new List<string> { $"## Build Diagnostics ({diagnostics.Length} issues)" };
        foreach (var group in byFile)
        {
            var shortPath = group.Key.Contains(Path.DirectorySeparatorChar)
                ? group.Key[(group.Key.LastIndexOf(Path.DirectorySeparatorChar) + 1)..]
                : group.Key;
            if (string.IsNullOrEmpty(shortPath)) shortPath = group.Key;
            foreach (var d in group.Take(5))
            {
                var icon = d.Severity == "error" ? "✗" : "⚠";
                lines.Add($"- {icon} {shortPath}:{d.LineNumber} [{d.Code}] {d.Message}");
            }
            if (group.Count() > 5)
                lines.Add($"  ... and {group.Count() - 5} more in {shortPath}");
        }
        if (diagnostics.Length > 20)
            lines.Add($"... {diagnostics.Length - 20} more issues not shown");
        return string.Join("\n", lines);
    }
}
