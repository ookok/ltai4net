using System.ComponentModel;
using System.Text.RegularExpressions;
using LTAI.AI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LTAI.Agent.Tools;

/// <summary>
/// Multi-language code analysis tools.
/// C#: Roslyn AST (mature) | Others: TreeSitter AST (30+ languages).
/// No regex code parsing.
/// </summary>
[ToolDomain("code")]
public sealed class CodeAnalysisTools
{
    private readonly string _ws;
    private TreeSitterParser? _tsParser;

    private static readonly HashSet<string> CsExts = new(StringComparer.OrdinalIgnoreCase)
        { ".cs" };
    private static readonly HashSet<string> TsExts = new(StringComparer.OrdinalIgnoreCase)
        { ".py", ".js", ".jsx", ".ts", ".tsx", ".go", ".rs", ".java",
          ".sh", ".bash", ".json", ".html", ".css",
          ".mbt", ".mojo", "🔥", ".cj" };

    public CodeAnalysisTools(string ws) => _ws = ws;

    [Description("获取源代码文件的符号结构：类、方法、接口、属性、枚举等。支持 C#(Roslyn) 和 30+ 语言(TreeSitter)。\n"
        + "适用场景：了解一个类的结构、查看文件中有哪些方法、浏览接口定义、快速定位代码组织。\n"
        + "不适用场景：搜索函数调用关系（请用 FindInCode）、搜索文件内容（请用 SearchContent）。\n"
        + "关键参数：path — 文件或目录路径。")]
    [ToolExample("这个类里有哪些方法")]
    [ToolExample("看看这个文件的结构")]
    public async Task<string> GetSymbols([Description("File or directory path")] string path)
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";
        if (Directory.Exists(fp)) return await GetSymbolsFromDirAsync(fp).ConfigureAwait(false);
        if (!File.Exists(fp)) return "File not found";

        var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
        var ext = Path.GetExtension(fp);
        var fileName = Path.GetFileName(fp);

        if (CsExts.Contains(ext))
            return GetCSharpSymbols(content, fileName);
        if (TsExts.Contains(ext))
            return GetTreeSitterSymbols(content, ext, fileName);

