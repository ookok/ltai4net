namespace LTAI.Capability.CodeEngine;

public sealed class CodeAnalysisResult
{
    public CodeLanguage Language { get; init; }
    public string LanguageName { get; init; } = "";
    public int TotalLines { get; init; }
    public int CodeLines { get; init; }
    public int CommentLines { get; init; }
    public int BlankLines { get; init; }
    public List<CodeFunction> Functions { get; set; } = new();
    public List<CodeClass> Classes { get; set; } = new();
    public List<CodeImport> Imports { get; set; } = new();
    public double Complexity { get; set; }
}

public sealed class CodeFunction
{
    public string Name { get; init; } = "";
    public int Line { get; init; }
    public int ParameterCount { get; init; }
}

public sealed class CodeClass
{
    public string Name { get; init; } = "";
    public int Line { get; init; }
    public int MethodCount { get; init; }
}

public sealed class CodeImport
{
    public string Module { get; init; } = "";
    public int Line { get; init; }
}

public sealed class CodeQualityReport
{
    public int TotalLines { get; set; }
    public int LongLines { get; set; }
    public bool InconsistentIndent { get; set; }
    public int TodoCount { get; set; }
}
