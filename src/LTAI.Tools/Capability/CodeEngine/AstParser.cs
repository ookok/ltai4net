namespace LTAI.Tools.CodeEngine;

public interface ICodeParser
{
    CodeLanguage Language { get; }
    Task<CodeParseResult> ParseAsync(string sourceCode, string? filePath = null, CancellationToken cancellationToken = default);
    bool SupportsDiagnostics { get; }
}

public sealed class CodeParseResult
{
    public CodeLanguage Language { get; init; }
    public string FilePath { get; init; } = "";
    public List<AstFunction> Functions { get; set; } = new();
    public List<AstClass> Classes { get; set; } = new();
    public List<AstImport> Imports { get; set; } = new();
    public List<AstVariable> Variables { get; set; } = new();
    public List<AstDiagnostic> Diagnostics { get; set; } = new();
    public AstSyntaxNode? RootNode { get; set; }
    public int TotalLines { get; init; }
    public int CodeLines { get; init; }
    public int CommentLines { get; init; }
    public int BlankLines { get; init; }
    public double CyclomaticComplexity { get; init; }
    public DateTime ParsedAt { get; init; } = DateTime.UtcNow;
}

public sealed class AstFunction
{
    public string Name { get; init; } = "";
    public int Line { get; init; }
    public int EndLine { get; init; }
    public int Column { get; init; }
    public string ReturnType { get; init; } = "";
    public List<string> Parameters { get; init; } = new();
    public List<string> Modifiers { get; init; } = new();
    public string? ParentClass { get; init; }
    public string? Documentation { get; init; }
    public List<AstVariable> LocalVariables { get; init; } = new();
    public List<AstFunctionCall> Calls { get; init; } = new();
    public double Complexity { get; init; }
}

public sealed class AstClass
{
    public string Name { get; init; } = "";
    public int Line { get; init; }
    public int EndLine { get; init; }
    public int Column { get; init; }
    public string Kind { get; init; } = "class";
    public List<string> Modifiers { get; init; } = new();
    public List<string> BaseTypes { get; init; } = new();
    public List<string> Methods { get; init; } = new();
    public List<string> Properties { get; init; } = new();
    public List<string> Fields { get; init; } = new();
    public string? Documentation { get; init; }
    public int MethodCount { get; init; }
    public int PropertyCount { get; init; }
    public int FieldCount { get; init; }
}

public sealed class AstImport
{
    public string Module { get; init; } = "";
    public string? Alias { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public string ImportKind { get; init; } = "import";
    public List<string>? ImportedSymbols { get; init; }
}

public sealed class AstVariable
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public int Line { get; init; }
    public int Column { get; init; }
    public string Scope { get; init; } = "local";
    public bool IsParameter { get; init; }
    public bool IsMutable { get; init; } = true;
}

public sealed class AstFunctionCall
{
    public string Target { get; init; } = "";
    public string? Object { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public List<string> Arguments { get; init; } = new();
}

public sealed class AstDiagnostic
{
    public int Line { get; init; }
    public int Column { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public string Message { get; init; } = "";
    public AstDiagnosticSeverity Severity { get; init; }
    public string? Code { get; init; }
    public string? Source { get; init; }
}

public enum AstDiagnosticSeverity
{
    Hint = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}

public sealed class AstSyntaxNode
{
    public string Kind { get; init; } = "";
    public int StartLine { get; init; }
    public int StartColumn { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public List<AstSyntaxNode> Children { get; init; } = new();
    public string? Text { get; init; }
}
