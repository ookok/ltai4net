namespace LTAI.Core.Messaging;

public sealed class ClassifyQuery
{
    public string Query { get; init; } = "";
    public string TraceId { get; init; } = "";
}

public sealed class ClassificationResult
{
    public string Query { get; init; } = "";
    public string Label { get; init; } = "deep";
    public float Complexity { get; init; }
    public string? Emotion { get; init; }
    public bool IsReflex { get; init; }
    public string? ReflexCommand { get; init; }
    public string TraceId { get; init; } = "";
}

public sealed class PreloadContext
{
    public string Query { get; init; } = "";
    public string Label { get; init; } = "deep";
    public string TraceId { get; init; } = "";
}

public sealed class ContextResult
{
    public string? Context { get; init; }
    public int TurnCount { get; init; }
    public string TraceId { get; init; } = "";
}

public sealed class SelectProvider
{
    public string Query { get; init; } = "";
    public string Label { get; init; } = "deep";
    public string TraceId { get; init; } = "";
}

public sealed class ProviderResult
{
    public string Model { get; init; } = "";
    public float Temperature { get; init; } = 0.3f;
    public string TraceId { get; init; } = "";
}

public sealed class ReviewOutput
{
    public string Response { get; init; } = "";
    public string TraceId { get; init; } = "";
}

public sealed class OutputReview
{
    public string Response { get; init; } = "";
    public bool Passed { get; init; } = true;
    public string? Warning { get; init; }
    public string TraceId { get; init; } = "";
}

public sealed class StartTrace
{
    public string TraceId { get; init; } = "";
}

public sealed class InvokeTool
{
    public string ToolName { get; init; } = "";
    public Dictionary<string, object?> Parameters { get; init; } = new();
    public string TraceId { get; init; } = "";
}

public sealed class ToolResult
{
    public object? Result { get; init; }
    public string? Error { get; init; }
    public string ToolName { get; init; } = "";
    public string TraceId { get; init; } = "";
}
