using LTAI.Core.Messaging;
 using System.Text.Json;

namespace LTAI.Tools.Tools;

public static class McpToolAdapter
{
    private static readonly HttpClient _discoverHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly HttpClient _callHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    public static async Task<object?> DiscoverAsync(Dictionary<string, object?> args)
    {
        var serverUrl = Arg(args, "server_url");
        if (string.IsNullOrWhiteSpace(serverUrl)) return JsonToolResult.Success(new { error = "server_url parameter is required" });
        try
        {
            using var http = _discoverHttp;
            var content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"LTAI","version":"5.5"}}}""", System.Text.Encoding.UTF8, "application/json");
            var initResult = await http.PostAsync($"{serverUrl.TrimEnd('/')}/message", content);
            var toolContent = new StringContent("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""", System.Text.Encoding.UTF8, "application/json");
            var toolResult = await http.PostAsync($"{serverUrl.TrimEnd('/')}/message", toolContent);
            var toolsJson = await toolResult.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(toolsJson);
            var tools = new List<object>();
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("tools", out var toolsArr))
            {
                foreach (var t in toolsArr.EnumerateArray())
                    tools.Add(new { name = t.GetProperty("name").GetString(), description = t.TryGetProperty("description", out var d) ? d.GetString() : "" });
            }
            return JsonToolResult.Success(new { server_url = serverUrl, connected = initResult.IsSuccessStatusCode, tools });
        }
        catch (Exception ex) { return JsonToolResult.Success(new { error = $"MCP discovery failed: {ex.Message}" }); }
    }

    public static async Task<object?> CallAsync(Dictionary<string, object?> args)
    {
        var serverUrl = Arg(args, "server_url");
        var toolName = Arg(args, "tool_name");
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(toolName)) return JsonToolResult.Success(new { error = "server_url and tool_name are required" });
        try
        {
            var toolArgs = Arg(args, "arguments", "{}");
            var http = _callHttp;
            var callPayload = $"{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{{\"name\":\"{toolName}\",\"arguments\":{toolArgs}}}}}";
            var callContent = new StringContent(callPayload, System.Text.Encoding.UTF8, "application/json");
            var callResult = await http.PostAsync($"{serverUrl.TrimEnd('/')}/message", callContent);
            var json = await callResult.Content.ReadAsStringAsync();
            return JsonToolResult.Success(new { tool = toolName, status = callResult.IsSuccessStatusCode ? "called" : "failed", raw_response = json[..Math.Min(2000, json.Length)] });
        }
        catch (Exception ex) { return JsonToolResult.Success(new { error = $"MCP call failed: {ex.Message}" }); }
    }

    public static object Export(ToolDef[] allTools)
    {
        var tools = allTools.Select(t => new { name = t.Name, description = t.Description, category = t.Category, has_handler = t.Handler != null });
        return JsonToolResult.Success(new { protocol = "2024-11-05", server_name = "LTAI", version = "7.0.0", tools, total = allTools.Length });
    }

    private static string Arg(Dictionary<string, object?>? args, string key, string def = "")
        => args?.TryGetValue(key, out var v) == true ? v?.ToString() ?? def : def;
}
