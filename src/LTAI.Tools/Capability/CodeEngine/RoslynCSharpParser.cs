using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.CodeEngine;

public sealed class RoslynCSharpParser : ICodeParser
{
    private readonly ILogger<RoslynCSharpParser> _logger;

    public RoslynCSharpParser(ILogger<RoslynCSharpParser>? logger = null)
    {
        _logger = logger ?? NullLogger<RoslynCSharpParser>.Instance;
    }

    public CodeLanguage Language => CodeLanguage.CSharp;
    public bool SupportsDiagnostics => true;

    public Task<CodeParseResult> ParseAsync(string sourceCode, string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
        var root = tree.GetCompilationUnitRoot();
        var lines = sourceCode.Split('\n');

        var result = new CodeParseResult
        {
            Language = CodeLanguage.CSharp,
            FilePath = filePath ?? "",
            TotalLines = lines.Length,
            CodeLines = CountCodeLines(lines),
            CommentLines = CountCommentLines(lines),
            BlankLines = CountBlankLines(lines),
            Functions = ExtractFunctions(root, lines),
            Classes = ExtractClasses(root),
            Imports = ExtractImports(root),
            Variables = ExtractVariables(root),
            Diagnostics = ExtractDiagnostics(tree.GetDiagnostics(cancellationToken)),
            CyclomaticComplexity = ComputeCyclomaticComplexity(root),
            RootNode = ConvertSyntaxNode(root, lines),
        };

        _logger.LogInformation("Roslyn parsed {Path}: {Funcs} functions, {Classes} classes, {Imports} imports, {Diags} diagnostics",
            filePath ?? "(memory)", result.Functions.Count, result.Classes.Count, result.Imports.Count, result.Diagnostics.Count);

        return Task.FromResult(result);
    }

