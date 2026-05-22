using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Capability.CodeEngine;

public sealed class MultiLangCodeAnalyzer
{
    private readonly ParserRegistry _parserRegistry;
    private readonly ILogger<MultiLangCodeAnalyzer> _logger;

    public MultiLangCodeAnalyzer(ParserRegistry parserRegistry, ILogger<MultiLangCodeAnalyzer> logger)
    {
        _parserRegistry = parserRegistry;
        _logger = logger;
    }

    public async Task<CodeAnalysisResult> AnalyzeAsync(string code, CodeLanguage language,
        CancellationToken cancellationToken = default)
    {
        var info = LanguageRegistry.Get(language);
        var parser = _parserRegistry.GetParser(language);
        if (parser != null)
        {
            var astResult = await parser.ParseAsync(code, null, cancellationToken);
            return MapToResult(astResult, language, info);
        }
        return AnalyzeFallback(code, language, info);
    }

    [Obsolete("Use AnalyzeAsync instead to avoid sync-over-async deadlocks")]
    public async Task<CodeAnalysisResult> Analyze(string code, CodeLanguage language)
    {
        return await AnalyzeAsync(code, language);
    }

    public async Task<List<string>> ExtractDependenciesAsync(
        string code, CodeLanguage language, CancellationToken cancellationToken = default)
    {
        var parser = _parserRegistry.GetParser(language);
        if (parser != null)
        {
            var result = await parser.ParseAsync(code, null, cancellationToken);
            return result.Imports.Select(i => i.Module).Distinct().ToList();
        }
        var info = LanguageRegistry.Get(language);
        var lines = code.Split('\n');
        return ExtractImportsFallback(lines, info).Select(i => i.Module).Distinct().ToList();
    }

    public async Task<List<AstDiagnostic>> GetDiagnosticsAsync(string code, CodeLanguage language,
        CancellationToken cancellationToken = default)
    {
        var parser = _parserRegistry.GetParser(language);
        if (parser != null && parser.SupportsDiagnostics)
        {
            var result = await parser.ParseAsync(code, null, cancellationToken);
            return result.Diagnostics;
        }
        return new();
    }

