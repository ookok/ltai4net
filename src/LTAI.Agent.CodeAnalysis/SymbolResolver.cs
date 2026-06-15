using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public enum ReferenceKind
{
    Any,
    Definition,
    Call,
    Reference,
}

public sealed class SymbolResolver : IDisposable
{
    private readonly string _workspace;
    private readonly ILogger? _logger;
    private TreeSitterParser? _tsParser;

    private static readonly HashSet<string> CsExts = new(StringComparer.OrdinalIgnoreCase) { ".cs" };

    public SymbolResolver(string workspace, ILogger? logger = null)
    {
        _workspace = workspace;
        _logger = logger;
    }

    public async Task<SymbolReference?> GetSymbolAtPosition(string filePath, int line, int column)
    {
        var fp = ResolvePath(filePath);
        if (fp == null || !File.Exists(fp)) return null;

        var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
        var ext = Path.GetExtension(fp);

        try
        {
            if (CsExts.Contains(ext))
                return GetRoslynSymbolAtPosition(fp, content, line, column);
            return GetTreeSitterSymbolAtPosition(fp, content, ext, line, column);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetSymbolAtPosition failed at {File}:{Line}:{Col}", fp, line, column);
            return null;
        }
    }

    public async Task<List<SymbolInfo>> GetSymbolsInFile(string filePath)
    {
        var fp = ResolvePath(filePath);
        if (fp == null || !File.Exists(fp)) return [];

        var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
        var ext = Path.GetExtension(fp);

        try
        {
            if (CsExts.Contains(ext))
                return GetRoslynSymbolsInFile(fp, content);
            return GetTreeSitterSymbolsInFile(fp, content, ext);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetSymbolsInFile failed for {File}", fp);
            return [];
        }
    }

    public async Task<List<SymbolReference>> GetSymbolReferences(string filePath, string symbolName, ReferenceKind kind = ReferenceKind.Any)
    {
        var fp = ResolvePath(filePath);
        if (fp == null || !File.Exists(fp)) return [];

        var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);
        var ext = Path.GetExtension(fp);