    private static List<AstFunction> ExtractFunctions(CompilationUnitSyntax root, string[] lines)
    {
        var functions = new List<AstFunction>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var lineSpan = method.GetLocation().GetLineSpan();
            var line = lineSpan.StartLinePosition.Line + 1;
            var endLine = method.Body != null
                ? method.Body.GetLocation().GetLineSpan().EndLinePosition.Line + 1
                : line + EstimateLines(method.ToString());
            var parameters = method.ParameterList.Parameters
                .Select(p => p.Identifier.Text).ToList();
            var modifiers = method.Modifiers.Select(m => m.Text).ToList();

            var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(inv =>
                {
                    var span = inv.GetLocation().GetLineSpan();
                    var target = inv.Expression switch
                    {
                        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                        IdentifierNameSyntax id => id.Identifier.Text,
                        _ => inv.Expression.ToString(),
                    };
                    var obj = inv.Expression is MemberAccessExpressionSyntax m2
                        ? m2.Expression.ToString()
                        : null;
                    return new AstFunctionCall
                    {
                        Target = target,
                        Object = obj,
                        Line = span.StartLinePosition.Line + 1,
                        Column = span.StartLinePosition.Character + 1,
                        Arguments = inv.ArgumentList.Arguments.Select(a => a.ToString()).ToList(),
                    };
                }).ToList();

            functions.Add(new AstFunction
            {
                Name = method.Identifier.Text,
                Line = line,
                EndLine = endLine,
                Column = lineSpan.StartLinePosition.Character + 1,
                ReturnType = method.ReturnType.ToString(),
                Parameters = parameters,
                Modifiers = modifiers,
                ParentClass = method.Parent is ClassDeclarationSyntax cls ? cls.Identifier.Text : null,
                Documentation = method.GetLeadingTrivia()
                    .Select(t => t.GetStructure())
                    .OfType<DocumentationCommentTriviaSyntax>()
                    .FirstOrDefault()?.ToString(),
                Calls = calls,
                Complexity = ComputeMethodComplexity(method),
            });
        }

        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            var lineSpan = ctor.GetLocation().GetLineSpan();
            functions.Add(new AstFunction
            {
                Name = ctor.Identifier.Text,
                Line = lineSpan.StartLinePosition.Line + 1,
                EndLine = ctor.Body?.GetLocation().GetLineSpan().EndLinePosition.Line + 1 ?? lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                ReturnType = "constructor",
                Parameters = ctor.ParameterList.Parameters.Select(p => p.Identifier.Text).ToList(),
                Modifiers = ctor.Modifiers.Select(m => m.Text).ToList(),
                ParentClass = ctor.Parent is ClassDeclarationSyntax cls ? cls.Identifier.Text : null,
                Complexity = ComputeMethodComplexity(ctor),
            });
        }

        return functions;
    }

    private static List<AstClass> ExtractClasses(CompilationUnitSyntax root)
    {
        var classes = new List<AstClass>();
        foreach (var decl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var lineSpan = decl.GetLocation().GetLineSpan();
            var kind = decl switch
            {
                ClassDeclarationSyntax => "class",
                StructDeclarationSyntax => "struct",
                InterfaceDeclarationSyntax => "interface",
                RecordDeclarationSyntax recordDecl when recordDecl.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => "record struct",
                RecordDeclarationSyntax => "record",
                EnumDeclarationSyntax => "enum",
                _ => "type",
            };

            var methods = decl.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Parent == decl).Select(m => m.Identifier.Text).ToList();
            var properties = decl.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Parent == decl).Select(p => p.Identifier.Text).ToList();
            var fields = decl.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(f => f.Parent == decl)
                .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text)).ToList();

            classes.Add(new AstClass
            {
                Name = decl.Identifier.Text,
                Line = lineSpan.StartLinePosition.Line + 1,
                EndLine = decl.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                Kind = kind,
                Modifiers = decl.Modifiers.Select(m => m.Text).ToList(),
                BaseTypes = decl.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? new(),
                Methods = methods,
                Properties = properties,
                Fields = fields,
                Documentation = decl.GetLeadingTrivia()
                    .Select(t => t.GetStructure())
                    .OfType<DocumentationCommentTriviaSyntax>()
                    .FirstOrDefault()?.ToString(),
                MethodCount = methods.Count,
                PropertyCount = properties.Count,
                FieldCount = fields.Count,
            });
        }

        return classes;
    }

    private static List<AstImport> ExtractImports(CompilationUnitSyntax root)
    {
        return root.Usings.Select(u =>
        {
            var lineSpan = u.GetLocation().GetLineSpan();
            return new AstImport
            {
                Module = u.Name?.ToString() ?? string.Empty,
                Alias = u.Alias?.Name.Identifier.Text,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                ImportKind = u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? "static-using" : "using",
            };
        }).ToList();
    }

    private static List<AstVariable> ExtractVariables(CompilationUnitSyntax root)
    {
        var variables = new List<AstVariable>();
        foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (var v in local.Declaration.Variables)
            {
                var lineSpan = v.GetLocation().GetLineSpan();
                variables.Add(new AstVariable
                {
                    Name = v.Identifier.Text,
                    Type = local.Declaration.Type.ToString(),
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Column = lineSpan.StartLinePosition.Character + 1,
                    Scope = "local",
                    IsParameter = false,
                });
            }
        }

        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var v in field.Declaration.Variables)
            {
                var lineSpan = v.GetLocation().GetLineSpan();
                variables.Add(new AstVariable
                {
                    Name = v.Identifier.Text,
                    Type = field.Declaration.Type.ToString(),
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Column = lineSpan.StartLinePosition.Character + 1,
                    Scope = "field",
                    IsParameter = false,
                });
            }
        }

        return variables;
    }

    private static List<AstDiagnostic> ExtractDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        return diagnostics.Select(d =>
        {
            var lineSpan = d.Location.GetLineSpan();
            return new AstDiagnostic
            {
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1,
                Message = d.GetMessage(),
                Severity = d.Severity switch
                {
                    DiagnosticSeverity.Hidden => AstDiagnosticSeverity.Hint,
                    DiagnosticSeverity.Info => AstDiagnosticSeverity.Information,
                    DiagnosticSeverity.Warning => AstDiagnosticSeverity.Warning,
                    DiagnosticSeverity.Error => AstDiagnosticSeverity.Error,
                    _ => AstDiagnosticSeverity.Information,
                },
                Code = d.Id,
                Source = "roslyn",
            };
        }).ToList();
    }

    private static double ComputeCyclomaticComplexity(CompilationUnitSyntax root)
    {
        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Select(ComputeMethodComplexity)
            .DefaultIfEmpty(0)
            .Average();
    }

    private static double ComputeMethodComplexity(SyntaxNode method)
    {
        var count = 1.0;
        foreach (var node in method.DescendantNodes())
        {
            count += node.Kind() switch
            {
                SyntaxKind.IfStatement => 1,
                SyntaxKind.ElseClause => 1,
                SyntaxKind.ForStatement => 1,
                SyntaxKind.ForEachStatement => 1,
                SyntaxKind.WhileStatement => 1,
                SyntaxKind.DoStatement => 1,
                SyntaxKind.SwitchSection => 1,
                SyntaxKind.CaseSwitchLabel => 0.5,
                SyntaxKind.CatchClause => 1,
                SyntaxKind.LogicalAndExpression => 0.5,
                SyntaxKind.LogicalOrExpression => 0.5,
                SyntaxKind.ConditionalExpression => 0.5,
                SyntaxKind.CoalesceExpression => 0.5,
                _ => 0,
            };
        }
        return Math.Round(count, 1);
    }

    private static AstSyntaxNode ConvertSyntaxNode(SyntaxNode node, string[] lines)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        var children = node.ChildNodes()
            .Select(c => ConvertSyntaxNode(c, lines))
            .ToList();

        var text = node.ToString();
        if (text.Length > 500) text = text[..500];

        return new AstSyntaxNode
        {
            Kind = node.Kind().ToString(),
            StartLine = lineSpan.StartLinePosition.Line + 1,
            StartColumn = lineSpan.StartLinePosition.Character + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            EndColumn = lineSpan.EndLinePosition.Character + 1,
            Children = children,
            Text = text,
        };
    }

    private static int CountCodeLines(string[] lines)
    {
        return lines.Count(l =>
        {
            var t = l.Trim();
            return !string.IsNullOrEmpty(t) && !t.StartsWith("//") && !t.StartsWith("/*") && !t.StartsWith("*");
        });
    }

    private static int CountCommentLines(string[] lines)
    {
        return lines.Count(l =>
        {
            var t = l.Trim();
            return t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith("*") || t == "*/";
        });
    }

    private static int CountBlankLines(string[] lines) => lines.Count(string.IsNullOrWhiteSpace);

    private static int EstimateLines(string code) => Math.Max(1, code.Count(c => c == '\n'));
}
