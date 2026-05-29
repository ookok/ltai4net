using TreeSitter;

namespace LTAI.Agent.Tools;

/// <summary>
/// Multi-language code parser using TreeSitter.DotNet (30+ languages, native AST).
/// Provides symbol extraction, structural outline, and node querying.
/// </summary>
public sealed class TreeSitterParser : IDisposable
{
    private readonly Parser _parser;
    private readonly Dictionary<string, Language> _languages = new();

    // Language name → (native DLL, native function)
    private static readonly Dictionary<string, (string dll, string fn)> LanguageMap = new()
    {
        ["c_sharp"] = ("tree-sitter-c-sharp", "tree_sitter_c_sharp"),
        ["python"] = ("tree-sitter-python", "tree_sitter_python"),
        ["javascript"] = ("tree-sitter-javascript", "tree_sitter_javascript"),
        ["typescript"] = ("tree-sitter-typescript", "tree_sitter_typescript"),
        ["tsx"] = ("tree-sitter-tsx", "tree_sitter_tsx"),
        ["go"] = ("tree-sitter-go", "tree_sitter_go"),
        ["rust"] = ("tree-sitter-rust", "tree_sitter_rust"),
        ["java"] = ("tree-sitter-java", "tree_sitter_java"),
        ["bash"] = ("tree-sitter-bash", "tree_sitter_bash"),
        ["json"] = ("tree-sitter-json", "tree_sitter_json"),
        ["html"] = ("tree-sitter-html", "tree_sitter_html"),
        ["css"] = ("tree-sitter-css", "tree_sitter_css"),
    };