        return "Unsupported language: " + ext;
    }

    [Description("在代码中搜索标识符的使用位置。支持按角色筛选：定义/调用/引用。\n"
        + "适用场景：查找某个函数在哪里被调用、找变量的定义位置、确认某个 API 的所有使用处。\n"
        + "不适用场景：搜索文本内容（请用 SearchContent）、获取代码结构（请用 GetSymbols）。\n"
        + "关键参数：name — 要搜索的标识符；path — 文件路径；kind — 筛选角色(definition/call/reference/any)。")]
    [ToolExample("找一下这个方法在哪里被调用的")]
    [ToolExample("搜索这个类在哪些地方被引用了")]
    public async Task<string> FindInCode(
        [Description("Identifier to search")] string name,
        [Description("File path")] string path,
        [Description("Filter: definition, call, reference, any")] string kind = "any")
    {
        var fp = ResolvePath(path);
        if (fp == null) return "Error: Path escape";
        if (!File.Exists(fp)) return "File not found";

        var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
        var ext = Path.GetExtension(fp);

        if (CsExts.Contains(ext))
            return FindInCSharpCode(name, content, kind, fp);

        return FindInGenericCode(name, content, kind, fp);
    }

    private async Task<string> GetSymbolsFromDirAsync(string dir)
    {
        var sb = new System.Text.StringBuilder();
        var allExts = CsExts.Concat(TsExts).ToArray();

        foreach (var ext in allExts)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*" + ext, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(_ws, file).Replace('\\', '/');
                if (ShouldSkip(rel)) continue;
                try
                {
                    var content = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                    var syms = CsExts.Contains(ext)
                        ? GetCSharpSymbols(content, Path.GetFileName(file))
                        : GetTreeSitterSymbols(content, ext, Path.GetFileName(file));
                    if (!string.IsNullOrEmpty(syms) && !syms.Contains("No symbols"))
                    { sb.AppendLine($"\n## {rel}"); sb.AppendLine(syms); }
                }
                catch { /* non-C# file — skip silently */ }
            }
        }
        return sb.Length > 0 ? sb.ToString() : "No symbols found.";
    }

    // ─── Roslyn (C#) ───

    private string GetCSharpSymbols(string content, string fileName)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(content);
            var root = tree.GetRoot();
            var syms = new List<(string kind, string name, int line)>();
            var types = new[] { typeof(TypeDeclarationSyntax), typeof(MethodDeclarationSyntax),
                typeof(PropertyDeclarationSyntax), typeof(EnumDeclarationSyntax) };
            var kindMap = new Dictionary<Type, string>
            {
                [typeof(ClassDeclarationSyntax)] = "class", [typeof(StructDeclarationSyntax)] = "struct",
                [typeof(InterfaceDeclarationSyntax)] = "interface", [typeof(RecordDeclarationSyntax)] = "record",
                [typeof(MethodDeclarationSyntax)] = "method",
                [typeof(PropertyDeclarationSyntax)] = "property",
                [typeof(EnumDeclarationSyntax)] = "enum",
            };

            // 单次树遍历（替代 5 次）
            foreach (var n in root.DescendantNodes())
            {
                var type = n.GetType();
                if (kindMap.TryGetValue(type, out var kind))
                {
                    var identifier = type switch
                    {
                        Type _ when n is TypeDeclarationSyntax tds => tds.Identifier.Text,
                        Type _ when n is MethodDeclarationSyntax mds => mds.Identifier.Text,
                        Type _ when n is PropertyDeclarationSyntax pds => pds.Identifier.Text,
                        Type _ when n is EnumDeclarationSyntax eds => eds.Identifier.Text,
                        _ => ""
                    };
                    syms.Add((kind, identifier, tree.GetLineSpan(n.Span).StartLinePosition.Line + 1));
                }
            }

            if (syms.Count == 0 && root.DescendantNodes().OfType<GlobalStatementSyntax>().Any())
                syms.Add(("program", "Top-level statements", 1));

            return FormatSymbols(syms, fileName, "C#");
        }
        catch { return "Error parsing C#"; }
    }

    private string FindInCSharpCode(string name, string content, string kind, string filePath)
    {
        try
        {
            var tree = CSharpSyntaxTree.ParseText(content);
            var root = tree.GetRoot();
            var results = new List<(int line, int col, string role, string snippet)>();

            foreach (var token in root.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == name)
                .Where(t => !IsInsideCommentOrString(t)))
            {
                var line = tree.GetLineSpan(token.Span).StartLinePosition.Line + 1;
                var col = tree.GetLineSpan(token.Span).StartLinePosition.Character + 1;
                var role = DetermineRole(token);
                if (kind != "any" && !MatchKind(role, kind)) continue;
                var snippet = content.Split('\n')[line - 1].Trim();
                results.Add((line, col, role, snippet));
            }
            return FormatFindResults(results, name, Path.GetFileName(filePath));
        }
        catch (Exception ex) { return "Error: " + ex.Message; }
    }

    // ─── TreeSitter (Python, JS, Go, Rust, Java, ...) ───

    private string GetTreeSitterSymbols(string content, string ext, string fileName)
    {
        _tsParser ??= new TreeSitterParser();
        var symbols = _tsParser.ExtractSymbols(content, ext);
        if (symbols.Count == 0) return "No symbols found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### {fileName}");
        foreach (var (kind, name, line, col) in symbols)
            sb.AppendLine($"  L{line,5}:{col,-3}  {kind,-10} {name}");
        return sb.ToString();
    }

    private string FindInGenericCode(string name, string content, string kind, string filePath)
    {
        var lines = content.Split('\n');
        var results = new List<(int line, int col, string snippet)>();
        // 预编译定义检测正则（只编译一次，非每行）
        var defPattern = $@"\b(?:class|struct|interface|enum|def|fn|function)\s+{Regex.Escape(name)}\b";
        var defRegex = new Regex(defPattern, RegexOptions.Compiled);
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("//") || t.StartsWith("#") || t.StartsWith("/*") || t.StartsWith("*")) continue;
            var idx = t.IndexOf(name, StringComparison.Ordinal);
            if (idx < 0) continue;
            var role = "reference";
            if (defRegex.IsMatch(t)) role = "definition";
            else if (t.Contains(name + "(")) role = "call";
            if (kind != "any" && !MatchKind(role, kind)) continue;
            results.Add((i + 1, idx + 1, t));
        }
        return FormatFindResults(results.Select(r => (r.line, r.col, "", r.snippet)).ToList(), name, Path.GetFileName(filePath));
    }

    // ─── Helpers ───

    private static string FormatSymbols(List<(string k, string n, int l)> syms, string file, string lang)
    {
        if (syms.Count == 0) return "No symbols found.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### {file} ({lang})");
        foreach (var (k, n, l) in syms.OrderBy(s => s.l))
            sb.AppendLine($"  L{l,5}  {k,-10} {n}");
        return sb.ToString();
    }

    private static string FormatFindResults(List<(int l, int c, string role, string s)> results, string name, string file)
    {
        if (results.Count == 0) return $"No matches for '{name}' in {file}";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {results.Count} match(es) for '{name}' in {file}:");
        foreach (var (l, c, role, s) in results)
            sb.AppendLine($"  L{l}:{c} [{role}] {Truncate(s, 80)}");
        return sb.ToString();
    }

    private static string DetermineRole(SyntaxToken token)
    {
        if (token.Parent is MethodDeclarationSyntax or VariableDeclaratorSyntax
            or PropertyDeclarationSyntax or BaseTypeDeclarationSyntax or ParameterSyntax)
            return "definition";
        if (token.Parent is InvocationExpressionSyntax or ObjectCreationExpressionSyntax)
            return "call";
        return "reference";
    }

    private static bool MatchKind(string role, string filter) => filter.ToLowerInvariant() switch
    {
        "definition" => role == "definition",
        "call" => role == "call",
        "reference" => role is "reference" or "call",
        _ => true,
    };

    private static bool IsInsideCommentOrString(SyntaxToken token)
    {
        foreach (var t in token.LeadingTrivia.Concat(token.TrailingTrivia))
            if (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || t.IsKind(SyntaxKind.StringLiteralToken) || t.IsKind(SyntaxKind.InterpolatedStringTextToken))
                return true;
        return false;
    }

    private static readonly char s_sep = System.IO.Path.DirectorySeparatorChar;

    private static bool ShouldSkip(string rel) =>
        rel.Contains($"{s_sep}obj{s_sep}") || rel.Contains($"{s_sep}bin{s_sep}") || rel.Contains($"node_modules{s_sep}")
        || rel.Contains("/dist/") || rel.Contains("/.git/") || rel.Contains("/.vs/");

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_ws, path);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
