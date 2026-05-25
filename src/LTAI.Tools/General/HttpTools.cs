using System.ComponentModel;
using System.Net.Http.Json;

namespace LTAI.Tools.General;

public static class HttpTools
{
    private static readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    [Description("Fetches content from a URL")]
    public static async Task<string> FetchAsync(
        [Description("The URL to fetch")] string url,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return content.Length > 10000
                ? content[..10000] + $"\n...(truncated, total {content.Length} chars)"
                : content;
        }
        catch (Exception ex)
        {
            return $"HTTP fetch failed: {ex.Message}";
        }
    }

    [Description("Sends a POST request with JSON body")]
    public static async Task<string> PostJsonAsync(
        [Description("The URL to post to")] string url,
        [Description("JSON string to send as the request body")] string jsonBody,
        CancellationToken ct = default)
    {
        try
        {
            var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            var response = await _client.PostAsync(url, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            return $"HTTP POST failed: {ex.Message}";
        }
    }
}
