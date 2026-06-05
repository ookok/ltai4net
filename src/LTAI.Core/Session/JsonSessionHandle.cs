using System.Text.Json;
using Microsoft.Extensions.AI;

namespace LTAI.Core.Session;

public sealed class JsonSessionHandle : ISessionHandle
{
    private readonly string _name;
    private JsonElement? _state;
    private IReadOnlyList<ChatMessage> _messages;
    private string? _conversationId;

    public JsonSessionHandle(string name, JsonElement? state)
    {
        _name = name;
        _state = state;
        (_messages, _conversationId) = ExtractFromState(state);
    }

    public string Name => _name;
    public string SerializeToJson() => _state?.GetRawText() ?? "";
    public IReadOnlyList<ChatMessage> Messages => _messages;
    public string? ConversationId => _conversationId;

    public void UpdateFromJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            _state = null;
            _messages = [];
            _conversationId = null;
            return;
        }
        var doc = JsonDocument.Parse(json);
        _state = doc.RootElement.Clone();
        (_messages, _conversationId) = ExtractFromState(_state);
    }

    private static (IReadOnlyList<ChatMessage> messages, string? conversationId) ExtractFromState(JsonElement? state)
    {
        if (state == null || state.Value.ValueKind == JsonValueKind.Undefined)
            return ([], null);

        if (state.Value.ValueKind == JsonValueKind.Array)
            return (ExtractFromOldArray(state.Value), null);

        // MAF ChatClientAgentSession format:
        // { "conversationId": "...", "stateBag": { "InMemoryChatHistoryProvider": { "messages": [...] } } }
        var convId = state.Value.TryGetProperty("conversationId", out var c)
            ? c.GetString() : null;

        var msgs = ExtractFromMafState(state.Value);
        return (msgs, convId);
    }

    private static IReadOnlyList<ChatMessage> ExtractFromOldArray(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<ChatMessage>();
        foreach (var item in arr.EnumerateArray())
        {
            var role = item.TryGetProperty("Role", out var r) ? r.GetString() ?? "user"
                   : item.TryGetProperty("role", out var r2) ? r2.GetString() ?? "user" : "user";
            var content = item.TryGetProperty("Content", out var c) ? c.GetString() ?? ""
                        : item.TryGetProperty("content", out var c2) ? c2.GetString() ?? "" : "";
            list.Add(new ChatMessage(
                role is "assistant" or "Assistant" ? ChatRole.Assistant : ChatRole.User,
                content));
        }
        return list;
    }

    private static IReadOnlyList<ChatMessage> ExtractFromMafState(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return [];
        if (!obj.TryGetProperty("stateBag", out var bag) || bag.ValueKind != JsonValueKind.Object)
            return [];

        foreach (var providerKey in new[] { "InMemoryChatHistoryProvider", "ChatHistoryMemoryProvider" })
        {
            if (!bag.TryGetProperty(providerKey, out var provider) || provider.ValueKind != JsonValueKind.Object)
                continue;
            if (!provider.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
                continue;
            return ExtractMessagesFromArray(msgs);
        }
        return [];
    }

    private static IReadOnlyList<ChatMessage> ExtractMessagesFromArray(JsonElement arr)
    {
        var list = new List<ChatMessage>();
        foreach (var m in arr.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
            var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            list.Add(new ChatMessage(
                role is "assistant" or "Assistant" ? ChatRole.Assistant : ChatRole.User,
                content));
        }
        return list;
    }
}
