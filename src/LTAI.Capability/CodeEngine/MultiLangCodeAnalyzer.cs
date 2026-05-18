using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Capability.CodeEngine;

public sealed class MultiLangCodeAnalyzer
{
    private readonly ILogger<MultiLangCodeAnalyzer> _logger;

    public MultiLangCodeAnalyzer(ILogger<MultiLangCodeAnalyzer> logger)
    {
        _logger = logger;
    }

    public CodeAnalysisResult Analyze(string code, CodeLanguage language)
    {
        var info = LanguageRegistry.Get(language);
        var result = new CodeAnalysisResult
        {
            Language = language,
            LanguageName = info.Name,
            TotalLines = CountLines(code),
            CodeLines = CountCodeLines(code, info),
            CommentLines = CountCommentLines(code, info),
            BlankLines = CountBlankLines(code),
        };

        result.Functions = ExtractFunctions(code, info);
        result.Classes = ExtractClasses(code, info);
        result.Imports = ExtractImports(code, info);
        result.Complexity = EstimateComplexity(code, info);

        _logger.LogInformation("Code analyzed: {Lang}, {Lines} lines, {Funcs} functions, {Classes} classes, complexity={Cx}",
            info.Name, result.TotalLines, result.Functions.Count, result.Classes.Count, result.Complexity);

        return result;
    }

    public async Task<List<string>> ExtractDependenciesAsync(
        string code, CodeLanguage language, CancellationToken cancellationToken = default)
    {
        var info = LanguageRegistry.Get(language);
        var imports = ExtractImports(code, info);
        return await Task.FromResult(imports.Select(i => i.Module).Distinct().ToList());
    }

