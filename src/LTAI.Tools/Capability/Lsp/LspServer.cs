using System.Diagnostics;
using System.Text.Json;
using LTAI.Tools.CodeEngine;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Lsp;

public sealed class LspServer
{
    private readonly ParserRegistry _parserRegistry;
    private readonly Dictionary<string, string> _openDocuments = new();
    private readonly ILogger<LspServer> _logger;

    public LspServer(ParserRegistry parserRegistry, ILogger<LspServer> logger)
    {
        _parserRegistry = parserRegistry;
        _logger = logger;
    }

    public async Task<string?> HandleMessageAsync(string json)
    {
        var msg = LspMessage.FromJson(json);
        if (msg == null) return null;

        if (msg.IsRequest && msg.Id.HasValue)
        {
            var result = await HandleRequestAsync(msg.Method!, msg.Params).ConfigureAwait(false);
            var response = new LspMessage { Id = msg.Id.Value, Result = result };
            return response.ToJson();
        }

        if (msg.IsNotification)
        {
            await HandleNotificationAsync(msg.Method!, msg.Params).ConfigureAwait(false);
            return null;
        }

        return null;
    }

    private async Task<JsonElement?> HandleRequestAsync(string method, JsonElement? @params)
    {
        try
        {
            return method switch
            {
                LspMethods.Initialize => JsonSerializer.SerializeToElement(HandleInitialize(@params)),
                LspMethods.Shutdown => JsonSerializer.SerializeToElement(new { }),
                LspMethods.TextDocumentHover => JsonSerializer.SerializeToElement(
                    await HandleHoverAsync(@params)),
                LspMethods.TextDocumentCompletion => JsonSerializer.SerializeToElement(
                    await HandleCompletionAsync(@params)),
                LspMethods.TextDocumentDefinition => JsonSerializer.SerializeToElement(
                    await HandleDefinitionAsync(@params)),
                LspMethods.TextDocumentReferences => JsonSerializer.SerializeToElement(
                    await HandleReferencesAsync(@params)),
                LspMethods.TextDocumentDocumentSymbol => JsonSerializer.SerializeToElement(
                    await HandleDocumentSymbolAsync(@params)),
                LspMethods.TextDocumentDiagnostic => JsonSerializer.SerializeToElement(
                    await HandleDiagnosticAsync(@params)),
                LspMethods.TextDocumentCodeAction => JsonSerializer.SerializeToElement(
                    await HandleCodeActionAsync(@params)),
                LspMethods.WorkspaceSymbol => JsonSerializer.SerializeToElement(
                    new List<object>()),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LSP request handler error for {Method}", method);
            return null;
        }
    }

    private async Task HandleNotificationAsync(string method, JsonElement? @params)
    {
        try
        {
            switch (method)
            {
                case LspMethods.Initialized:
                    _logger.LogInformation("LSP client initialized");
                    break;
                case LspMethods.TextDocumentDidOpen:
                    HandleDidOpen(@params);
                    break;
                case LspMethods.TextDocumentDidChange:
                    HandleDidChange(@params);
                    break;
                case LspMethods.TextDocumentDidClose:
                    HandleDidClose(@params);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LSP notification handler error for {Method}", method);
        }
    }

    private static InitializeResult HandleInitialize(JsonElement? @params)
    {
        return new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = 1,
                HoverProvider = true,
                CompletionProvider = new CompletionOptions
                {
                    TriggerCharacters = new[] { ".", ":", "\"", "/" }
                },
                DefinitionProvider = true,
                ReferencesProvider = true,
                DocumentSymbolProvider = true,
                DiagnosticProvider = new DiagnosticOptions
                {
                    Identifier = "ltai",
                    InterFileDependencies = false,
                    WorkspaceDiagnostics = false,
                },
                CodeActionProvider = true,
            },
            ServerInfo = new ServerInfo { Name = "LTAI", Version = "1.0.0" },
        };
    }

