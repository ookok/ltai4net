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
        using var doc = JsonDocument.Parse(json);
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
            var chatRole = role is "assistant" or "Assistant" ? ChatRole.Assistant : ChatRole.User;

            var contents = new List<AIContent>();

            // Extract text content
            var text = item.TryGetProperty("Content", out var c) ? c.GetString()
                     : item.TryGetProperty("content", out var c2) ? c2.GetString() : null;
            if (!string.IsNullOrEmpty(text))
                contents.Add(new TextContent(text));

            // Extract function calls from Contents array
            if (item.TryGetProperty("Contents", out var contentsArr) && contentsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var ct in contentsArr.EnumerateArray())
                {
                    if (ct.TryGetProperty("Name", out var fn) || ct.TryGetProperty("name", out fn))
                    {
                        var callId = ct.TryGetProperty("CallId", out var ci) || ct.TryGetProperty("callId", out ci)
                            ? ci.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                        var args = ct.TryGetProperty("Arguments", out var argEl) && argEl.ValueKind == JsonValueKind.Object
                            ? new Dictionary<string, object?>(argEl.EnumerateObject()
                                .ToDictionary(p => p.Name, p => (object?)p.Value.ToString()))
                            : null;
                        contents.Add(new FunctionCallContent(callId, fn.GetString() ?? "", args));
                    }
                    else if (ct.TryGetProperty("CallId", out var frcId) || ct.TryGetProperty("callId", out frcId))
                    {
                        // FunctionResultContent — detect by presence of CallId with no Name (tool result)
                        var result = ct.TryGetProperty("Result", out var res) ? res.ToString()
                                   : ct.TryGetProperty("result", out var res2) ? res2.ToString() : null;
                        if (result != null)
                            contents.Add(new FunctionResultContent(frcId.GetString() ?? "", result));
                    }
                }
            }

            list.Add(new ChatMessage(chatRole, contents));
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

    /// <summary>
    /// Extract messages from MAF state JSON for lightweight display.
    /// NOTE: Text content is preserved for UI display (ChatView, TUI ChatWindow).
    /// Non-text content (function calls, results, images) is also included
    /// for full fidelity. The full state is preserved via MAF's
    /// SerializeSessionAsync/DeserializeSessionAsync.
    /// </summary>
    private static IReadOnlyList<ChatMessage> ExtractMessagesFromArray(JsonElement arr)
    {
        var list = new List<ChatMessage>();
        foreach (var m in arr.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var role = m.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
            var chatRole = role is "assistant" or "Assistant" ? ChatRole.Assistant : ChatRole.User;

            var contents = new List<AIContent>();

            // Extract text content
            var content = m.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (!string.IsNullOrEmpty(content))
                contents.Add(new TextContent(content));

            // Extract function calls from separate fields
            if (m.TryGetProperty("functionCalls", out var fcs) && fcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var fc in fcs.EnumerateArray())
                {
                    var fn = fc.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var callId = fc.TryGetProperty("callId", out var ci) || fc.TryGetProperty("call_id", out ci)
                        ? ci.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                    if (!string.IsNullOrEmpty(fn))
                        contents.Add(new FunctionCallContent(callId, fn, null));
                }
            }

            // Extract tool results
            if (m.TryGetProperty("toolResults", out var trs) && trs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tr in trs.EnumerateArray())
                {
                    var callId = tr.TryGetProperty("callId", out var ci) || tr.TryGetProperty("call_id", out ci)
                        ? ci.GetString() : null;
                    var result = tr.TryGetProperty("result", out var res) ? res.ToString() : null;
                    if (!string.IsNullOrEmpty(callId) && result != null)
                        contents.Add(new FunctionResultContent(callId, result));
                }
            }

            list.Add(new ChatMessage(chatRole, contents));
        }
        return list;
    }
}
