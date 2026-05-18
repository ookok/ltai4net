namespace LTAI.Capability.CodeEngine;

public enum CodeLanguage
{
    CSharp,
    Python,
    JavaScript,
    TypeScript,
    Go,
    Rust,
    Java,
    C,
    Cpp,
    Sql,
    Html,
    Css,
    Json,
    Xml,
    Yaml,
    Markdown,
    Shell,
    Unknown
}

public sealed class LanguageInfo
{
    public CodeLanguage Language { get; init; }
    public string Name { get; init; } = "";
    public string[] Extensions { get; init; } = Array.Empty<string>();
    public string[] Keywords { get; init; } = Array.Empty<string>();
    public string SingleLineComment { get; init; } = "//";
    public string MultiLineCommentStart { get; init; } = "/*";
    public string MultiLineCommentEnd { get; init; } = "*/";
    public bool IsCompiled { get; init; }
    public bool SupportsTreeSitter { get; init; } = true;
}

public static class LanguageRegistry
{
    public static readonly Dictionary<CodeLanguage, LanguageInfo> Languages = new()
    {
        [CodeLanguage.CSharp] = new()
        {
            Language = CodeLanguage.CSharp, Name = "C#",
            Extensions = new[] { ".cs", ".csx", ".csproj" },
            Keywords = new[] { "class", "struct", "interface", "enum", "record", "namespace", "using", "var", "async", "await", "public", "private", "protected", "static", "sealed", "abstract", "virtual", "override", "new", "return", "if", "else", "switch", "for", "foreach", "while", "do", "try", "catch", "throw", "null", "true", "false" },
            IsCompiled = true
        },
        [CodeLanguage.Python] = new()
        {
            Language = CodeLanguage.Python, Name = "Python",
            Extensions = new[] { ".py", ".pyw", ".pyx" },
            Keywords = new[] { "def", "class", "import", "from", "as", "if", "elif", "else", "for", "while", "try", "except", "finally", "with", "as", "return", "yield", "raise", "lambda", "async", "await", "pass", "break", "continue", "global", "nonlocal", "True", "False", "None", "self", "and", "or", "not", "in", "is" },
            SingleLineComment = "#",
            MultiLineCommentStart = "\"\"\"",
            MultiLineCommentEnd = "\"\"\"",
            IsCompiled = false
        },
        [CodeLanguage.JavaScript] = new()
        {
            Language = CodeLanguage.JavaScript, Name = "JavaScript",
            Extensions = new[] { ".js", ".jsx", ".mjs" },
            Keywords = new[] { "function", "class", "const", "let", "var", "if", "else", "for", "while", "switch", "case", "return", "async", "await", "try", "catch", "throw", "new", "this", "super", "import", "export", "default", "from", "typeof", "instanceof", "null", "undefined", "true", "false", "console" },
            IsCompiled = false
        },
        [CodeLanguage.TypeScript] = new()
        {
            Language = CodeLanguage.TypeScript, Name = "TypeScript",
            Extensions = new[] { ".ts", ".tsx" },
            Keywords = new[] { "function", "class", "const", "let", "var", "interface", "type", "enum", "namespace", "module", "if", "else", "for", "while", "async", "await", "return", "throw", "import", "export", "as", "implements", "extends", "readonly", "public", "private", "protected", "abstract", "static", "null", "undefined" },
            IsCompiled = true
        },
        [CodeLanguage.Go] = new()
        {
            Language = CodeLanguage.Go, Name = "Go",
            Extensions = new[] { ".go" },
            Keywords = new[] { "func", "type", "struct", "interface", "package", "import", "var", "const", "if", "else", "for", "range", "switch", "case", "return", "defer", "go", "chan", "select", "map", "make", "new", "nil", "true", "false", "error", "string", "int", "bool" },
            IsCompiled = true
        },
        [CodeLanguage.Rust] = new()
        {
            Language = CodeLanguage.Rust, Name = "Rust",
            Extensions = new[] { ".rs" },
            Keywords = new[] { "fn", "struct", "enum", "trait", "impl", "mod", "use", "pub", "let", "mut", "const", "static", "if", "else", "match", "for", "while", "loop", "return", "async", "await", "move", "ref", "self", "super", "crate", "where", "type", "unsafe", "extern", "None", "Some", "Ok", "Err", "true", "false" },
            IsCompiled = true
        },
        [CodeLanguage.Java] = new()
        {
            Language = CodeLanguage.Java, Name = "Java",
            Extensions = new[] { ".java", ".kt" },
            Keywords = new[] { "class", "interface", "enum", "package", "import", "public", "private", "protected", "static", "final", "abstract", "extends", "implements", "new", "return", "if", "else", "for", "while", "do", "switch", "case", "try", "catch", "finally", "throw", "throws", "null", "true", "false", "void", "this", "super" },
            IsCompiled = true
        },
        [CodeLanguage.Sql] = new()
        {
            Language = CodeLanguage.Sql, Name = "SQL",
            Extensions = new[] { ".sql" },
            Keywords = new[] { "SELECT", "FROM", "WHERE", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "TABLE", "INDEX", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "ON", "GROUP", "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "AS", "INTO", "VALUES", "SET", "NULL", "NOT", "AND", "OR", "IN", "EXISTS", "BETWEEN", "LIKE", "CASE", "WHEN", "THEN", "ELSE", "END" },
            SingleLineComment = "--",
            IsCompiled = false
        },
        [CodeLanguage.Html] = new()
        {
            Language = CodeLanguage.Html, Name = "HTML",
            Extensions = new[] { ".html", ".htm" },
            SingleLineComment = "",
            MultiLineCommentStart = "<!--",
            MultiLineCommentEnd = "-->",
            IsCompiled = false
        },
        [CodeLanguage.Markdown] = new()
        {
            Language = CodeLanguage.Markdown, Name = "Markdown",
            Extensions = new[] { ".md", ".markdown" },
            SingleLineComment = "",
            IsCompiled = false
        },
        [CodeLanguage.Json] = new()
        {
            Language = CodeLanguage.Json, Name = "JSON",
            Extensions = new[] { ".json" },
            SingleLineComment = "",
            IsCompiled = false
        },
        [CodeLanguage.Yaml] = new()
        {
            Language = CodeLanguage.Yaml, Name = "YAML",
            Extensions = new[] { ".yml", ".yaml" },
            SingleLineComment = "#",
            IsCompiled = false
        },
    };

    public static CodeLanguage Detect(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        foreach (var (lang, info) in Languages)
        {
            if (info.Extensions.Contains(ext))
                return lang;
        }
        return CodeLanguage.Unknown;
    }

    public static LanguageInfo Get(CodeLanguage language)
        => Languages.TryGetValue(language, out var info) ? info : new LanguageInfo { Language = CodeLanguage.Unknown, Name = "Unknown" };
}
