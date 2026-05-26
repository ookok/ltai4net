using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Providers;

public sealed class NormalizingChatClient : DelegatingChatClient
{
    private readonly ILogger<NormalizingChatClient> _logger;

    public NormalizingChatClient(IChatClient innerClient, ILogger<NormalizingChatClient> logger)
        : base(innerClient)
    {
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var response = await base.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        var parts = ExtractParts(response);
        response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        response.AdditionalProperties["NormalizedParts"] = parts;
        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        return NormalizeStream(base.GetStreamingResponseAsync(messages, options, ct), ct);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> NormalizeStream(
        IAsyncEnumerable<ChatResponseUpdate> upstream, [EnumeratorCancellation] CancellationToken ct)
    {
        var fullText = new StringBuilder();
        var reasoningText = new StringBuilder();
        var toolCallBuilders = new Dictionary<string, ToolCallAccumulator>();
        var orderedIds = new List<string>();

        await foreach (var update in upstream.WithCancellation(ct))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextReasoningContent reasoning)
                {
                    reasoningText.Append(reasoning.Text ?? "");
                }
                else if (content is TextContent text)
                {
                    fullText.Append(text.Text ?? "");
                }
                else if (content is FunctionCallContent funcCall)
                {
                    var callId = funcCall.CallId ?? Guid.NewGuid().ToString("N");
                    if (!toolCallBuilders.ContainsKey(callId))
                    {
                        toolCallBuilders[callId] = new ToolCallAccumulator();
                        orderedIds.Add(callId);
                    }
                    var acc = toolCallBuilders[callId];
                    if (!string.IsNullOrEmpty(funcCall.CallId)) acc.CallId = funcCall.CallId;
                    if (!string.IsNullOrEmpty(funcCall.Name)) acc.Name = funcCall.Name;
                    if (funcCall.Arguments != null)
                    {
                        foreach (var kvp in funcCall.Arguments)
                            acc.Args[kvp.Key] = kvp.Value?.ToString() ?? "";
                    }
                }
            }

            yield return update;
        }

        var completedToolCalls = new List<FunctionCallContent>();
        foreach (var callId in orderedIds)
        {
            var acc = toolCallBuilders[callId];
            if (!string.IsNullOrEmpty(acc.Name))
            {
                var argsJson = JsonSerializer.Serialize(acc.Args);
                var repaired = RescueParser.TryParseToolCall(argsJson);
                if (repaired != null)
                {
                    var repairedDict = new Dictionary<string, object?>();
                    foreach (var prop in repaired.Value.EnumerateObject())
                        repairedDict[prop.Name] = prop.Value.Clone();
                    completedToolCalls.Add(new FunctionCallContent(
                        acc.CallId ?? callId,
                        acc.Name,
                        repairedDict));
                }
                else
                {
                    completedToolCalls.Add(new FunctionCallContent(
                        acc.CallId ?? callId,
                        acc.Name,
                        acc.Args.ToDictionary(k => k.Key, v => (object?)v.Value)));
                }
            }
        }

        var parts = new List<Part>();
        if (reasoningText.Length > 0)
            parts.Add(new ReasoningPart(NextPartId(), reasoningText.ToString()));
        if (fullText.Length > 0)
            parts.Add(new TextPart(NextPartId(), fullText.ToString()));
        foreach (var tc in completedToolCalls)
        {
            var input = tc.Arguments != null
                ? tc.Arguments.ToDictionary(k => k.Key, v => v.Value)
                : new Dictionary<string, object?>();
            parts.Add(new ToolInvocationPart(
                NextPartId(),
                tc.Name ?? "",
                input,
                ToolState.Pending));
        }

        if (parts.Count > 0)
        {
            var finalUpdate = new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["NormalizedParts"] = parts
                }
            };
            yield return finalUpdate;
        }
    }

    private List<Part> ExtractParts(ChatResponse response)
    {
        var parts = new List<Part>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextReasoningContent reasoning)
                    parts.Add(new ReasoningPart(NextPartId(), reasoning.Text ?? ""));
                else if (content is TextContent text)
                    parts.Add(new TextPart(NextPartId(), text.Text ?? ""));
                else if (content is FunctionCallContent funcCall)
                {
                    var argsDict = funcCall.Arguments?.ToDictionary(k => k.Key, v => v.Value)
                        ?? new Dictionary<string, object?>();
                    var argsJson = JsonSerializer.Serialize(argsDict);
                    var repaired = RescueParser.TryParseToolCall(argsJson);
                    var input = repaired != null
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(repaired.Value.GetRawText())
                            ?? argsDict
                        : argsDict;
                    parts.Add(new ToolInvocationPart(
                        NextPartId(),
                        funcCall.Name ?? "",
                        input,
                        ToolState.Pending));
                }
            }
        }
        return parts;
    }

    private static string NextPartId() => $"np_{Guid.NewGuid():N}"[..16];

    private sealed class ToolCallAccumulator
    {
        public string? CallId;
        public string? Name;
        public readonly Dictionary<string, string> Args = new();
    }
}
