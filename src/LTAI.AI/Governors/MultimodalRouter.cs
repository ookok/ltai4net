using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public enum MultimodalType { Text, Image, Audio, Video, Document }

public sealed record MultimodalInput
{
    public MultimodalType Type { get; init; }
    public string Content { get; init; } = "";
    public byte[]? BinaryData { get; init; }
    public string? MimeType { get; init; }
    public float Confidence { get; init; } = 1.0f;
}

public sealed record MultimodalRouteResult
{
    public bool CanHandleLocally { get; init; }
    public string Response { get; init; } = "";
    public string RequiredModel { get; init; } = "";
    public float Confidence { get; init; }
    public string PreprocessedText { get; init; } = "";
}

public sealed class MultimodalRouter
{
    private readonly ILogger<MultimodalRouter> _logger;
    private readonly Dictionary<string, Func<MultimodalInput, Task<string>>> _localProcessors = new();

    public MultimodalRouter(ILogger<MultimodalRouter>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MultimodalRouter>.Instance;
        RegisterDefaultProcessors();
    }

    public void RegisterProcessor(string mimeType, Func<MultimodalInput, Task<string>> processor)
    {
        _localProcessors[mimeType] = processor;
        _logger.LogInformation("Registered multimodal processor: {MimeType}", mimeType);
    }

    public async Task<MultimodalRouteResult> RouteAsync(MultimodalInput input, CancellationToken ct = default)
    {
        if (input.Type == MultimodalType.Text)
        {
            return new MultimodalRouteResult
            {
                CanHandleLocally = false,
                RequiredModel = "l1",
                PreprocessedText = input.Content,
                Confidence = 1.0f
            };
        }

        var mimeType = input.MimeType ?? GetDefaultMimeType(input.Type);

        if (_localProcessors.TryGetValue(mimeType, out var processor))
        {
            try
            {
                var extractedText = await processor(input);
                return new MultimodalRouteResult
                {
                    CanHandleLocally = true,
                    Response = extractedText,
                    PreprocessedText = extractedText,
                    Confidence = input.Confidence
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local processor failed for {MimeType}", mimeType);
            }
        }

        return input.Type switch
        {
            MultimodalType.Image => new MultimodalRouteResult
            {
                CanHandleLocally = false,
                RequiredModel = "l2",
                Confidence = 0.8f,
                PreprocessedText = "[Image content requires vision model]"
            },
            MultimodalType.Audio => new MultimodalRouteResult
            {
                CanHandleLocally = false,
                RequiredModel = "l2",
                Confidence = 0.7f,
                PreprocessedText = "[Audio content requires speech recognition]"
            },
            MultimodalType.Video => new MultimodalRouteResult
            {
                CanHandleLocally = false,
                RequiredModel = "l2",
                Confidence = 0.6f,
                PreprocessedText = "[Video content requires multimodal model]"
            },
            MultimodalType.Document => new MultimodalRouteResult
            {
                CanHandleLocally = false,
                RequiredModel = "l1",
                Confidence = 0.9f,
                PreprocessedText = "[Document content requires parsing]"
            },
            _ => new MultimodalRouteResult { CanHandleLocally = false, Confidence = 0.5f }
        };
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["registered_processors"] = _localProcessors.Count,
            ["supported_mime_types"] = _localProcessors.Keys.ToList()
        };
    }

    private void RegisterDefaultProcessors()
    {
        _localProcessors["text/plain"] = input => Task.FromResult(input.Content);
        _localProcessors["text/csv"] = input => Task.FromResult($"CSV data with {input.Content.Split('\n').Length} rows");
        _localProcessors["application/json"] = input => Task.FromResult($"JSON data: {input.Content[..Math.Min(200, input.Content.Length)]}");
    }

    private static string GetDefaultMimeType(MultimodalType type)
    {
        return type switch
        {
            MultimodalType.Text => "text/plain",
            MultimodalType.Image => "image/jpeg",
            MultimodalType.Audio => "audio/mpeg",
            MultimodalType.Video => "video/mp4",
            MultimodalType.Document => "application/pdf",
            _ => "application/octet-stream"
        };
    }
}
