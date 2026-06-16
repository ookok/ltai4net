using System.Text.Json;
using LTAI.AI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.CodeAnalysis;

public enum SymbolKind
{
    Class,
    Method,
    Property,
    Field,
    Interface,
    Enum,
}

public enum EditMode
{
    Replace,
    Append,
    Prepend,
}

public sealed record LocationSpan(int StartLine, int StartColumn, int EndLine, int EndColumn);

public sealed record SymbolReference(string FilePath, string SymbolName, SymbolKind Kind, LocationSpan Location, string Role);

public sealed record SymbolInfo(string Name, SymbolKind Kind, string FilePath, LocationSpan Location, string Body, string DocComment);

public sealed record StructuredEditResult(bool Success, string FilePath, string SymbolName, int LineCount, string? ErrorMessage);

[ToolDomain("code")]
public sealed class StructuredCodeActions
{
    private readonly string _workspace;
    private readonly ILogger<StructuredCodeActions>? _logger;
    private readonly SymbolResolver _symbolResolver;

    public StructuredCodeActions(string workspace, ILogger<StructuredCodeActions>? logger = null)
    {
        _workspace = workspace;
        _logger = logger;
        _symbolResolver = new SymbolResolver(workspace, logger);
    }

    public async Task<StructuredEditResult> EditSymbol(
        string filePath,
        string symbolName,
        SymbolKind kind,
        string newImplementation,
        EditMode mode)
    {
        var fp = ResolvePath(filePath);
        if (fp == null)
            return new StructuredEditResult(false, filePath, symbolName, 0, "Path escape");

        if (!File.Exists(fp))
            return new StructuredEditResult(false, filePath, symbolName, 0, "File not found");

        var ext = Path.GetExtension(fp);
        var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);