    // Extension → language ID
    private static readonly Dictionary<string, string> ExtToLang = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "c_sharp", [".py"] = "python",
        [".js"] = "javascript", [".jsx"] = "javascript",
        [".ts"] = "typescript", [".tsx"] = "tsx",
        [".go"] = "go", [".rs"] = "rust", [".java"] = "java",
        [".sh"] = "bash", [".bash"] = "bash",
        [".json"] = "json", [".html"] = "html", [".css"] = "css",
    };

    // AST node types that declare symbols per language
    private static readonly Dictionary<string, HashSet<string>> DeclarationTypes = new()
    {
        ["c_sharp"] = new() { "class_declaration", "struct_declaration", "interface_declaration",
            "record_declaration", "method_declaration", "property_declaration",
            "enum_declaration", "constructor_declaration", "destructor_declaration" },
        ["python"] = new() { "class_definition", "function_definition" },
        ["javascript"] = new() { "class_declaration", "function_declaration", "method_definition",
            "arrow_function", "generator_function_declaration" },
        ["typescript"] = new() { "class_declaration", "function_declaration", "method_definition",
            "interface_declaration", "type_alias_declaration", "enum_declaration" },
        ["go"] = new() { "function_declaration", "method_declaration", "type_declaration" },
        ["rust"] = new() { "function_item", "struct_item", "enum_item", "trait_item",
            "type_item", "impl_item", "macro_invocation" },
        ["java"] = new() { "class_declaration", "method_declaration", "interface_declaration",
            "enum_declaration", "constructor_declaration" },
    };

    // Language → kind mapping for declaration types
    private static readonly Dictionary<string, Dictionary<string, string>> TypeToKind = new()
    {
        ["c_sharp"] = new() {
            ["class_declaration"] = "class", ["struct_declaration"] = "struct",
            ["interface_declaration"] = "interface", ["record_declaration"] = "record",
            ["method_declaration"] = "method", ["property_declaration"] = "property",
            ["enum_declaration"] = "enum", ["constructor_declaration"] = "constructor" },
        ["python"] = new() { ["class_definition"] = "class", ["function_definition"] = "method" },
        ["javascript"] = new() { ["class_declaration"] = "class", ["function_declaration"] = "method",
            ["method_definition"] = "method", ["arrow_function"] = "method" },
        ["go"] = new() { ["function_declaration"] = "method", ["method_declaration"] = "method",
            ["type_declaration"] = "type" },
        ["rust"] = new() { ["function_item"] = "method", ["struct_item"] = "struct",
            ["enum_item"] = "enum", ["trait_item"] = "trait" },
        ["java"] = new() { ["class_declaration"] = "class", ["method_declaration"] = "method",
            ["interface_declaration"] = "interface", ["enum_declaration"] = "enum" },
    };

    public TreeSitterParser()
    {
        _parser = new Parser();
    }

    /// <summary>Get the TreeSitter language for a file extension.</summary>
    public bool TryGetLanguage(string extension, out string langId)
        => ExtToLang.TryGetValue(extension, out langId);

    /// <summary>Parse source code and return the AST tree.</summary>
    public Tree? Parse(string code, string extension)
    {
        if (!ExtToLang.TryGetValue(extension, out var langId))
            return null;

        var lang = GetOrLoadLanguage(langId);
        if (lang == null) return null;

        _parser.Language = lang;
        return _parser.Parse(code);
    }

    /// <summary>Extract symbols (declarations) from source code.</summary>
    public List<(string kind, string name, int line, int col)> ExtractSymbols(string code, string extension)
    {
        var tree = Parse(code, extension);
        if (tree == null) return [];

        if (!ExtToLang.TryGetValue(extension, out var langId))
            return [];

        var declTypes = DeclarationTypes.GetValueOrDefault(langId);
        var kindMap = TypeToKind.GetValueOrDefault(langId);
        if (declTypes == null) return [];

        var symbols = new List<(string, string, int, int)>();
        ExtractDeclarations(tree.RootNode, declTypes, kindMap, symbols);
        return symbols;
    }

    private void ExtractDeclarations(Node node, HashSet<string> declTypes,
        Dictionary<string, string>? kindMap, List<(string, string, int, int)> results)
    {
        if (declTypes.Contains(node.Type))
        {
            // Find the name identifier - usually the first named child with IsNamed=true
            var nameNode = FindNameNode(node);
            var name = nameNode?.Text ?? node.Text ?? "?";
            var kind = kindMap?.GetValueOrDefault(node.Type) ?? "symbol";

            results.Add((kind, name, node.StartPosition.Row + 1, node.StartPosition.Column + 1));

            // Don't recurse into declarations (avoid nested methods in C#)
            return;
        }

        foreach (var child in node.Children)
            ExtractDeclarations(child, declTypes, kindMap, results);
    }

    private static Node? FindNameNode(Node node)
    {
        // For most declarations, the name is the first child with IsNamed=true
        foreach (var child in node.Children)
        {
            if (child.IsNamed && child.Type == "identifier")
                return child;
            if (child.IsNamed && child.Type == "name") // Rust uses "name"
                return child;
        }
        // Fallback: first child that's a non-keyword string
        foreach (var child in node.Children)
        {
            if (!child.IsNamed && !IsKeyword(child.Text ?? ""))
                return child;
        }
        return null;
    }

    private static bool IsKeyword(string text) => text switch
    {
        "class" or "struct" or "interface" or "enum" or "record" or "def" or "fn" or "func"
        or "function" or "public" or "private" or "protected" or "internal" or "static"
        or "abstract" or "virtual" or "override" or "async" or "unsafe" or "readonly"
        or "const" or "sealed" or "partial" => true,
        _ => false,
    };

    /// <summary>Get a human-readable outline of the AST for a source file.</summary>
    public string GetOutline(string code, string extension, int maxDepth = 3)
    {
        var tree = Parse(code, extension);
        if (tree == null) return "Unsupported language: " + extension;

        var sb = new System.Text.StringBuilder();
        PrintNode(tree.RootNode, "", code, maxDepth, sb);
        return sb.ToString();
    }

    private static void PrintNode(Node node, string indent, string source, int maxDepth, System.Text.StringBuilder sb, int depth = 0)
    {
        if (depth > maxDepth) return;
        var text = node.Text?.Replace("\n", "\\n") ?? "";
        if (text.Length > 50) text = text[..50] + "...";
        if (!string.IsNullOrWhiteSpace(text) || node.IsNamed)
        {
            var marker = node.IsNamed ? "■" : "·";
            sb.AppendLine($"{indent}{marker} {node.Type} \"{text}\"");
        }
        foreach (var child in node.Children)
            PrintNode(child, indent + "  ", source, maxDepth, sb, depth + 1);
    }

    private Language? GetOrLoadLanguage(string langId)
    {
        if (_languages.TryGetValue(langId, out var lang))
            return lang;

        if (!LanguageMap.TryGetValue(langId, out var spec))
            return null;

        try
        {
            lang = new Language(spec.dll, spec.fn);
            _languages[langId] = lang;
            return lang;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var lang in _languages.Values)
            lang.Dispose();
        _parser.Dispose();
    }
}