    public async Task<CodeParseResult> ParseWithAstAsync(string code, CodeLanguage language,
        CancellationToken cancellationToken = default)
    {
        var parser = _parserRegistry.GetParser(language);
        if (parser != null)
            return await parser.ParseAsync(code, null, cancellationToken);

        var result = new CodeParseResult { Language = language };
        var info = LanguageRegistry.Get(language);
        var lines = code.Split('\n');
        result.Functions = ExtractFunctionsFallback(lines, info);
        result.Classes = ExtractClassesFallback(lines, info);
        result.Imports = ExtractImportsFallback(lines, info);
        return result;
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

    private static CodeAnalysisResult MapToResult(CodeParseResult astResult, CodeLanguage language, LanguageInfo info)
    {
        return new CodeAnalysisResult
        {
            Language = language,
            LanguageName = info.Name,
            TotalLines = astResult.TotalLines,
            CodeLines = astResult.CodeLines,
            CommentLines = astResult.CommentLines,
            BlankLines = astResult.BlankLines,
            Functions = astResult.Functions.Select(f => new CodeFunction
            {
                Name = f.Name,
                Line = f.Line,
                ParameterCount = f.Parameters.Count,
            }).ToList(),
            Classes = astResult.Classes.Select(c => new CodeClass
            {
                Name = c.Name,
                Line = c.Line,
                MethodCount = c.MethodCount,
            }).ToList(),
            Imports = astResult.Imports.Select(i => new CodeImport
            {
                Module = i.Module,
                Line = i.Line,
            }).ToList(),
            Complexity = astResult.CyclomaticComplexity,
        };
    }

    private static CodeAnalysisResult AnalyzeFallback(string code, CodeLanguage language, LanguageInfo info)
    {
        var lines = code.Split('\n');
        return new CodeAnalysisResult
        {
            Language = language,
            LanguageName = info.Name,
            TotalLines = lines.Length,
            CodeLines = CountCodeLines(lines, info),
            CommentLines = CountCommentLines(lines, info),
            BlankLines = CountBlankLines(lines),
            Functions = ExtractFunctionsFallback(lines, info).Select(f => new CodeFunction { Name = f.Name, Line = f.Line, ParameterCount = f.Parameters.Count }).ToList(),
            Classes = ExtractClassesFallback(lines, info).Select(c => new CodeClass { Name = c.Name, Line = c.Line, MethodCount = c.MethodCount }).ToList(),
            Imports = ExtractImportsFallback(lines, info).Select(i => new CodeImport { Module = i.Module, Line = i.Line }).ToList(),
            Complexity = EstimateComplexity(code),
        };
    }

    private static List<AstFunction> ExtractFunctionsFallback(string[] lines, LanguageInfo info)
    {
        var functions = new List<AstFunction>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            string? name = null;
            int paramCount = 0;

            switch (info.Language)
            {
                case CodeLanguage.CSharp:
                case CodeLanguage.Java:
                    var csMatch = Regex.Match(line, @"(?:public|private|protected|internal|static|\s)+(\w+)\s+(\w+)\s*\((.*?)\)");
                    if (csMatch.Success && !line.Contains("class "))
                    { name = csMatch.Groups[2].Value; paramCount = csMatch.Groups[3].Value.Split(',').Count(p => !string.IsNullOrWhiteSpace(p)); }
                    break;
                case CodeLanguage.Python:
                    var pyMatch = Regex.Match(line, @"^\s*def\s+(\w+)\s*\((.*?)\)");
                    if (pyMatch.Success) { name = pyMatch.Groups[1].Value; paramCount = pyMatch.Groups[2].Value.Split(',').Count(p => !string.IsNullOrWhiteSpace(p)); }
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
                functions.Add(new AstFunction { Name = name, Line = i + 1, EndLine = i + 5, Column = 1, Parameters = new List<string>(paramCount) });
        }
        return functions;
    }

    private static List<AstClass> ExtractClassesFallback(string[] lines, LanguageInfo info)
    {
        var classes = new List<AstClass>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            string? name = null;

            switch (info.Language)
            {
                case CodeLanguage.Python:
                    var pyMatch = Regex.Match(line, @"^\s*class\s+(\w+)");
                    if (pyMatch.Success) name = pyMatch.Groups[1].Value;
                    break;
                case CodeLanguage.CSharp:
                    var csMatch = Regex.Match(line, @"(?:public|private|protected|internal|static|\s)+class\s+(\w+)");
                    if (csMatch.Success) name = csMatch.Groups[1].Value;
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
                classes.Add(new AstClass { Name = name, Line = i + 1, EndLine = i + 10, Column = 1 });
        }
        return classes;
    }

    private static List<AstImport> ExtractImportsFallback(string[] lines, LanguageInfo info)
    {
        var imports = new List<AstImport>();
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
                    var goImport = Regex.Match(line, "\"([^\"]+)\"");
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
                imports.Add(new AstImport { Module = module, Line = i + 1, Column = 1 });
        }
        return imports;
    }

    private static int CountCodeLines(string[] lines, LanguageInfo info)
    {
        return lines.Count(line => !string.IsNullOrWhiteSpace(line) &&
                       !line.TrimStart().StartsWith(info.SingleLineComment) &&
                       !IsMultiLineComment(line, info));
    }

    private static int CountCommentLines(string[] lines, LanguageInfo info)
    {
        return lines.Count(line => line.TrimStart().StartsWith(info.SingleLineComment) ||
                       IsMultiLineComment(line, info));
    }

    private static int CountBlankLines(string[] lines) => lines.Count(string.IsNullOrWhiteSpace);

    private static bool IsMultiLineComment(string line, LanguageInfo info)
    {
        var trimmed = line.Trim();
        return !string.IsNullOrEmpty(info.MultiLineCommentStart) &&
               (trimmed.StartsWith(info.MultiLineCommentStart) || trimmed.EndsWith(info.MultiLineCommentEnd));
    }

    private static double EstimateComplexity(string code)
    {
        var branches = Regex.Matches(code, @"\b(if|else|for|while|switch|case|catch|when|match|and|or)\b").Count;
        var totalStatements = code.Split('\n').Count(l => l.Contains(';') || l.TrimStart().StartsWith("def ") || l.TrimStart().StartsWith("class "));
        return Math.Min(10.0, Math.Round(branches * 0.5 + totalStatements * 0.1, 1));
    }
}
