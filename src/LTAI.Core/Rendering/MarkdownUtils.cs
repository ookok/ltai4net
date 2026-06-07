using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LTAI.Core.Rendering;

public static class MarkdownUtils
{
    private static readonly Regex FenceStartRx = new(@"^```(\w*)$", RegexOptions.Multiline);
    private static readonly Regex FenceEndRx = new(@"^```$", RegexOptions.Multiline);

    public static bool HasUnclosedFence(string text)
    {
        var starts = FenceStartRx.Matches(text).Count;
        var ends = FenceEndRx.Matches(text).Count;
        return starts > ends;
    }

    public static string ContentHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string DetectLanguage(string? lang)
    {
        return (lang?.ToLowerInvariant()) switch
        {
            "cs" or "c#" or "csharp" => "csharp",
            "py" or "python" => "python",
            "js" or "javascript" => "javascript",
            "ts" or "typescript" => "typescript",
            "go" or "golang" => "go",
            "rs" or "rust" => "rust",
            "java" => "java",
            "moonbit" or "mbt" => "moonbit",
            "mojo" or "🔥" => "mojo",
            "cangjie" or "cj" => "cangjie",
            _ => lang ?? "",
        };
    }

    private static readonly Dictionary<string, HashSet<string>> KeywordSets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = new(StringComparer.Ordinal) { "class", "struct", "interface", "enum", "record",
            "namespace", "using", "public", "private", "protected", "internal", "static", "readonly",
            "virtual", "abstract", "override", "async", "await", "new", "return", "if", "else",
            "for", "foreach", "while", "do", "switch", "case", "break", "continue", "try", "catch",
            "finally", "throw", "var", "void", "int", "string", "bool", "double", "float", "long",
            "char", "object", "true", "false", "null", "this", "base", "in", "out", "ref", "is",
            "as", "typeof", "get", "set", "value", "where", "select", "from", "partial", "sealed",
            "required", "init", "global", "file", "params", "yield", "lock", "volatile", "event",
            "delegate", "implicit", "explicit", "operator", "sizeof", "nameof", "notnull" },
        ["python"] = new(StringComparer.Ordinal) { "class", "def", "return", "if", "elif", "else",
            "for", "while", "try", "except", "finally", "import", "from", "as", "with", "yield",
            "lambda", "True", "False", "None", "self", "and", "or", "not", "in", "is", "async",
            "await", "raise", "pass", "break", "continue", "global", "nonlocal", "match", "case",
            "type", "assert", "del", "print", "range", "len", "super" },
        ["javascript"] = new(StringComparer.Ordinal) { "function", "class", "const", "let", "var",
            "return", "if", "else", "for", "while", "do", "switch", "case", "break", "continue",
            "try", "catch", "finally", "throw", "new", "this", "async", "await", "import", "export",
            "default", "from", "true", "false", "null", "undefined", "typeof", "instanceof",
            "void", "delete", "yield", "super", "extends", "static", "get", "set", "of", "in" },
        ["typescript"] = new(StringComparer.Ordinal) { "interface", "type", "enum", "namespace",
            "module", "declare", "abstract", "readonly", "public", "private", "protected", "static",
            "implements", "extends", "as", "is", "keyof", "typeof", "infer", "satisfies", "const",
            "let", "var", "function", "class", "return", "if", "else", "for", "while", "async",
            "await", "import", "export", "default", "from", "true", "false", "null", "undefined",
            "never", "unknown", "any", "void", "string", "number", "boolean" },
        ["go"] = new(StringComparer.Ordinal) { "func", "type", "struct", "interface", "map", "chan",
            "go", "defer", "select", "range", "return", "if", "else", "for", "switch", "case",
            "break", "continue", "var", "const", "package", "import", "true", "false", "nil",
            "make", "new", "append", "len", "cap", "close", "delete", "panic", "recover" },
        ["rust"] = new(StringComparer.Ordinal) { "fn", "let", "mut", "const", "static", "pub",
            "use", "mod", "struct", "enum", "trait", "impl", "type", "ref", "match", "if", "else",
            "for", "while", "loop", "return", "break", "continue", "true", "false", "None",
            "Some", "Ok", "Err", "async", "await", "move", "unsafe", "extern", "dyn", "where",
            "as", "in", "self", "Self", "super", "crate" },
        ["java"] = new(StringComparer.Ordinal) { "class", "interface", "enum", "record", "extends",
            "implements", "public", "private", "protected", "static", "final", "abstract",
            "synchronized", "volatile", "return", "if", "else", "for", "while", "do", "switch",
            "case", "break", "continue", "try", "catch", "finally", "throw", "throws", "new",
            "this", "super", "import", "package", "true", "false", "null", "void", "int", "long",
            "double", "float", "boolean", "char", "byte", "String", "instanceof" },
    };

    public static HashSet<string>? GetKeywords(string? lang)
    {
        var normalized = DetectLanguage(lang);
        if (string.IsNullOrEmpty(normalized)) return null;
        KeywordSets.TryGetValue(normalized, out var keywords);
        return keywords;
    }
}