    public CodeQualityReport CheckQuality(string code, CodeLanguage language)
    {
        var info = LanguageRegistry.Get(language);
        var report = new CodeQualityReport();

        var lines = code.Split('\n');
        report.TotalLines = lines.Length;

        foreach (var line in lines)
            if (line.Length > 150) report.LongLines++;

        if (language == CodeLanguage.Python)
        {
            var indentCounts = new Dictionary<int, int>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(info.SingleLineComment)) continue;
                var indent = line.Length - line.TrimStart().Length;
                if (indent > 0)
                {
                    indentCounts.TryGetValue(indent, out var count);
                    indentCounts[indent] = count + 1;
                }
            }
            if (indentCounts.Any(kvp => kvp.Key % 4 != 0)) report.InconsistentIndent = true;
        }

        var todoRx = new Regex(@"TODO|FIXME|HACK|XXX", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        report.TodoCount = todoRx.Matches(code).Count;

        return report;
    }

    private static int CountLines(string code) =>
        code.Split('\n').Length;

    private static int CountCodeLines(string code, LanguageInfo info)
    {
        return code.Split('\n')
            .Count(line => !string.IsNullOrWhiteSpace(line) &&
                           !line.TrimStart().StartsWith(info.SingleLineComment) &&
                           !IsMultiLineComment(line, info));
    }

    private static int CountCommentLines(string code, LanguageInfo info)
    {
        return code.Split('\n')
            .Count(line => line.TrimStart().StartsWith(info.SingleLineComment) ||
                           IsMultiLineComment(line, info));
    }

    private static int CountBlankLines(string code) =>
        code.Split('\n').Count(string.IsNullOrWhiteSpace);

    private static bool IsMultiLineComment(string line, LanguageInfo info)
    {
        var trimmed = line.Trim();
        return !string.IsNullOrEmpty(info.MultiLineCommentStart) &&
               (trimmed.StartsWith(info.MultiLineCommentStart) || trimmed.EndsWith(info.MultiLineCommentEnd));
    }

    private static List<CodeFunction> ExtractFunctions(string code, LanguageInfo info)
    {
        var functions = new List<CodeFunction>();
        var lines = code.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            string? name = null;
            int paramCount = 0;

            switch (info.Language)
            {
                case CodeLanguage.Python:
                    var pyMatch = Regex.Match(line, @"^\s*def\s+(\w+)\s*\((.*?)\)");
                    if (pyMatch.Success) { name = pyMatch.Groups[1].Value; paramCount = pyMatch.Groups[2].Value.Split(',').Count(p => !string.IsNullOrWhiteSpace(p)); }
                    break;
                case CodeLanguage.CSharp:
                case CodeLanguage.Java:
                    var csMatch = Regex.Match(line, @"(?:public|private|protected|internal|static|\s)+(\w+)\s+(\w+)\s*\((.*?)\)");
                    if (csMatch.Success && !line.Contains("class ")) { name = csMatch.Groups[2].Value; paramCount = csMatch.Groups[3].Value.Split(',').Count(p => !string.IsNullOrWhiteSpace(p)); }
                    break;
                case CodeLanguage.JavaScript:
                case CodeLanguage.TypeScript:
                    var jsMatch = Regex.Match(line, @"(?:function\s+(\w+)|(\w+)\s*[=:]\s*(?:async\s*)?\(|(\w+)\s*\((.*?)\)\s*\{)");
                    if (jsMatch.Success) { name = jsMatch.Groups[1].Success ? jsMatch.Groups[1].Value : jsMatch.Groups[2].Success ? jsMatch.Groups[2].Value : jsMatch.Groups[3].Value; }
                    break;
                case CodeLanguage.Go:
                    var goMatch = Regex.Match(line, @"^\s*func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)\s*\((.*?)\)");
                    if (goMatch.Success) { name = goMatch.Groups[1].Value; paramCount = goMatch.Groups[2].Value.Split(',').Count(p => !string.IsNullOrWhiteSpace(p)); }
                    break;
                case CodeLanguage.Rust:
                    var rsMatch = Regex.Match(line, @"^\s*(?:pub\s+)?fn\s+(\w+)\s*[<(](.*?)[)>]");
                    if (rsMatch.Success) { name = rsMatch.Groups[1].Value; paramCount = rsMatch.Groups[2].Value.Split(',').Count(p => !string.IsNullOrWhiteSpace(p)); }
                    break;
            }

            if (name != null)
            {
                functions.Add(new CodeFunction { Name = name, Line = i + 1, ParameterCount = paramCount });
            }
        }

        return functions;
    }

    private static List<CodeClass> ExtractClasses(string code, LanguageInfo info)
    {
        var classes = new List<CodeClass>();
        var lines = code.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            string? name = null;
            var methodCount = 0;

            switch (info.Language)
            {
                case CodeLanguage.Python:
                    var pyMatch = Regex.Match(line, @"^\s*class\s+(\w+)");
                    if (pyMatch.Success)
                    {
                        name = pyMatch.Groups[1].Value;
                        for (var j = i + 1; j < Math.Min(lines.Length, i + 200); j++)
                            if (Regex.IsMatch(lines[j], @"^\s{4}def\s+\w+")) methodCount++;
                    }
                    break;
                case CodeLanguage.CSharp:
                    var csMatch = Regex.Match(line, @"(?:public|private|protected|internal|static|\s)+class\s+(\w+)");
                    if (csMatch.Success)
                    {
                        name = csMatch.Groups[1].Value;
                        for (var j = i + 1; j < Math.Min(lines.Length, i + 200); j++)
                            if (Regex.IsMatch(lines[j], @"(?:public|private|protected|internal)\s+\w+\s+\w+\s*\(")) methodCount++;
                    }
                    break;
                case CodeLanguage.Java:
                case CodeLanguage.TypeScript:
                    var jMatch = Regex.Match(line, @"(?:public\s+)?(?:class|interface)\s+(\w+)");
                    if (jMatch.Success) name = jMatch.Groups[1].Value;
                    break;
                case CodeLanguage.Go:
                    var goMatch = Regex.Match(line, @"^\s*type\s+(\w+)\s+struct");
                    if (goMatch.Success) name = goMatch.Groups[1].Value;
                    break;
                case CodeLanguage.Rust:
                    var rsMatch = Regex.Match(line, @"^\s*(?:pub\s+)?(?:struct|enum|trait)\s+(\w+)");
                    if (rsMatch.Success) name = rsMatch.Groups[1].Value;
                    break;
            }

            if (name != null)
            {
                classes.Add(new CodeClass { Name = name, Line = i + 1, MethodCount = methodCount });
            }
        }

        return classes;
    }

    private static List<CodeImport> ExtractImports(string code, LanguageInfo info)
    {
        var imports = new List<CodeImport>();
        var lines = code.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            string? module = null;

            switch (info.Language)
            {
                case CodeLanguage.Python:
                    var pyImport = Regex.Match(line, @"^(?:import\s+(\w+)|from\s+(\w+)\s+import)");
                    if (pyImport.Success) module = pyImport.Groups[1].Success ? pyImport.Groups[1].Value : pyImport.Groups[2].Value;
                    break;
                case CodeLanguage.CSharp:
                    var csImport = Regex.Match(line, @"^using\s+([\w.]+)");
                    if (csImport.Success) module = csImport.Groups[1].Value;
                    break;
                case CodeLanguage.JavaScript:
                case CodeLanguage.TypeScript:
                    var jsImport = Regex.Match(line, @"^(?:import\s+.*?\s+from\s+['""](.+?)['""]|require\s*\(['""](.+?)['""]\))");
                    if (jsImport.Success) module = jsImport.Groups[1].Success ? jsImport.Groups[1].Value : jsImport.Groups[2].Value;
                    break;
                case CodeLanguage.Go:
                    var goImport = Regex.Match(line, @"^(?:import\s+)?\""([^\""]+)\""");
                    if (goImport.Success && line.Contains("import")) module = goImport.Groups[1].Value;
                    break;
                case CodeLanguage.Rust:
                    var rsImport = Regex.Match(line, @"^use\s+([\w:]+)");
                    if (rsImport.Success) module = rsImport.Groups[1].Value;
                    break;
                case CodeLanguage.Java:
                    var javaImport = Regex.Match(line, @"^import\s+([\w.]+)");
                    if (javaImport.Success) module = javaImport.Groups[1].Value;
                    break;
            }

            if (module != null)
                imports.Add(new CodeImport { Module = module, Line = i + 1 });
        }

        return imports;
    }

    private static double EstimateComplexity(string code, LanguageInfo info)
    {
        var branches = Regex.Matches(code, @"\b(if|else|for|while|switch|case|catch|when|match|and|or)\b").Count;
        var totalStatements = code.Split('\n').Count(l => l.Contains(';') || l.TrimStart().StartsWith("def ") || l.TrimStart().StartsWith("class "));
        return Math.Min(10.0, Math.Round(branches * 0.5 + totalStatements * 0.1, 1));
    }
}

public sealed class CodeAnalysisResult
{
    public CodeLanguage Language { get; init; }
    public string LanguageName { get; init; } = "";
    public int TotalLines { get; init; }
    public int CodeLines { get; init; }
    public int CommentLines { get; init; }
    public int BlankLines { get; init; }
    public List<CodeFunction> Functions { get; set; } = new();
    public List<CodeClass> Classes { get; set; } = new();
    public List<CodeImport> Imports { get; set; } = new();
    public double Complexity { get; set; }
}

public sealed class CodeFunction
{
    public string Name { get; init; } = "";
    public int Line { get; init; }
    public int ParameterCount { get; init; }
}

public sealed class CodeClass
{
    public string Name { get; init; } = "";
    public int Line { get; init; }
    public int MethodCount { get; init; }
}

public sealed class CodeImport
{
    public string Module { get; init; } = "";
    public int Line { get; init; }
}

public sealed class CodeQualityReport
{
    public int TotalLines { get; set; }
    public int LongLines { get; set; }
    public bool InconsistentIndent { get; set; }
    public int TodoCount { get; set; }
}
