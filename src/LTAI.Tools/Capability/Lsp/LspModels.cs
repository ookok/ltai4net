using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Tools.Lsp;

public sealed class LspMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Id { get; set; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LspError? Error { get; set; }

    public bool IsRequest => Id != null && Method != null;
    public bool IsResponse => Id != null && Method == null;
    public bool IsNotification => Id == null && Method != null;

    public static LspMessage? FromJson(string json) =>
        JsonSerializer.Deserialize<LspMessage>(json);

    public string ToJson() => JsonSerializer.Serialize(this,
        new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
}

public sealed class LspError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public static class LspMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string TextDocumentDidOpen = "textDocument/didOpen";
    public const string TextDocumentDidChange = "textDocument/didChange";
    public const string TextDocumentDidClose = "textDocument/didClose";
    public const string TextDocumentHover = "textDocument/hover";
    public const string TextDocumentCompletion = "textDocument/completion";
    public const string TextDocumentDefinition = "textDocument/definition";
    public const string TextDocumentReferences = "textDocument/references";
    public const string TextDocumentDocumentSymbol = "textDocument/documentSymbol";
    public const string TextDocumentDiagnostic = "textDocument/diagnostic";
    public const string TextDocumentCodeAction = "textDocument/codeAction";
    public const string WorkspaceSymbol = "workspace/symbol";
}

public sealed class InitializeParams
{
    [JsonPropertyName("processId")]
    public int? ProcessId { get; set; }
    [JsonPropertyName("rootUri")]
    public string? RootUri { get; set; }
    [JsonPropertyName("capabilities")]
    public ClientCapabilities? Capabilities { get; set; }
}

public sealed class ClientCapabilities
{
    [JsonPropertyName("textDocument")]
    public TextDocumentClientCapabilities? TextDocument { get; set; }
}

public sealed class TextDocumentClientCapabilities
{
    [JsonPropertyName("hover")]
    public DynamicRegistrationCapability? Hover { get; set; }
    [JsonPropertyName("completion")]
    public CompletionClientCapabilities? Completion { get; set; }
    [JsonPropertyName("definition")]
    public DynamicRegistrationCapability? Definition { get; set; }
}

public sealed class DynamicRegistrationCapability
{
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; set; }
}

public sealed class CompletionClientCapabilities
{
    [JsonPropertyName("dynamicRegistration")]
    public bool DynamicRegistration { get; set; }
    [JsonPropertyName("completionItem")]
    public CompletionItemCapabilities? CompletionItem { get; set; }
}

public sealed class CompletionItemCapabilities
{
    [JsonPropertyName("snippetSupport")]
    public bool SnippetSupport { get; set; }
}

public sealed class InitializeResult
{
    [JsonPropertyName("capabilities")]
    public ServerCapabilities Capabilities { get; set; } = new();
    [JsonPropertyName("serverInfo")]
    public ServerInfo? ServerInfo { get; set; }
}

public sealed class ServerCapabilities
{
    [JsonPropertyName("textDocumentSync")]
    public int TextDocumentSync { get; set; } = 1;
    [JsonPropertyName("hoverProvider")]
    public bool HoverProvider { get; set; } = true;
    [JsonPropertyName("completionProvider")]
    public CompletionOptions? CompletionProvider { get; set; }
    [JsonPropertyName("definitionProvider")]
    public bool DefinitionProvider { get; set; } = true;
    [JsonPropertyName("referencesProvider")]
    public bool ReferencesProvider { get; set; } = true;
    [JsonPropertyName("documentSymbolProvider")]
    public bool DocumentSymbolProvider { get; set; } = true;
    [JsonPropertyName("diagnosticProvider")]
    public DiagnosticOptions? DiagnosticProvider { get; set; }
    [JsonPropertyName("codeActionProvider")]
    public bool CodeActionProvider { get; set; } = true;
}

public sealed class CompletionOptions
{
    [JsonPropertyName("triggerCharacters")]
    public string[] TriggerCharacters { get; set; } = new[] { ".", ":", "\"" };
}

public sealed class DiagnosticOptions
{
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = "ltai";
    [JsonPropertyName("interFileDependencies")]
    public bool InterFileDependencies { get; set; }
    [JsonPropertyName("workspaceDiagnostics")]
    public bool WorkspaceDiagnostics { get; set; }
}

public sealed class ServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "LTAI";
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";
}

public sealed class TextDocumentIdentifier
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";
}

public sealed class TextDocumentItem
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";
    [JsonPropertyName("languageId")]
    public string LanguageId { get; set; } = "";
    [JsonPropertyName("version")]
    public int Version { get; set; }
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public sealed class Position
{
    [JsonPropertyName("line")]
    public int Line { get; set; }
    [JsonPropertyName("character")]
    public int Character { get; set; }
}

public sealed class Range
{
    [JsonPropertyName("start")]
    public Position Start { get; set; } = new();
    [JsonPropertyName("end")]
    public Position End { get; set; } = new();
}

public sealed class Location
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";
    [JsonPropertyName("range")]
    public Range Range { get; set; } = new();
}

public sealed class HoverResult
{
    [JsonPropertyName("contents")]
    public MarkupContent Contents { get; set; } = new();
    [JsonPropertyName("range")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Range? Range { get; set; }
}

public sealed class MarkupContent
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "markdown";
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public sealed class CompletionList
{
    [JsonPropertyName("isIncomplete")]
    public bool IsIncomplete { get; set; }
    [JsonPropertyName("items")]
    public List<CompletionItem> Items { get; set; } = new();
}

public sealed class CompletionItem
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
    [JsonPropertyName("kind")]
    public int Kind { get; set; }
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
    [JsonPropertyName("documentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Documentation { get; set; }
    [JsonPropertyName("insertText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InsertText { get; set; }
}

public sealed class DocumentSymbol
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("kind")]
    public int Kind { get; set; }
    [JsonPropertyName("range")]
    public Range Range { get; set; } = new();
    [JsonPropertyName("selectionRange")]
    public Range SelectionRange { get; set; } = new();
    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DocumentSymbol>? Children { get; set; }
}

public sealed class Diagnostic
{
    [JsonPropertyName("range")]
    public Range Range { get; set; } = new();
    [JsonPropertyName("severity")]
    public int Severity { get; set; }
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }
    [JsonPropertyName("source")]
    public string Source { get; set; } = "ltai";
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class CodeAction
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }
    [JsonPropertyName("diagnostics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Diagnostic>? Diagnostics { get; set; }
}

public static class CompletionItemKind
{
    public const int Text = 1;
    public const int Method = 2;
    public const int Function = 3;
    public const int Constructor = 4;
    public const int Field = 5;
    public const int Variable = 6;
    public const int Class = 7;
    public const int Interface = 8;
    public const int Module = 9;
    public const int Property = 10;
    public const int Keyword = 14;
    public const int Snippet = 15;
}

public static class SymbolKind
{
    public const int File = 1;
    public const int Module = 2;
    public const int Namespace = 3;
    public const int Package = 4;
    public const int Class = 5;
    public const int Method = 6;
    public const int Property = 7;
    public const int Field = 8;
    public const int Constructor = 9;
    public const int Enum = 10;
    public const int Interface = 11;
    public const int Function = 12;
    public const int Variable = 13;
    public const int Constant = 14;
    public const int String = 15;
    public const int Number = 16;
    public const int Boolean = 17;
    public const int Array = 18;
}

public static class DiagnosticSeverity
{
    public const int Error = 1;
    public const int Warning = 2;
    public const int Information = 3;
    public const int Hint = 4;
}
