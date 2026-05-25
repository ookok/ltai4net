using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.MCP;

public sealed class MCPToolInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public JsonElement? InputSchema { get; init; }
}

public sealed class MCPResourceInfo
{
    public string Uri { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string MimeType { get; init; } = "";
}

public sealed class MCPCallResult
{
    public bool Success { get; init; }
    public string Content { get; init; } = "";
    public string Error { get; init; } = "";
}

public sealed class MCPClient
{
    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly ILogger<MCPClient> _logger;
    private int _requestId;

    public string ServerName { get; private set; } = "";
    public string ServerVersion { get; private set; } = "";

    public MCPClient(string serverUrl, ILogger<MCPClient>? logger = null)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _logger = logger ?? NullLogger<MCPClient>.Instance;
    }

    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await SendRequestAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "LTAI", version = "0.51.0" }
            }, ct);

            if (result.TryGetProperty("serverInfo", out var info))
            {
                ServerName = info.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                ServerVersion = info.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            }

            await SendNotificationAsync("notifications/initialized", null, ct);
            _logger.LogInformation("MCP client connected to {Server} v{Version}", ServerName, ServerVersion);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP client initialization failed for {Url}", _serverUrl);
            return false;
        }
    }

    public async Task<List<MCPToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        var result = await SendRequestAsync("tools/list", null, ct);
        var tools = new List<MCPToolInfo>();

        if (result.TryGetProperty("tools", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                tools.Add(new MCPToolInfo
                {
                    Name = item.GetProperty("name").GetString() ?? "",
                    Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    InputSchema = item.TryGetProperty("inputSchema", out var s) ? s : null
                });
            }
        }

        return tools;
    }

    public async Task<List<MCPResourceInfo>> ListResourcesAsync(CancellationToken ct = default)
    {
        var result = await SendRequestAsync("resources/list", null, ct);
        var resources = new List<MCPResourceInfo>();

        if (result.TryGetProperty("resources", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                resources.Add(new MCPResourceInfo
                {
                    Uri = item.GetProperty("uri").GetString() ?? "",
                    Name = item.GetProperty("name").GetString() ?? "",
                    Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    MimeType = item.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : ""
                });
            }
        }

        return resources;
    }

    public async Task<MCPCallResult> CallToolAsync(string toolName, Dictionary<string, object?>? arguments = null, CancellationToken ct = default)
    {
        try
        {
            var result = await SendRequestAsync("tools/call", new
            {
                name = toolName,
                arguments = arguments ?? new Dictionary<string, object?>()
            }, ct);

            var content = new StringBuilder();
            if (result.TryGetProperty("content", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text))
                        content.AppendLine(text.GetString());
                    else if (item.TryGetProperty("type", out var type) && type.GetString() == "text")
                        content.AppendLine(item.TryGetProperty("text", out var t) ? t.GetString() : "");
                }
            }

            return new MCPCallResult { Success = true, Content = content.ToString().TrimEnd() };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP tool call failed: {Tool}", toolName);
            return new MCPCallResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<string?> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        try
        {
            var result = await SendRequestAsync("resources/read", new { uri }, ct);
            if (result.TryGetProperty("contents", out var arr) && arr.GetArrayLength() > 0)
            {
                var content = arr[0];
                return content.TryGetProperty("text", out var text) ? text.GetString()
                    : content.TryGetProperty("blob", out var blob) ? blob.GetString()
                    : null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP resource read failed: {Uri}", uri);
        }
        return null;
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId);
        var request = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(_serverUrl, content, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"MCP server returned {(int)response.StatusCode}: {errorBody[..Math.Min(200, errorBody.Length)]}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err))
        {
            var errMsg = err.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown MCP error";
            throw new InvalidOperationException($"MCP error: {errMsg}");
        }

        return root.TryGetProperty("result", out var res) ? res : default;
    }

    private async Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        var notification = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        };

        var json = JsonSerializer.Serialize(notification);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        await _http.PostAsync(_serverUrl, content, ct).ConfigureAwait(false);
    }
}
