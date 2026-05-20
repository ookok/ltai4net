namespace LTAI.Core.Serialization;

public sealed class ChatRequestProto
{
    public string Model { get; set; } = "";
    public List<MessageProto> Messages { get; set; } = new();
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 4096;
    public bool Stream { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public sealed class MessageProto
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string? Name { get; set; }
}

public sealed class ChatResponseProto
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public string Content { get; set; } = "";
    public double Cost { get; set; }
    public int TokensUsed { get; set; }
}

public sealed class TaskRequestProto
{
    public string TaskId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public int Priority { get; set; }
    public string SourceNode { get; set; } = "";
}

public sealed class TaskResponseProto
{
    public string TaskId { get; set; } = "";
    public string Status { get; set; } = "";
    public string Result { get; set; } = "";
    public string Error { get; set; } = "";
    public double Progress { get; set; }
}

public sealed class KnowledgeMessageProto
{
    public string DocId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public List<ChunkProto> Chunks { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public sealed class ChunkProto
{
    public string ChunkId { get; set; } = "";
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public string SectionPath { get; set; } = "";
}

public sealed class NodeStatusProto
{
    public string NodeId { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public int ActiveTasks { get; set; }
    public long UptimeSeconds { get; set; }
}

public static class ProtoServiceStubs
{
    private static readonly global::System.Text.Json.JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = global::System.Text.Json.JsonNamingPolicy.CamelCase
    };

    public static byte[] EncodeChatRequest(ChatRequestProto req) =>
        global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(req, Opts);

    public static ChatRequestProto DecodeChatRequest(byte[] data) =>
        global::System.Text.Json.JsonSerializer.Deserialize<ChatRequestProto>(data, Opts) ?? new();

    public static byte[] EncodeChatResponse(ChatResponseProto resp) =>
        global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(resp, Opts);

    public static ChatResponseProto DecodeChatResponse(byte[] data) =>
        global::System.Text.Json.JsonSerializer.Deserialize<ChatResponseProto>(data, Opts) ?? new();

    public static byte[] EncodeTaskRequest(TaskRequestProto req) =>
        global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(req, Opts);

    public static TaskRequestProto DecodeTaskRequest(byte[] data) =>
        global::System.Text.Json.JsonSerializer.Deserialize<TaskRequestProto>(data, Opts) ?? new();

    public static byte[] EncodeTaskResponse(TaskResponseProto resp) =>
        global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(resp, Opts);

    public static TaskResponseProto DecodeTaskResponse(byte[] data) =>
        global::System.Text.Json.JsonSerializer.Deserialize<TaskResponseProto>(data, Opts) ?? new();

    public static byte[] EncodeNodeStatus(NodeStatusProto status) =>
        global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(status, Opts);

    public static NodeStatusProto DecodeNodeStatus(byte[] data) =>
        global::System.Text.Json.JsonSerializer.Deserialize<NodeStatusProto>(data, Opts) ?? new();
}