    private void HandleDidOpen(JsonElement? @params)
    {
        if (!@params.HasValue) return;
        var doc = JsonSerializer.Deserialize<TextDocumentItem>(@params.Value.GetRawText());
        if (doc == null) return;
        _openDocuments[doc.Uri] = doc.Text;
        _logger.LogInformation("LSP document opened: {Uri} ({Lang})", doc.Uri, doc.LanguageId);
    }

    private void HandleDidChange(JsonElement? @params)
    {
        if (!@params.HasValue) return;
        try
        {
            var uri = @params.Value.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
            var changes = @params.Value.GetProperty("contentChanges");
            if (changes.GetArrayLength() > 0)
            {
                var text = changes[0].GetProperty("text").GetString() ?? "";
                _openDocuments[uri] = text;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LSP: Failed to parse code action diagnostics"); }
    }

    private void HandleDidClose(JsonElement? @params)
    {
        if (!@params.HasValue) return;
        try
        {
            var uri = @params.Value.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
            _openDocuments.Remove(uri);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LSP: Failed to handle didClose"); }
    }

    private async Task<HoverResult?> HandleHoverAsync(JsonElement? @params)
    {
        var (code, language, pos) = await ResolvePosition(@params).ConfigureAwait(false);
        if (code == null || language == null) return null;

        var parser = _parserRegistry.GetParser(language.Value);
        if (parser == null) return null;

        var result = await parser.ParseAsync(code).ConfigureAwait(false);
        var line = pos.Line + 1;

        var func = result.Functions.FirstOrDefault(f => f.Line <= line && f.EndLine >= line);
        if (func != null)
        {
            var sig = $"{string.Join(" ", func.Modifiers)} {(string.IsNullOrEmpty(func.ReturnType) ? "void" : func.ReturnType)} {func.Name}({string.Join(", ", func.Parameters)})";
            var md = $"```\n{sig}\n```\n\n**{func.Name}**  (line {func.Line})";
            if (!string.IsNullOrEmpty(func.Documentation))
                md += $"\n\n{func.Documentation}";
            return new HoverResult { Contents = new MarkupContent { Kind = "markdown", Value = md } };
        }

        var cls = result.Classes.FirstOrDefault(c => c.Line <= line && c.EndLine >= line);
        if (cls != null)
        {
            var md = $"**{cls.Kind} {cls.Name}**  (line {cls.Line})\n\nMethods: {cls.MethodCount} | Properties: {cls.PropertyCount} | Fields: {cls.FieldCount}";
            if (!string.IsNullOrEmpty(cls.Documentation))
                md += $"\n\n{cls.Documentation}";
            return new HoverResult { Contents = new MarkupContent { Kind = "markdown", Value = md } };
        }

        return null;
    }

    private async Task<CompletionList?> HandleCompletionAsync(JsonElement? @params)
    {
        var (code, language, _) = await ResolvePosition(@params).ConfigureAwait(false);
        if (code == null || language == null)
            return new CompletionList { IsIncomplete = false, Items = new() };

        var parser = _parserRegistry.GetParser(language.Value);
        if (parser == null)
            return new CompletionList { IsIncomplete = false, Items = new() };

        var result = await parser.ParseAsync(code).ConfigureAwait(false);
        var items = new List<CompletionItem>();

        foreach (var func in result.Functions)
            items.Add(new CompletionItem
            {
                Label = func.Name,
                Kind = CompletionItemKind.Function,
                Detail = $"{func.ReturnType} {func.Name}({string.Join(", ", func.Parameters)})",
            });

        foreach (var cls in result.Classes)
            items.Add(new CompletionItem
            {
                Label = cls.Name,
                Kind = cls.Kind switch
                { "interface" => CompletionItemKind.Interface, _ => CompletionItemKind.Class },
                Detail = $"{cls.Kind} {cls.Name}",
            });

        foreach (var v in result.Variables)
            items.Add(new CompletionItem
            {
                Label = v.Name,
                Kind = CompletionItemKind.Variable,
                Detail = $"{v.Type} {v.Name} ({v.Scope})",
            });

        return new CompletionList { IsIncomplete = false, Items = items };
    }

    private async Task<List<Location>?> HandleDefinitionAsync(JsonElement? @params)
    {
        var (code, language, pos) = await ResolvePosition(@params).ConfigureAwait(false);
        if (code == null || language == null) return new();

        var parser = _parserRegistry.GetParser(language.Value);
        if (parser == null) return new();

        var result = await parser.ParseAsync(code).ConfigureAwait(false);
        var line = pos.Line + 1;
        var uri = ExtractUri(@params) ?? "file:///untitled";

        var func = result.Functions.FirstOrDefault(f => f.Line <= line && f.EndLine >= line);
        if (func != null)
        {
            return new List<Location>
            {
                new()
                {
                    Uri = uri,
                    Range = new Range
                    {
                        Start = new Position { Line = func.Line - 1, Character = func.Column - 1 },
                        End = new Position { Line = func.EndLine - 1, Character = 0 },
                    },
                },
            };
        }

        return new();
    }

    private async Task<List<Location>?> HandleReferencesAsync(JsonElement? @params)
    {
        var (code, language, pos) = await ResolvePosition(@params).ConfigureAwait(false);
        if (code == null || language == null) return new();

        var parser = _parserRegistry.GetParser(language.Value);
        if (parser == null) return new();

        var result = await parser.ParseAsync(code).ConfigureAwait(false);
        var line = pos.Line + 1;
        var uri = ExtractUri(@params) ?? "file:///untitled";

        var func = result.Functions.FirstOrDefault(f => f.Line <= line && f.EndLine >= line);
        var name = func?.Name;
        if (string.IsNullOrEmpty(name)) return new();

        var references = new List<Location>();
        foreach (var f in result.Functions)
        {
            foreach (var call in f.Calls)
            {
                if (call.Target == name)
                {
                    references.Add(new Location
                    {
                        Uri = uri,
                        Range = new Range
                        {
                            Start = new Position { Line = call.Line - 1, Character = call.Column - 1 },
                            End = new Position { Line = call.Line - 1, Character = call.Column + name.Length },
                        },
                    });
                }
            }
        }

        return references;
    }

    private async Task<List<DocumentSymbol>?> HandleDocumentSymbolAsync(JsonElement? @params)
    {
        var (code, language, _) = await ResolvePosition(@params).ConfigureAwait(false);
        if (code == null || language == null) return new();

        var parser = _parserRegistry.GetParser(language.Value);
        if (parser == null) return new();

        var result = await parser.ParseAsync(code).ConfigureAwait(false);
        var symbols = new List<DocumentSymbol>();

        foreach (var cls in result.Classes)
        {
            symbols.Add(new DocumentSymbol
            {
                Name = cls.Name,
                Kind = cls.Kind switch
                {
                    "interface" => SymbolKind.Interface, "enum" => SymbolKind.Enum,
                    "struct" => SymbolKind.Class, _ => SymbolKind.Class,
                },
                Range = new Range
                {
                    Start = new Position { Line = cls.Line - 1, Character = cls.Column - 1 },
                    End = new Position { Line = cls.EndLine - 1, Character = 0 },
                },
                SelectionRange = new Range
                {
                    Start = new Position { Line = cls.Line - 1, Character = cls.Column - 1 },
                    End = new Position { Line = cls.Line - 1, Character = cls.Column + cls.Name.Length },
                },
                Children = result.Functions
                    .Where(f => f.ParentClass == cls.Name)
                    .Select(f => new DocumentSymbol
                    {
                        Name = f.Name,
                        Kind = SymbolKind.Method,
                        Range = new Range
                        {
                            Start = new Position { Line = f.Line - 1, Character = f.Column - 1 },
                            End = new Position { Line = f.EndLine - 1, Character = 0 },
                        },
                        SelectionRange = new Range
                        {
                            Start = new Position { Line = f.Line - 1, Character = f.Column - 1 },
                            End = new Position { Line = f.Line - 1, Character = f.Column + f.Name.Length },
                        },
                    }).ToList(),
            });
        }

        foreach (var f in result.Functions.Where(f => string.IsNullOrEmpty(f.ParentClass)))
        {
            symbols.Add(new DocumentSymbol
            {
                Name = f.Name,
                Kind = SymbolKind.Function,
                Range = new Range
                {
                    Start = new Position { Line = f.Line - 1, Character = f.Column - 1 },
                    End = new Position { Line = f.EndLine - 1, Character = 0 },
                },
                SelectionRange = new Range
                {
                    Start = new Position { Line = f.Line - 1, Character = f.Column - 1 },
                    End = new Position { Line = f.Line - 1, Character = f.Column + f.Name.Length },
                },
            });
        }

        return symbols;
    }

    private async Task<List<Diagnostic>?> HandleDiagnosticAsync(JsonElement? @params)
    {
        var (code, language, _) = await ResolvePosition(@params).ConfigureAwait(false);
        if (code == null || language == null) return new();

        var parser = _parserRegistry.GetParser(language.Value);
        if (parser == null || !parser.SupportsDiagnostics) return new();

        var result = await parser.ParseAsync(code).ConfigureAwait(false);
        return result.Diagnostics.Select(d => new Diagnostic
        {
            Range = new Range
            {
                Start = new Position { Line = d.Line - 1, Character = d.Column - 1 },
                End = new Position { Line = d.EndLine - 1, Character = d.EndColumn - 1 },
            },
            Severity = d.Severity switch
            {
                AstDiagnosticSeverity.Error => DiagnosticSeverity.Error,
                AstDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
                AstDiagnosticSeverity.Information => DiagnosticSeverity.Information,
                _ => DiagnosticSeverity.Hint,
            },
            Code = d.Code ?? "",
            Source = d.Source ?? "ltai",
            Message = d.Message,
        }).ToList();
    }

    private async Task<List<CodeAction>> HandleCodeActionAsync(JsonElement? @params)
    {
        var diags = new List<Diagnostic>();
        if (@params.HasValue)
        {
            try
            {
                var ctx = @params.Value.GetProperty("context");
                var rawDiags = ctx.GetProperty("diagnostics");
                diags = JsonSerializer.Deserialize<List<Diagnostic>>(rawDiags.GetRawText()) ?? new();
            }
        catch (Exception ex) { _logger.LogWarning(ex, "LSP: Failed to handle didChange"); }
        }

        return diags
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => new CodeAction
            {
                Title = $"LTAI: Analyze '{d.Message}'",
                Kind = "quickfix",
                Diagnostics = new List<Diagnostic> { d },
            }).ToList();
    }

    private async Task<(string? code, CodeLanguage? language, Position pos)> ResolvePosition(
        JsonElement? @params)
    {
        if (!@params.HasValue) return (null, null, new());

        try
        {
            var uri = @params.Value.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
            var pos = JsonSerializer.Deserialize<Position>(
                @params.Value.GetProperty("position").GetRawText()) ?? new Position();

            if (_openDocuments.TryGetValue(uri, out var openCode))
                return (openCode, DetectLanguage(uri), pos);

            if (uri.StartsWith("file:///"))
            {
                string path;
                if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                    path = parsed.LocalPath;
                else
                    path = uri["file:///".Length..];

                if (!File.Exists(path)) return (null, null, pos);
                return (await File.ReadAllTextAsync(path), DetectLanguage(uri), pos);
            }

            return (null, null, pos);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LSP: Failed to resolve position from params");
            return (null, null, new());
        }
    }

    private static string? ExtractUri(JsonElement? @params)
    {
        try
        {
            return @params?.GetProperty("textDocument").GetProperty("uri").GetString();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex, "LSP: Failed to extract URI from params"); return null; }
    }

    private static CodeLanguage DetectLanguage(string uri)
    {
        var ext = Path.GetExtension(uri).ToLowerInvariant();
        return ext switch
        {
            ".cs" or ".csx" => CodeLanguage.CSharp,
            ".py" or ".pyw" => CodeLanguage.Python,
            ".js" or ".jsx" or ".mjs" => CodeLanguage.JavaScript,
            ".ts" or ".tsx" => CodeLanguage.TypeScript,
            ".go" => CodeLanguage.Go,
            ".rs" => CodeLanguage.Rust,
            ".java" or ".kt" => CodeLanguage.Java,
            _ => CodeLanguage.CSharp,
        };
    }
}
