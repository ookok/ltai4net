// ──────────────────────────────────────────────────────────────
//  GrammarCheckStep — 第 1 层: QuickParse 快速语法分析
//  Roslyn (C#) + TreeSitter (Python/JS/Go/Rust/Java 等)
// ──────────────────────────────────────────────────────────────

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public sealed partial class GrammarCheckStep
{
    private List<GrammarError> QuickParseFile(string filePath, string content)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => QuickParseCSharp(filePath, content),
            ".py" or ".js" or ".jsx" or ".ts" or ".tsx"
                or ".go" or ".rs" or ".java"
                or ".sh" or ".bash" or ".json" or ".html" or ".css"
                or ".mbt" or ".mojo" or ".cj" or "🔥"
                => QuickParseTreeSitter(filePath, content, ext),
            _ => []
        };
    }

    private static List<GrammarError> QuickParseCSharp(string filePath, string content)
    {
        var errors = new List<GrammarError>();
        try
        {
            var tree = CSharpSyntaxTree.ParseText(content, options: null, filePath);
            foreach (var diag in tree.GetDiagnostics())
            {
                if (diag.Severity == DiagnosticSeverity.Hidden) continue;
                var lineSpan = diag.Location.GetLineSpan();
                errors.Add(new GrammarError(filePath,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    diag.Severity switch
                    {
                        DiagnosticSeverity.Error => GrammarErrorSeverity.Error,
                        DiagnosticSeverity.Warning => GrammarErrorSeverity.Warning,
                        _ => GrammarErrorSeverity.Info
                    }, "syntax", diag.Id, diag.GetMessage(), "Roslyn"));
            }
        }
        catch (Exception ex)
        {
            errors.Add(new GrammarError(filePath, 1, 1,
                GrammarErrorSeverity.Error, "syntax", "ROSLYN-FATAL",
                $"Roslyn parse failed: {ex.Message}", "Roslyn"));
        }
        return errors;
    }

    private List<GrammarError> QuickParseTreeSitter(string filePath, string content, string ext)
    {
        var errors = new List<GrammarError>();
        if (_tsParser == null)
        {
            _logger.LogDebug("GrammarCheckStep: TreeSitterParser not available, skipping {File}", filePath);
            return errors;
        }
        try
        {
            var tree = _tsParser.Parse(content, ext);
            if (tree?.RootNode == null) return errors;
            DetectTsErrors(tree.RootNode, filePath, content, errors);
        }
        catch (Exception ex)
        {
            errors.Add(new GrammarError(filePath, 1, 1,
                GrammarErrorSeverity.Error, "syntax", "TS-FATAL",
                $"TreeSitter parse failed: {ex.Message}", "TreeSitter"));
        }
        return errors;
    }

    private static void DetectTsErrors(
        global::TreeSitter.Node node, string filePath, string content,
        List<GrammarError> errors, HashSet<(int line, int col)>? seen = null)
    {
        seen ??= new HashSet<(int, int)>();
        if (node.Type == "ERROR")
        {
            var line = node.StartPosition.Row + 1;
            var col = node.StartPosition.Column + 1;
            if (seen.Add((line, col)))
            {
                var snippet = ExtractSnippet(content, node.StartPosition.Row, 40);
                errors.Add(new GrammarError(filePath, line, col,
                    GrammarErrorSeverity.Error, "syntax", "TS-ERROR",
                    $"语法错误：无法解析此处代码。上下文: \"{snippet}\"", "TreeSitter"));
            }
        }
        if (node.IsMissing)
        {
            var line = node.StartPosition.Row + 1;
            var col = node.StartPosition.Column + 1;
            if (seen.Add((line, col)))
            {
                errors.Add(new GrammarError(filePath, line, col,
                    GrammarErrorSeverity.Error, "syntax", "TS-MISSING",
                    $"语法错误：期望 \"{node.Text ?? "?"}\" 但未找到", "TreeSitter"));
            }
        }
        foreach (var child in node.Children)
            DetectTsErrors(child, filePath, content, errors, seen);
    }
}
