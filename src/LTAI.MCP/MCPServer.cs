using System.Text.Json;
using LTAI.Core.Messaging;
using LTAI.DNA.Safety;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.MCP;

public sealed class MCPServer
{
    private readonly AIToolRegistry _tools;
    private readonly UnifiedSafetyGate? _safety;
    private readonly ILogger<MCPServer> _logger;
    private readonly Dictionary<string, MCPResource> _resources = new();
    private string _serverName = "LTAI v7.0";

    public MCPServer(AIToolRegistry tools, UnifiedSafetyGate? safety, ILogger<MCPServer> logger)
    {
        _tools = tools;
        _safety = safety;
        _logger = logger;
        RegisterBuiltInResources();
    }

    public async Task<string> HandleMessageAsync(string json, CancellationToken cancellationToken = default)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<JsonRpcMessage>(json);
            if (msg == null) return ErrorResponse(null, -32700, "Parse error");

            if (msg.IsResponse) return "";

            return msg.Method switch
            {
                "initialize" => await HandleInitializeAsync(msg, cancellationToken),
                "tools/list" => await HandleListToolsAsync(msg),
                "tools/call" => await HandleCallToolAsync(msg, cancellationToken),
                "resources/list" => await HandleListResourcesAsync(msg),
                "resources/read" => await HandleReadResourceAsync(msg),
                "notifications/initialized" => "",
                _ => ErrorResponse(msg.Id, -32601, $"Method not found: {msg.Method}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP message handling failed");
            return ErrorResponse(null, -32603, ex.Message);
        }
    }

    private async Task<string> HandleInitializeAsync(JsonRpcMessage msg, CancellationToken ct)
    {
        var result = new InitializeResult
        {
            Info = new ServerInfo { Name = _serverName, Version = "7.0.0" },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolCapability { ListChanged = false },
                Resources = new ResourceCapability { Subscribe = false },
                Prompts = new PromptCapability { ListChanged = false }
            }
        };

        _logger.LogInformation("MCP initialized by client");
        return ResultResponse(msg.Id, result);
    }

    private async Task<string> HandleListToolsAsync(JsonRpcMessage msg)
    {
        var tools = _tools.GetTools().ToList();
        var mcpTools = new List<MCPTool>();

        foreach (var tool in tools)
        {
            var name = (tool as AIFunction)?.Name ?? tool.GetType().Name;
            var desc = (tool as AIFunction)?.Description ?? $"LTAI tool: {name}";
            var schemaJson = JsonSerializer.Serialize(new { type = "object", properties = new { query = new { type = "string", description = $"Input for {name} tool" } } });

            mcpTools.Add(new MCPTool
            {
                Name = name,
                Description = desc,
                InputSchema = JsonDocument.Parse(schemaJson).RootElement
            });
        }

        _logger.LogDebug("Listed {Count} tools", mcpTools.Count);
        return ResultResponse(msg.Id, new { tools = mcpTools });
    }

    private async Task<string> HandleCallToolAsync(JsonRpcMessage msg, CancellationToken ct)
    {
        var request = msg.Params?.Deserialize<ToolCallRequest>();
        if (request == null || string.IsNullOrEmpty(request.Name))
            return ErrorResponse(msg.Id, -32602, "Invalid params: name required");

        try
        {
            if (_safety != null && !_safety.EvaluateToolCall(request.Name, JsonSerializer.Serialize(request.Arguments)))
            {
                _logger.LogWarning("MCPServer: SafetyGate blocked tool call {Tool}", request.Name);
                return ErrorResponse(msg.Id, -32000, $"[Safety] Tool call blocked: {request.Name}");
            }

            var args = new Dictionary<string, object?>();
            if (request.Arguments != null)
            {
                foreach (var prop in request.Arguments.Value.EnumerateObject())
                    args[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.ToString()
                    };
            }

            var result = await _tools.InvokeAsync(request.Name, args, ct);
            var resultJson = JsonSerializer.Serialize(result);

            return ResultResponse(msg.Id, new ToolCallResult
            {
                Content = new List<ContentItem>
                {
                    new() { Type = "text", Text = resultJson }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool call failed: {Tool}", request.Name);
            return ResultResponse(msg.Id, new ToolCallResult
            {
                Content = new List<ContentItem>
                {
                    new() { Type = "text", Text = $"Error: {ex.Message}" }
                },
                IsError = true
            });
        }
    }

    private Task<string> HandleListResourcesAsync(JsonRpcMessage msg)
    {
        return Task.FromResult(ResultResponse(msg.Id, new { resources = _resources.Values.ToList() }));
    }

    private async Task<string> HandleReadResourceAsync(JsonRpcMessage msg)
    {
        var request = msg.Params?.Deserialize<ReadResourceRequest>();
        if (request == null || string.IsNullOrEmpty(request.Uri))
            return ErrorResponse(msg.Id, -32602, "Invalid params: uri required");

        if (!_resources.TryGetValue(request.Uri, out var resource))
            return ErrorResponse(msg.Id, -32002, $"Resource not found: {request.Uri}");

        var content = await ReadResourceContentAsync(request.Uri);
        return ResultResponse(msg.Id, new ReadResourceResult
        {
            Contents = new List<ResourceContent>
            {
                new() { Uri = request.Uri, MimeType = resource.MimeType, Text = content }
            }
        });
    }

    private void RegisterBuiltInResources()
    {
        _resources["ltai://status"] = new MCPResource
        {
            Uri = "ltai://status",
            Name = "LTAI System Status",
            Description = "Current system status, mode, and health information",
            MimeType = "application/json"
        };
        _resources["ltai://tools"] = new MCPResource
        {
            Uri = "ltai://tools",
            Name = "Available Tools",
            Description = "List of all registered capability tools",
            MimeType = "application/json"
        };
    }

    private Task<string> ReadResourceContentAsync(string uri) => uri switch
    {
        "ltai://status" => Task.FromResult(JsonSerializer.Serialize(new
        {
            name = _serverName,
            version = "5.5.0",
            tools = _tools.ListTools().Count(),
            timestamp = DateTime.UtcNow.ToString("O")
        })),
        "ltai://tools" => Task.FromResult(JsonSerializer.Serialize(new
        {
            tools = _tools.ListTools()
        })),
        _ => Task.FromResult("{}")
    };

    private static string ResultResponse(object? id, object result)
    {
        var element = JsonSerializer.SerializeToElement(result);
        return JsonSerializer.Serialize(new JsonRpcMessage
        {
            Id = id,
            Result = element
        });
    }

    private static string ErrorResponse(object? id, int code, string message)
    {
        return JsonSerializer.Serialize(new JsonRpcMessage
        {
            Id = id,
            Error = new JsonRpcError { Code = code, Message = message }
        });
    }
}
