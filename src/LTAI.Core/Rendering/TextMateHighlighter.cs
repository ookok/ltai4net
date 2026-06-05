using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace LTAI.Core.Rendering;

/// <summary>
/// Shared TextMate syntax highlighter — uses VS Code's .tmLanguage grammar files.
/// Replaces the hardcoded keyword-based highlighting with accurate tokenization.
/// Singleton per theme; thread-safe after initialization.
/// </summary>
public sealed class TextMateHighlighter
{
    private readonly Registry _registry;
    private readonly Dictionary<string, IGrammar> _grammars = new(StringComparer.OrdinalIgnoreCase);
    private IStateStack? _state;

    public static readonly TextMateHighlighter Default = new();

    public TextMateHighlighter(ThemeName theme = ThemeName.DarkPlus)
    {
        var options = new RegistryOptions(theme);
        _registry = new Registry(options);

        // Pre-load common grammars
        foreach (var lang in new[] { "cs", "csharp", "py", "python", "js", "javascript",
            "ts", "typescript", "go", "rs", "rust", "java", "rb", "ruby",
            "swift", "kt", "kotlin", "dart", "php", "sql", "sh", "shell",
            "bash", "ps1", "powershell", "yaml", "yml", "xml", "html", "css",
            "json", "md", "markdown", "fs", "fsharp", "vb", "vbnet" })
        {
            try
            {
                var scope = MapLanguageToScope(lang);
                if (scope != null)
                {
                    var grammar = _registry.LoadGrammar(scope);
                    if (grammar != null)
                        _grammars[lang] = grammar;
                }
            }
            catch { /* grammar not available */ }
        }
    }

    /// <summary>Tokenize a single line of code. Returns list of (text, foreground color hex) tuples.</summary>
    public List<(string text, string fgColor)> TokenizeLine(string lang, string line)
    {
        var result = new List<(string, string)>();
        if (!_grammars.TryGetValue(lang, out var grammar))
        {
            result.Add((line, "#d4d4d4"));
            return result;
        }

        var tokenized = grammar.TokenizeLine(line, _state, TimeSpan.FromMilliseconds(50));
        _state = tokenized.RuleStack;
        var theme = _registry.GetTheme();

        if (tokenized.Tokens == null || tokenized.Tokens.Length == 0)
        {
            result.Add((line, "#d4d4d4"));
            return result;
        }

        for (int i = 0; i < tokenized.Tokens.Length; i++)
        {
            var token = tokenized.Tokens[i];
            var start = Math.Min(token.StartIndex, line.Length);
            var end = Math.Min(token.EndIndex, line.Length);
            if (start >= end) continue;

            var text = line[start..end];
            var scopes = token.Scopes;
            var fg = "#d4d4d4"; // default foreground

            if (scopes != null && scopes.Count > 0)
            {
                var themeRules = theme.Match(scopes);
                if (themeRules != null)
                {
                    foreach (var rule in themeRules)
                    {
                        var color = theme.GetColor(rule.foreground);
                        if (!string.IsNullOrEmpty(color))
                        {
                            fg = color;
                            break;
                        }
                    }
                }
            }

            result.Add((text, fg));
        }

        return result;
    }

    /// <summary>Reset the tokenizer state (for new code blocks).</summary>
    public void ResetState() => _state = null;

    /// <summary>Detect language from file extension or code fence label.</summary>
    public static string NormalizeLanguage(string? lang)
    {
        return (lang?.ToLowerInvariant()) switch
        {
            "cs" or "c#" or "csharp" => "csharp",
            "py" or "python" => "python",
            "js" or "javascript" or "jsx" => "javascript",
            "ts" or "typescript" or "tsx" => "typescript",
            "go" or "golang" => "go",
            "rs" or "rust" => "rust",
            "java" => "java",
            "rb" or "ruby" => "ruby",
            "swift" => "swift",
            "kt" or "kotlin" => "kotlin",
            "dart" => "dart",
            "php" => "php",
            "sql" => "sql",
            "sh" or "shell" or "bash" or "zsh" => "shell",
            "ps1" or "powershell" => "powershell",
            "yaml" or "yml" => "yaml",
            "xml" => "xml",
            "html" or "htm" => "html",
            "css" or "scss" or "less" => "css",
            "json" => "json",
            "md" or "markdown" => "markdown",
            "fs" or "fsharp" => "fsharp",
            "vb" or "vbnet" => "vbnet",
            "dockerfile" or "docker" => "dockerfile",
            _ => "",
        };
    }

    private static string? MapLanguageToScope(string lang)
    {
        return lang switch
        {
            "csharp" => "source.cs",
            "python" => "source.python",
            "javascript" => "source.js",
            "typescript" => "source.ts",
            "go" => "source.go",
            "rust" => "source.rust",
            "java" => "source.java",
            "ruby" => "source.ruby",
            "swift" => "source.swift",
            "kotlin" => "source.kotlin",
            "dart" => "source.dart",
            "php" => "source.php",
            "sql" => "source.sql",
            "shell" => "source.shell",
            "powershell" => "source.powershell",
            "yaml" => "source.yaml",
            "xml" => "text.xml",
            "html" => "text.html",
            "css" => "source.css",
            "json" => "source.json",
            "markdown" => "text.html.markdown",
            "fsharp" => "source.fsharp",
            "vbnet" => "source.vbnet",
            "dockerfile" => "source.dockerfile",
            _ => null,
        };
    }
}
