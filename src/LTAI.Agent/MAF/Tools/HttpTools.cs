using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;

namespace LTAI.Agent.Tools;

[Description("HTTP client tools for making web requests to APIs and websites")]
public sealed class HttpTools
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "LTAI-HttpTool/1.0" } }
    };

    [Description("Perform an HTTP GET request to a URL and return the response body as text. Returns HTML or JSON depending on the URL.")]
    public static async Task<string> HttpGet(
        [Description("Full URL to GET")] string url,
        [Description("Optional custom headers as JSON object, e.g. {\"Authorization\":\"Bearer token\"}")] string? headers = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, headers);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await FormatResponse(response, cancellationToken).ConfigureAwait(false);
    }

    [Description("Perform an HTTP POST request with a JSON body and return the response.")]
    public static async Task<string> HttpPost(
        [Description("Full URL to POST to")] string url,
        [Description("JSON string body to send")] string body,
        [Description("Optional custom headers as JSON object")] string? headers = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddHeaders(request, headers);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await FormatResponse(response, cancellationToken).ConfigureAwait(false);
    }

    [Description("Download content from a URL and return base64-encoded data with content type. Useful for downloading images or files.")]
    public static async Task<string> HttpDownload(
        [Description("URL to download from")] string url,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var base64 = Convert.ToBase64String(bytes);
        var preview = base64.Length > 200 ? base64[..200] + "..." : base64;
        return JsonSerializer.Serialize(new
        {
            url,
            statusCode = (int)response.StatusCode,
            contentType,
            contentLength = bytes.Length,
            base64,
            preview
        });
    }

    [Description("Check the status of a URL (HEAD request). Returns HTTP status code and response headers.")]
    public static async Task<string> HttpCheckStatus(
        [Description("URL to check")] string url,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            url,
            statusCode = (int)response.StatusCode,
            reason = response.ReasonPhrase,
            headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value))
        });
    }

    private static void AddHeaders(HttpRequestMessage request, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            if (dict != null)
                foreach (var (k, v) in dict)
                    request.Headers.TryAddWithoutValidation(k, v);
        }
        catch { }
    }

    private static async Task<string> FormatResponse(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (text.Length > 20000) text = text[..20000] + $"\n... (truncated)";

        object? parsed = null;
        if (contentType.Contains("json"))
        {
            try { parsed = JsonSerializer.Deserialize<object>(text); } catch { }
        }

        return JsonSerializer.Serialize(new
        {
            url = response.RequestMessage?.RequestUri?.ToString(),
            statusCode = (int)response.StatusCode,
            contentType,
            contentLength = text.Length,
            text,
            json = parsed
        });
    }
}
