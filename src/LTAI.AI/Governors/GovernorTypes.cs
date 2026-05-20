namespace LTAI.AI.Governors;

public sealed record GovernorInput
{
    public string Query { get; init; } = "";
    public string TraceId { get; init; } = "";
    public string Label { get; init; } = "deep";
    public float Complexity { get; init; }
    public string? Emotion { get; init; }
    public string? Context { get; init; }
    public string? Model { get; init; }
    public float Temperature { get; init; } = 0.3f;
    public string? Response { get; init; }

    public static GovernorInput Create(string query, string traceId = "")
        => new() { Query = query, TraceId = string.IsNullOrEmpty(traceId) ? Guid.NewGuid().ToString("N") : traceId };

    public GovernorInput WithClassification(string label, float complexity, string? emotion = null)
        => this with { Label = label, Complexity = complexity, Emotion = emotion };

    public GovernorInput WithContext(string context)
        => this with { Context = context };

    public GovernorInput WithRoute(string model, float temperature)
        => this with { Model = model, Temperature = temperature };

    public GovernorInput WithResponse(string response)
        => this with { Response = response };

    public string FullPrompt => string.IsNullOrEmpty(Context) ? Query : $"Context:\n{Context}\n\nQuery: {Query}";
}

public sealed record GovernorOutput
{
    public string Response { get; init; } = "";
    public string TraceId { get; init; } = "";
    public bool IsReflex { get; init; }
    public string? ReflexCommand { get; init; }
    public bool IsBlocked { get; init; }
    public string? BlockReason { get; init; }

    public static GovernorOutput Success(string response, string traceId) => new() { Response = response, TraceId = traceId };
    public static GovernorOutput Reflex(string command, string traceId) => new() { IsReflex = true, ReflexCommand = command, TraceId = traceId };
    public static GovernorOutput Blocked(string reason) => new() { IsBlocked = true, BlockReason = reason };
}

public sealed record StreamChunk
{
    public string Text { get; init; } = "";
    public bool IsFinal { get; init; }
    public string? ModelUsed { get; init; }
}
