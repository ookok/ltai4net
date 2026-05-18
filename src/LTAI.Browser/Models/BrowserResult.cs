using System.Text.Json.Serialization;

namespace LTAI.Browser.Models;

public sealed class BrowserResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("items")]
    public List<Dictionary<string, object?>> Items { get; init; } = new();

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("elapsed_ms")]
    public double ElapsedMs { get; init; }

    [JsonPropertyName("iterations")]
    public int Iterations { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static BrowserResult Ok(string url, string? title, List<Dictionary<string, object?>> items, string method, double elapsed, int iterations = 0) =>
        new()
        {
            Success = true,
            Url = url,
            Title = title,
            Items = items,
            Count = items.Count,
            Method = method,
            ElapsedMs = elapsed,
            Iterations = iterations
        };

    public static BrowserResult Fail(string url, string error, double elapsed) =>
        new()
        {
            Success = false,
            Url = url,
            Error = error,
            ElapsedMs = elapsed
        };
}

public sealed class ScreenshotResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("base64")]
    public string? Base64 { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class PageState
{
    public List<PageInput> Inputs { get; set; } = new();
    public List<PageClickable> Clickables { get; set; } = new();
    public string Text { get; set; } = string.Empty;
}

public sealed class PageInput
{
    public string Selector { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Placeholder { get; init; }
    public bool Visible { get; init; }
}

public sealed class PageClickable
{
    public string Selector { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public bool Visible { get; init; }
}

public sealed class BrowserSession
{
    public string SessionId { get; init; } = string.Empty;
    public string? CurrentUrl { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int PageViews { get; set; }
}
