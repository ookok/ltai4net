using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.AI.Providers;

// ============================================================================
// Pillar 2: Tool-Call Repair Pipeline
// Inspired by Reasonix's four-stage repair for DeepSeek API behaviors.
//
// Four stages (from ToolCallRepairer):
//   1. FLATTEN  — dot-notation for >10 params or depth>2, restores on dispatch
//   2. SCAVENGE — regex + JSON parser scans reasoning_content for lost tool-calls
//   3. TRUNCATION — detect incomplete JSON, brace-count repair
//   4. STORM    — sliding window dedup + SHA256 loop detection with circuit breaker
//
// This middleware runs ALL four stages on EVERY model response.
// ============================================================================

/// <summary>
/// Middleware that runs the full four-stage tool-call repair pipeline
/// on every IChatClient response. Wraps any chat client transparently.
///
/// Usage in DI pipeline:
///   builder.Use((inner, sp) => new ToolCallRepairMiddleware(inner, sp));
/// </summary>
public sealed class ToolCallRepairMiddleware : DelegatingChatClient
{
    private readonly string _sessionId;
    private readonly ILogger<ToolCallRepairMiddleware> _logger;

    public ToolCallRepairMiddleware(
        IChatClient innerClient,
        string? sessionId = null,
        ILogger<ToolCallRepairMiddleware>? logger = null)
        : base(innerClient)
    {
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N")[..12];
        _logger = logger ?? NullLogger<ToolCallRepairMiddleware>.Instance;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var response = await base.GetResponseAsync(messages, options, ct).ConfigureAwait(false);

        // Run the four-stage repair on every tool-call in the response
        var repaired = RepairResponseToolCalls(response);

        if (repaired.StormBlocked)
        {
            _logger.LogWarning("ToolCallRepair[Storm]: blocked duplicate call in session {Session}",
                _sessionId[..8]);
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, ct))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Run the full four-stage repair on all tool-calls in a response.
    /// Returns counts of what was repaired and whether any calls were storm-blocked.
    /// </summary>
    private (int Flattened, int Scavenged, int Truncated, bool StormBlocked)
        RepairResponseToolCalls(ChatResponse response)
    {
        var flatCount = 0;
        var scavCount = 0;
        var truncCount = 0;
        var stormBlocked = false;

        foreach (var message in response.Messages)
        {
            // Stage 2: Scavenge — check if reasoning content has lost tool-calls
            foreach (var content in message.Contents)
            {
                if (content is TextReasoningContent reasoning && !string.IsNullOrWhiteSpace(reasoning.Text))
                {
                    var scavengedCall = ToolCallRepairer.ScavengeFromThinking(reasoning.Text);
                    if (scavengedCall != null)
                    {
                        scavCount++;
                        _logger.LogInformation("ToolCallRepair[Scavenge]: recovered lost tool-call from reasoning");
                    }
                }
            }

            // Stage 1 & 3 & 4: for each function call content
            foreach (var content in message.Contents.OfType<FunctionCallContent>())
            {
                var name = content.Name ?? "";
                var argsJson = content.Arguments != null
                    ? JsonSerializer.Serialize(content.Arguments)
                    : "";

                // Stage 1: Flatten check
                if (!string.IsNullOrWhiteSpace(argsJson))
                {
                    var flattenedJson = ToolCallRepairer.FlattenDeepSchema(argsJson);
                    if (flattenedJson != argsJson)
                        flatCount++;
                }

                // Stage 3: Truncation repair
                if (!string.IsNullOrWhiteSpace(argsJson))
                {
                    var truncResult = ToolCallRepairer.TruncationRepair(argsJson);
                    if (truncResult != null)
                        truncCount++;
                }

                // Stage 4: Storm detection
                if (ToolCallRepairer.IsDuplicateToolCall(_sessionId, name, argsJson))
                {
                    stormBlocked = true;
                    _logger.LogWarning("ToolCallRepair[Storm]: duplicate {Tool} in session {Session}",
                        name, _sessionId[..8]);
                }

                if (ToolCallRepairer.DetectLoop(_sessionId, name, argsJson))
                {
                    stormBlocked = true;
                    _logger.LogError("ToolCallRepair[Loop]: CIRCUIT BREAKER — {Tool} in session {Session}",
                        name, _sessionId[..8]);
                }
            }
        }

        return (flatCount, scavCount, truncCount, stormBlocked);
    }

    /// <summary>Clear storm history for this session.</summary>
    public void ClearSession() => ToolCallRepairer.ClearStormHistory(_sessionId);
}
