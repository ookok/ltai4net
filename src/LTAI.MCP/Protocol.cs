using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.MCP;

public sealed class JsonRpcMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }

    public bool IsRequest => Method != null;
    public bool IsResponse => Result != null || Error != null;
    public bool IsNotification => Method != null && Id == null;
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; set; }
}

public sealed class InitializeRequest
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public JsonElement? Capabilities { get; set; }

    [JsonPropertyName("clientInfo")]
    public JsonElement? ClientInfo { get; set; }
}

public sealed class InitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [JsonPropertyName("serverInfo")]
    public ServerInfo Info { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public ServerCapabilities Capabilities { get; set; } = new();
}

public sealed class ServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "LTAI";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "7.0.0";
}

public sealed class ServerCapabilities
{
    [JsonPropertyName("tools")]
    public ToolCapability? Tools { get; set; }

    [JsonPropertyName("resources")]
    public ResourceCapability? Resources { get; set; }

    [JsonPropertyName("prompts")]
    public PromptCapability? Prompts { get; set; }
}

public sealed class ToolCapability { [JsonPropertyName("listChanged")] public bool ListChanged { get; set; } }
public sealed class ResourceCapability { [JsonPropertyName("subscribe")] public bool Subscribe { get; set; } }
public sealed class PromptCapability { [JsonPropertyName("listChanged")] public bool ListChanged { get; set; } }

public sealed class MCPTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; set; }
}

public sealed class ToolCallRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Arguments { get; set; }
}

public sealed class ToolCallResult
{
    [JsonPropertyName("content")]
    public List<ContentItem> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsError { get; set; }
}

public sealed class ContentItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class MCPResource
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "text/plain";
}

public sealed class ReadResourceRequest
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";
}

public sealed class ReadResourceResult
{
    [JsonPropertyName("contents")]
    public List<ResourceContent> Contents { get; set; } = new();
}

public sealed class ResourceContent
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "text/plain";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}