        try
        {
            if (CsExts.Contains(ext))
                return GetRoslynReferences(fp, content, symbolName, kind);
            return GetGenericReferences(fp, content, symbolName, kind);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetSymbolReferences failed for {Symbol} in {File}", symbolName, fp);
            return [];
        }
    }

    private SymbolReference? GetRoslynSymbolAtPosition(string fp, string content, int line, int column)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetRoot();
        var position = tree.GetText().Lines[line - 1].Start + column - 1;
        var token = root.FindToken(position, true);

        if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;

        var symbolName = token.Text;
        var span = tree.GetLineSpan(token.Span);
        var location = new LocationSpan(
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1);

        var kind = GetSymbolKind(token);
        return new SymbolReference(fp, symbolName, kind, location, "reference");
    }

    private SymbolReference? GetTreeSitterSymbolAtPosition(string fp, string content, string ext, int line, int column)
    {
        _tsParser ??= new TreeSitterParser(_logger);
        var symbols = _tsParser.ExtractSymbols(content, ext);

        foreach (var (kind, name, symLine, symCol) in symbols)
        {
            if (symLine == line && Math.Abs(symCol - column) <= 5)
            {
                var symbolKind = MapTreeSitterKind(kind);
                var location = new LocationSpan(symLine, symCol, symLine, symCol + name.Length);
                return new SymbolReference(fp, name, symbolKind, location, "definition");
            }
        }

        var lines = content.Split('\n');
        if (line > 0 && line <= lines.Length)
        {
            var text = lines[line - 1];
            foreach (var (kind, name, symLine, symCol) in symbols)
            {
                if (text.Contains(name, StringComparison.Ordinal))
                {
                    var location = new LocationSpan(line, text.IndexOf(name, StringComparison.Ordinal) + 1, line, text.Length);
                    return new SymbolReference(fp, name, MapTreeSitterKind(kind), location, "reference");
                }
            }
        }

        return null;
    }

    private List<SymbolInfo> GetRoslynSymbolsInFile(string fp, string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetRoot();
        var results = new List<SymbolInfo>();

        foreach (var node in root.DescendantNodes())
        {
            SymbolKind? kind = null;
            string? name = null;

            if (node is ClassDeclarationSyntax cds) { kind = SymbolKind.Class; name = cds.Identifier.Text; }
            else if (node is MethodDeclarationSyntax mds) { kind = SymbolKind.Method; name = mds.Identifier.Text; }
            else if (node is PropertyDeclarationSyntax pds) { kind = SymbolKind.Property; name = pds.Identifier.Text; }
            else if (node is InterfaceDeclarationSyntax ids) { kind = SymbolKind.Interface; name = ids.Identifier.Text; }
            else if (node is EnumDeclarationSyntax eds) { kind = SymbolKind.Enum; name = eds.Identifier.Text; }
            else if (node is FieldDeclarationSyntax fds)
            {
                kind = SymbolKind.Field;
                name = fds.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
            }

            if (kind != null && name != null)
            {
                var span = tree.GetLineSpan(node.Span);
                var location = new LocationSpan(
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1,
                    span.EndLinePosition.Line + 1,
                    span.EndLinePosition.Character + 1);

                results.Add(new SymbolInfo(name, kind.Value, fp, location, node.ToFullString(), ""));
            }
        }

        return results;
    }

    private List<SymbolInfo> GetTreeSitterSymbolsInFile(string fp, string content, string ext)
    {
        _tsParser ??= new TreeSitterParser(_logger);
        var symbols = _tsParser.ExtractSymbols(content, ext);
        var results = new List<SymbolInfo>();

        foreach (var (kind, name, line, col) in symbols)
        {
            var location = new LocationSpan(line, col, line, col + name.Length);
            results.Add(new SymbolInfo(name, MapTreeSitterKind(kind), fp, location, "", ""));
        }

        return results;
    }

    private List<SymbolReference> GetRoslynReferences(string fp, string content, string symbolName, ReferenceKind kind)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetRoot();
        var results = new List<SymbolReference>();

        foreach (var token in root.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == symbolName)
            .Where(t => !IsInsideCommentOrString(t)))
        {
            var span = tree.GetLineSpan(token.Span);
            var location = new LocationSpan(
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                span.EndLinePosition.Line + 1,
                span.EndLinePosition.Character + 1);

            var role = GetReferenceRole(token);
            if (FilterRole(role, kind)) continue;

            var symbolKind = GetSymbolKind(token);
            results.Add(new SymbolReference(fp, symbolName, symbolKind, location, role));
        }

        return results;
    }

    private List<SymbolReference> GetGenericReferences(string fp, string content, string symbolName, ReferenceKind kind)
    {
        var lines = content.Split('\n');
        var results = new List<SymbolReference>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var idx = line.IndexOf(symbolName, StringComparison.Ordinal);
            if (idx < 0) continue;

            var role = "reference";
            if (line.Contains("class " + symbolName) || line.Contains("def " + symbolName)
                || line.Contains("fn " + symbolName) || line.Contains("func " + symbolName)
                || line.Contains("function " + symbolName))
                role = "definition";
            else if (line.Contains(symbolName + "("))
                role = "call";

            if (FilterRole(role, kind)) continue;

            var location = new LocationSpan(i + 1, idx + 1, i + 1, idx + symbolName.Length);
            results.Add(new SymbolReference(fp, symbolName, SymbolKind.Method, location, role));
        }

        return results;
    }

    private static bool FilterRole(string role, ReferenceKind kind) => kind switch
    {
        ReferenceKind.Definition => role != "definition",
        ReferenceKind.Call => role != "call",
        ReferenceKind.Reference => role != "reference" && role != "call",
        _ => false,
    };

    private static string GetReferenceRole(SyntaxToken token)
    {
        if (token.Parent is MethodDeclarationSyntax or VariableDeclaratorSyntax
            or PropertyDeclarationSyntax or BaseTypeDeclarationSyntax or ParameterSyntax)
            return "definition";
        if (token.Parent is InvocationExpressionSyntax or ObjectCreationExpressionSyntax)
            return "call";
        return "reference";
    }

    private static SymbolKind GetSymbolKind(SyntaxToken token)
    {
        if (token.Parent is ClassDeclarationSyntax) return SymbolKind.Class;
        if (token.Parent is MethodDeclarationSyntax) return SymbolKind.Method;
        if (token.Parent is PropertyDeclarationSyntax) return SymbolKind.Property;
        if (token.Parent is InterfaceDeclarationSyntax) return SymbolKind.Interface;
        if (token.Parent is EnumDeclarationSyntax) return SymbolKind.Enum;
        if (token.Parent is FieldDeclarationSyntax || token.Parent is VariableDeclaratorSyntax)
            return SymbolKind.Field;
        return SymbolKind.Method;
    }

    private static SymbolKind MapTreeSitterKind(string kind) => kind.ToLowerInvariant() switch
    {
        "class" => SymbolKind.Class,
        "method" or "function" => SymbolKind.Method,
        "property" => SymbolKind.Property,
        "interface" => SymbolKind.Interface,
        "enum" => SymbolKind.Enum,
        "field" => SymbolKind.Field,
        _ => SymbolKind.Method,
    };

    private static bool IsInsideCommentOrString(SyntaxToken token)
    {
        foreach (var t in token.LeadingTrivia.Concat(token.TrailingTrivia))
            if (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || t.IsKind(SyntaxKind.StringLiteralToken) || t.IsKind(SyntaxKind.InterpolatedStringTextToken))
                return true;
        return false;
    }

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_workspace, path);

    public void Dispose() => _tsParser?.Dispose();
}
