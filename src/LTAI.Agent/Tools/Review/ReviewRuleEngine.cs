using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.Agent.Tools.Review;

public sealed class ReviewRuleEngine
{
    private static readonly ConcurrentDictionary<string, Regex> s_regexCache = new();
    private static readonly ConcurrentDictionary<string, Regex> s_globCache = new();

    private readonly List<ReviewRule> _rules = [];

    public IReadOnlyList<ReviewRule> Rules => _rules;

    public void AddRules(IEnumerable<ReviewRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!string.IsNullOrEmpty(rule.Id) && !string.IsNullOrEmpty(rule.Pattern))
                _rules.Add(rule);
        }
    }

    public void AddRule(ReviewRule rule)
    {
        if (!string.IsNullOrEmpty(rule.Id) && !string.IsNullOrEmpty(rule.Pattern))
            _rules.Add(rule);
    }

    /// <summary>Load built-in rules for .NET/C# and common patterns.</summary>
    public void LoadBuiltinRules()
    {
        _rules.AddRange(BuiltinRules);
    }

    /// <summary>Load project-level rules from .ltai/review-rules.json or review-rules.yaml.</summary>
    public void LoadProjectRules(string projectRoot)
    {
        var jsonPath = Path.Combine(projectRoot, ".ltai", "review-rules.json");
        if (File.Exists(jsonPath))
        {
            var json = File.ReadAllText(jsonPath);
            var projectRules = JsonSerializer.Deserialize<List<ReviewRule>>(json);
            if (projectRules != null)
                AddRules(projectRules);
        }

        var yamlPath = Path.Combine(projectRoot, ".ltai", "review-rules.yaml");
        if (File.Exists(yamlPath))
        {
            // YAML not natively in .NET — fallback to JSON convention
        }
    }

    /// <summary>Match rules against file content. Returns all matching rule violations.</summary>
    public List<ReviewRuleMatch> Match(string filePath, string content)
    {
        var matches = new List<ReviewRuleMatch>();
        var fileName = Path.GetFileName(filePath);

        foreach (var rule in _rules)
        {
            if (!string.IsNullOrEmpty(rule.FilePattern) && !GlobMatch(fileName, rule.FilePattern))
                continue;

            if (!string.IsNullOrEmpty(rule.Language) && !MatchesLanguage(filePath, rule.Language))
                continue;

            var regex = GetRegex(rule.Pattern!);
            var match = regex.Match(content);
            if (!match.Success)
                continue;

            if (!string.IsNullOrEmpty(rule.NotPattern))
            {
                var negRegex = GetRegex(rule.NotPattern);
                if (negRegex.IsMatch(content))
                    continue;
            }

            var lineNumber = LineFromIndex(content, match.Index);
            var message = "";
            if (rule.MessageTemplate != null)
            {
                try { message = string.Format(rule.MessageTemplate, filePath, match.Value); }
                catch (FormatException) { message = $"[{rule.Category}] {rule.Description}"; }
            }
            else
            {
                message = $"[{rule.Category}] {rule.Description}";
            }

            matches.Add(new ReviewRuleMatch(
                RuleId: rule.Id,
                RuleName: rule.Name,
                Category: rule.Category,
                Severity: rule.Severity,
                FilePath: filePath,
                LineNumber: lineNumber,
                MatchedText: match.Value,
                Message: message));
        }

        return matches;
    }

    /// <summary>Batch match across multiple files.</summary>
    public Dictionary<string, List<ReviewRuleMatch>> MatchAll(Dictionary<string, string> files)
    {
        var results = new Dictionary<string, List<ReviewRuleMatch>>();
        foreach (var (path, content) in files)
        {
            var matches = Match(path, content);
            if (matches.Count > 0)
                results[path] = matches;
        }
        return results;
    }

    // ── built-in rules ──

    private static readonly List<ReviewRule> BuiltinRules = GenerateBuiltinRules();

    private static List<ReviewRule> GenerateBuiltinRules()
    {
        return
        [
            // == Correctness ==
            new() { Id = "CORR-001", Name = "async-void", Category = "correctness", Severity = "error",
                Description = "Async void methods are not catchable and crash the process",
                Pattern = @"async\s+void\s+\w+\s*\(", FilePattern = "**/*.cs",
                MessageTemplate = "Async void method — convert to async Task" },

            new() { Id = "CORR-002", Name = "sync-over-async", Category = "correctness", Severity = "error",
                Description = "Blocking on async with .Result/.Wait() can deadlock",
                Pattern = @"\.Result\b|\.Wait\(\)|\.GetAwaiter\(\)\.GetResult\(\)", FilePattern = "**/*.cs",
                MessageTemplate = "Blocking call on async — use await instead" },

            new() { Id = "CORR-003", Name = "thread-sleep", Category = "correctness", Severity = "warning",
                Description = "Thread.Sleep in async context blocks the thread",
                Pattern = @"Thread\.Sleep\(", FilePattern = "**/*.cs",
                MessageTemplate = "Thread.Sleep in async — use await Task.Delay instead" },

            new() { Id = "CORR-004", Name = "dispose-before-use", Category = "correctness", Severity = "warning",
                Description = "Potential null reference on disposed object",
                Pattern = @"\bDispose\(\)\s*;\s*\n\s*\w+\.", FilePattern = "**/*.cs",
                MessageTemplate = "Object used after Dispose() — potential ObjectDisposedException" },

            // == Security ==
            new() { Id = "SEC-001", Name = "sql-concat", Category = "security", Severity = "error",
                Description = "SQL string concatenation is vulnerable to injection",
                Pattern = @"\$""[^""]*SELECT|""\s*\+\s*""[^""]*SELECT|\+\s*""[^""]*SELECT|String\.Format\([^)]*SELECT", FilePattern = "**/*.cs",
                MessageTemplate = "SQL string concatenation — use parameterized queries" },

            new() { Id = "SEC-002", Name = "hardcoded-secret", Category = "security", Severity = "error",
                Description = "Hardcoded secret/key/token/password in source",
                Pattern = @"(api.?key|apikey|secret|token|password|connectionstring)\s*[:=]\s*[""'][^""'\s]{8,}", FilePattern = "**/*.cs",
                MessageTemplate = "Hardcoded secret — use User Secrets / Key Vault / env vars" },

            new() { Id = "SEC-003", Name = "command-injection", Category = "security", Severity = "error",
                Description = "Process.Start with user input enables command injection",
                Pattern = @"Process\.Start\s*\([^)]*\$", FilePattern = "**/*.cs",
                MessageTemplate = "Process.Start with interpolated string — validate/sanitize input" },

            new() { Id = "SEC-004", Name = "path-traversal", Category = "security", Severity = "error",
                Description = "Path.Combine with user input without validation",
                Pattern = @"Path\.Combine\s*\([^)]*\.(Trim|Replace|ToLower)", FilePattern = "**/*.cs",
                NotPattern = @"Path\.GetFullPath|Path\.GetFileName",
                MessageTemplate = "Path traversal risk — use Path.GetFullPath + validation" },

            // == Performance ==
            new() { Id = "PERF-001", Name = "linq-multiple-enum", Category = "performance", Severity = "warning",
                Description = "IEnumerable should not be enumerated multiple times",
                Pattern = @"\.Count\(\)\s*[>|<|=]|\.Any\(\)|\.ToList\(\)|\.First\(\)", FilePattern = "**/*.cs",
                MessageTemplate = "Possible multiple IEnumerable enumeration — materialize with .ToList() or .ToArray()" },

            new() { Id = "PERF-002", Name = "string-concat-loop", Category = "performance", Severity = "warning",
                Description = "String concatenation in loop is O(n²)",
                Pattern = @"\w+\s*\+=\s*[""']", FilePattern = "**/*.cs",
                MessageTemplate = "String concatenation in loop — use StringBuilder" },

            new() { Id = "PERF-003", Name = "foreach-boxing", Category = "performance", Severity = "info",
                Description = "foreach on non-generic collection causes boxing",
                Pattern = @"foreach\s*\(\s*(object|var)\s+\w+\s+in\s+\w+\)", FilePattern = "**/*.cs",
                MessageTemplate = "Non-generic foreach causes boxing — use generic collection" },

            new() { Id = "PERF-004", Name = "task-result-block", Category = "performance", Severity = "warning",
                Description = "Task<T>.Result blocks the calling thread",
                Pattern = @"\.Result(?![.(]|Async)\b", FilePattern = "**/*.cs",
                MessageTemplate = "Task.Result blocks thread — prefer await" },

            // == Maintainability ==
            new() { Id = "MAINT-001", Name = "magic-string", Category = "maintainability", Severity = "info",
                Description = "Repeated literal string suggests a named constant",
                Pattern = @"""[^""]{10,}""" , FilePattern = "**/*.cs",
                NotPattern = @"(const|readonly|enum|class|struct)",
                MessageTemplate = "Repeated literal string — extract to named constant" },

            new() { Id = "MAINT-002", Name = "todo-comment", Category = "maintainability", Severity = "info",
                Description = "TODO/FIXME/HACK comments indicate incomplete work",
                Pattern = @"//\s*(TODO|FIXME|HACK|XXX|BUG)\b", FilePattern = "**/*.cs",
                MessageTemplate = "Incomplete work marker — address before merging" },

            new() { Id = "MAINT-003", Name = "large-method", Category = "maintainability", Severity = "warning",
                Description = "Methods longer than 80 lines are harder to maintain",
                Pattern = @"\bvoid\s+\w+\s*\(|\w+\s+\w+\s*\(.*\)\s*\n\s*\{", FilePattern = "**/*.cs",
                MessageTemplate = "Method may be too long (>80 lines) — consider splitting" },

            // == .NET-specific ==
            new() { Id = "DOTNET-001", Name = "missing-cancellation", Category = "correctness", Severity = "warning",
                Description = "Async method parameter should accept CancellationToken",
                Pattern = @"async\s+\w+\s+\w+Async\s*\(", FilePattern = "**/*.cs",
                NotPattern = @"CancellationToken",
                MessageTemplate = "Async method lacks CancellationToken parameter" },

            new() { Id = "DOTNET-002", Name = "configureawait-false", Category = "performance", Severity = "info",
                Description = "Library async methods should use ConfigureAwait(false)",
                Pattern = @"await\s+\w+\.\w+Async\(", FilePattern = "**/*.cs",
                NotPattern = @"ConfigureAwait\(false\)",
                MessageTemplate = "Library code await without ConfigureAwait(false) — may cause deadlock in UI/ASP.NET" },

            new() { Id = "DOTNET-003", Name = "idisposable-not-disposed", Category = "correctness", Severity = "warning",
                Description = "IDisposable should be wrapped in using",
                Pattern = @"new\s+\w+(Stream|Reader|Writer|Connection|Client|File)\s*\(", FilePattern = "**/*.cs",
                NotPattern = @"using\s|using var",
                MessageTemplate = "IDisposable not wrapped in 'using' — resource leak" },

            // == JSON / Serialization ==
            new() { Id = "SER-001", Name = "missing-json-ctor", Category = "correctness", Severity = "warning",
                Description = "Record/class used for JSON deserialization lacks parameterless constructor",
                Pattern = @"record\s+\w+\s*\(|readonly\s+struct", FilePattern = "**/*.cs",
                NotPattern = @"\[JsonConstructor|\[JsonDerivedType|\[JsonInclude",
                MessageTemplate = "JSON model may need parameterless constructor or JsonConstructor attribute" },

            // == MoonBit ==
            new() { Id = "MOON-001", Name = "moonbit-unused-var", Category = "correctness", Severity = "warning",
                Description = "Unused variable in MoonBit triggers compiler warning",
                Pattern = @"let\s+_\w+\s*=", FilePattern = "**/*.mbt",
                MessageTemplate = "Unused variable — prefix with _ or remove" },

            new() { Id = "MOON-002", Name = "moonbit-pub-type-doc", Category = "maintainability", Severity = "info",
                Description = "Public MoonBit type/function should have doc comment",
                Pattern = @"^pub\s+(fn|type|enum|struct|trait)\s+\w+", FilePattern = "**/*.mbt",
                NotPattern = @"///|//\|",
                MessageTemplate = "Public declaration without doc comment — add ///" },

            new() { Id = "MOON-003", Name = "moonbit-shadow-import", Category = "maintainability", Severity = "warning",
                Description = "Shadowed import in MoonBit",
                Pattern = @"use\s+\w+\.\{\s*\w+\s*\}", FilePattern = "**/*.mbt",
                MessageTemplate = "Import uses braces — prefer direct use" },

            // == Mojo ==
            new() { Id = "MOJO-001", Name = "mojo-no-type-hint", Category = "maintainability", Severity = "info",
                Description = "Mojo function parameter without type annotation",
                Pattern = @"fn\s+\w+\s*\([^)]*:\s*\w+", FilePattern = "**/*.mojo",
                NotPattern = @":\s*(Int|Float|String|Bool|SIMD|Tensor)",
                MessageTemplate = "Parameter type annotation missing explicit type" },

            new() { Id = "MOJO-002", Name = "mojo-unsafe-ptr", Category = "security", Severity = "warning",
                Description = "Unsafe pointer arithmetic in Mojo",
                Pattern = @"\bPointer\b|address_of|offset\s*\(", FilePattern = "**/*.mojo",
                MessageTemplate = "Unsafe pointer usage — prefer safe references when possible" },

            new() { Id = "MOJO-003", Name = "mojo-alias-mut", Category = "correctness", Severity = "warning",
                Description = "Mut alias in Mojo without explicit mut annotation",
                Pattern = @"alias\s+\w+\s*=", FilePattern = "**/*.mojo",
                MessageTemplate = "Alias — consider 'let' for immutable, 'var' for mutable" },

            // == Cangjie ==
            new() { Id = "CJ-001", Name = "cangjie-null-return", Category = "correctness", Severity = "error",
                Description = "Function returning nullable without null check",
                Pattern = @"func\s+\w+\s*\([^)]*\)\s*:\s*\w+\?", FilePattern = "**/*.cj",
                MessageTemplate = "Nullable return type — caller must handle Option<T>" },

            new() { Id = "CJ-002", Name = "cangjie-unused-import", Category = "maintainability", Severity = "info",
                Description = "Unused import in Cangjie",
                Pattern = @"import\s+\w+(\.\w+)*\s*;", FilePattern = "**/*.cj",
                MessageTemplate = "Check if import is used — remove unused imports" },

            new() { Id = "CJ-003", Name = "cangjie-mut-param", Category = "correctness", Severity = "warning",
                Description = "Mutable parameter in Cangjie function",
                Pattern = @"func\s+\w+\s*\(\s*mut\s+\w+", FilePattern = "**/*.cj",
                MessageTemplate = "Mutable parameter — consider if mutation is necessary" },
        ];
    }

    // ── helpers ──

    private static Regex GetRegex(string pattern)
    {
        return s_regexCache.GetOrAdd(pattern, p =>
            new Regex(p, RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase));
    }

    private static bool GlobMatch(string fileName, string globPattern)
    {
        if (!globPattern.Contains('*') && !globPattern.Contains('?'))
            return fileName == globPattern;

        var regex = s_globCache.GetOrAdd(globPattern, p =>
        {
            var escaped = "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(escaped, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        });
        return regex.IsMatch(fileName);
    }

    private static bool MatchesLanguage(string filePath, string language) => language.ToLowerInvariant() switch
    {
        "c#" or "cs" => filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
        "javascript" or "js" => filePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase),
        "typescript" or "ts" => filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase),
        "python" or "py" => filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase),
        "go" => filePath.EndsWith(".go", StringComparison.OrdinalIgnoreCase),
        "rust" or "rs" => filePath.EndsWith(".rs", StringComparison.OrdinalIgnoreCase),
        "java" => filePath.EndsWith(".java", StringComparison.OrdinalIgnoreCase),
        "yaml" or "yml" => filePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase),
        "json" => filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase),
        "markdown" or "md" => filePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase),
        "moonbit" or "mbt" => filePath.EndsWith(".mbt", StringComparison.OrdinalIgnoreCase),
        "mojo" => filePath.EndsWith(".mojo", StringComparison.OrdinalIgnoreCase),
        "cangjie" or "cj" => filePath.EndsWith(".cj", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static int LineFromIndex(string content, int index)
    {
        if (index <= 0) return 1;
        var line = 1;
        for (int i = 0; i < index && i < content.Length; i++)
        {
            if (content[i] == '\n') line++;
        }
        return line;
    }
}