        try
        {
            if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase))
                return EditCSharpSymbol(fp, symbolName, kind, newImplementation, mode, content);
            return EditGenericSymbol(fp, symbolName, newImplementation, mode, content);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EditSymbol failed for {Symbol} in {File}", symbolName, fp);
            return new StructuredEditResult(false, fp, symbolName, 0, ex.Message);
        }
    }

    public async Task<SymbolInfo?> GetSymbolDetail(
        string filePath,
        string symbolName,
        bool includeBody = true,
        bool includeDocComment = true)
    {
        var fp = ResolvePath(filePath);
        if (fp == null || !File.Exists(fp))
            return null;

        var ext = Path.GetExtension(fp);
        var content = await File.ReadAllTextAsync(fp).ConfigureAwait(false);

        try
        {
            if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase))
                return GetCSharpSymbolDetail(fp, symbolName, content, includeBody, includeDocComment);
            return GetGenericSymbolDetail(fp, symbolName, content);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetSymbolDetail failed for {Symbol} in {File}", symbolName, fp);
            return null;
        }
    }

    public async Task<IReadOnlyList<SymbolReference>> FindSymbolReferences(
        string symbolName,
        string? scopePath = null,
        ReferenceKind kind = ReferenceKind.Any)
    {
        var results = new List<SymbolReference>();
        var searchRoot = scopePath != null ? ResolvePath(scopePath) : _workspace;
        if (searchRoot == null) return results;

        try
        {
            if (Directory.Exists(searchRoot))
            {
                foreach (var file in Directory.EnumerateFiles(searchRoot, "*.*", SearchOption.AllDirectories))
                {
                    if (ShouldSkip(file)) continue;
                    var fileResults = await _symbolResolver.GetSymbolReferences(file, symbolName, kind).ConfigureAwait(false);
                    results.AddRange(fileResults);
                }
            }
            else if (File.Exists(searchRoot))
            {
                results.AddRange(await _symbolResolver.GetSymbolReferences(searchRoot, symbolName, kind).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FindSymbolReferences failed for {Symbol}", symbolName);
        }

        return results;
    }

    private StructuredEditResult EditCSharpSymbol(
        string fp, string symbolName, SymbolKind kind,
        string newImplementation, EditMode mode, string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetRoot();

        var decl = FindCSharpDeclaration(root, symbolName, kind);
        if (decl == null)
            return new StructuredEditResult(false, fp, symbolName, 0, "Symbol not found");

        var span = decl.Span;
        var newContent = mode switch
        {
            EditMode.Replace => content[..span.Start] + newImplementation + content[span.End..],
            EditMode.Append => content[..span.End] + "\n" + newImplementation + content[span.End..],
            EditMode.Prepend => content[..span.Start] + newImplementation + "\n" + content[span.Start..],
            _ => content,
        };

        File.WriteAllText(fp, newContent);

        var lineCount = newContent.Split('\n').Length;
        return new StructuredEditResult(true, fp, symbolName, lineCount, null);
    }

    private SyntaxNode? FindCSharpDeclaration(SyntaxNode root, string name, SymbolKind kind)
    {
        var candidates = root.DescendantNodes().Where(n => kind switch
        {
            SymbolKind.Class => n is ClassDeclarationSyntax cds && cds.Identifier.Text == name,
            SymbolKind.Method => n is MethodDeclarationSyntax mds && mds.Identifier.Text == name,
            SymbolKind.Property => n is PropertyDeclarationSyntax pds && pds.Identifier.Text == name,
            SymbolKind.Field => n is FieldDeclarationSyntax fds && fds.Declaration.Variables.Any(v => v.Identifier.Text == name),
            SymbolKind.Interface => n is InterfaceDeclarationSyntax ids && ids.Identifier.Text == name,
            SymbolKind.Enum => n is EnumDeclarationSyntax eds && eds.Identifier.Text == name,
            _ => false,
        });
        return candidates.FirstOrDefault();
    }

    private StructuredEditResult EditGenericSymbol(
        string fp, string symbolName,
        string newImplementation, EditMode mode, string content)
    {
        var lines = content.Split('\n');
        var matchLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(symbolName, StringComparison.Ordinal))
            {
                matchLine = i;
                break;
            }
        }

        if (matchLine < 0)
            return new StructuredEditResult(false, fp, symbolName, 0, "Symbol not found");

        var newContent = mode switch
        {
            EditMode.Replace => string.Join('\n', lines[..matchLine]) + newImplementation + string.Join('\n', lines[(matchLine + 1)..]),
            EditMode.Append => content + "\n" + newImplementation,
            EditMode.Prepend => newImplementation + "\n" + content,
            _ => content,
        };

        File.WriteAllText(fp, newContent);
        var lineCount = newContent.Split('\n').Length;
        return new StructuredEditResult(true, fp, symbolName, lineCount, null);
    }

    private SymbolInfo? GetCSharpSymbolDetail(
        string fp, string symbolName, string content,
        bool includeBody, bool includeDocComment)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetRoot();

        var decl = FindCSharpDeclaration(root, symbolName, SymbolKind.Class)
            ?? FindCSharpDeclaration(root, symbolName, SymbolKind.Method)
            ?? FindCSharpDeclaration(root, symbolName, SymbolKind.Property)
            ?? FindCSharpDeclaration(root, symbolName, SymbolKind.Interface)
            ?? FindCSharpDeclaration(root, symbolName, SymbolKind.Enum)
            ?? (SyntaxNode?)FindFieldDeclaration(root, symbolName);

        if (decl == null) return null;

        var lineSpan = tree.GetLineSpan(decl.Span);
        var location = new LocationSpan(
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            lineSpan.EndLinePosition.Character + 1);

        var kind = decl switch
        {
            ClassDeclarationSyntax => SymbolKind.Class,
            MethodDeclarationSyntax => SymbolKind.Method,
            PropertyDeclarationSyntax => SymbolKind.Property,
            InterfaceDeclarationSyntax => SymbolKind.Interface,
            EnumDeclarationSyntax => SymbolKind.Enum,
            FieldDeclarationSyntax => SymbolKind.Field,
            _ => SymbolKind.Class,
        };

        var body = includeBody ? decl.ToFullString() : "";
        var docComment = includeDocComment ? GetDocComment(decl) : "";

        return new SymbolInfo(symbolName, kind, fp, location, body, docComment);
    }

    private FieldDeclarationSyntax? FindFieldDeclaration(SyntaxNode root, string name)
    {
        return root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == name));
    }

    private static string GetDocComment(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia();
        var docComments = trivia
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                     || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(t => t.ToString());
        return string.Join("\n", docComments);
    }

    private SymbolInfo? GetGenericSymbolDetail(string fp, string symbolName, string content)
    {
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(symbolName, StringComparison.Ordinal))
            {
                var location = new LocationSpan(i + 1, 1, i + 1, lines[i].Length);
                return new SymbolInfo(symbolName, SymbolKind.Method, fp, location, lines[i].Trim(), "");
            }
        }
        return null;
    }

    private string? ResolvePath(string path) => LTAI.Core.PathUtils.SafeResolvePath(_workspace, path);

    private static readonly char Sep = System.IO.Path.DirectorySeparatorChar;

    private static bool ShouldSkip(string rel) =>
        rel.Contains($"{Sep}obj{Sep}") || rel.Contains($"{Sep}bin{Sep}") || rel.Contains($"node_modules{Sep}")
        || rel.Contains($"{Sep}dist{Sep}") || rel.Contains($"{Sep}.git{Sep}") || rel.Contains($"{Sep}.vs{Sep}");
}
