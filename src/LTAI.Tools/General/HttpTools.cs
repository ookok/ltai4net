using System.Net.Http.Json;
using Microsoft.Agents.AI;

namespace LTAI.Tools.General;

public static class HttpTools
{
    private static readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    [AIFunction("Fetches content from a URL")]
    public static async Task<string> FetchAsync(
        [AIFunctionParameter("The URL to fetch", Required = true)]
        string url,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);

            return content.Length > 10000
                ? content[..10000] + $"\n...(truncated, total {content.Length} chars)"
                : content;
        }
        catch (Exception ex)
        {
            return $"HTTP fetch failed: {ex.Message}";
        }
    }

    [AIFunction("Sends a POST request with JSON body")]
    public static async Task<string> PostJsonAsync(
        [AIFunctionParameter("The URL to post to", Required = true)]
        string url,
        [AIFunctionParameter("JSON string to send as the request body", Required = true)]
        string jsonBody,
        CancellationToken ct = default)
    {
        try
        {
            var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            var response = await _client.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadAsStringAsync(ct);
            return result;
        }
        catch (Exception ex)
        {
            return $"HTTP POST failed: {ex.Message}";
        }
    }
}
