using System.Text;
using System.Text.Json;
using LTAI.AI;
using LTAI.Agent.Formats;
using LTAI.Agent.FusionRoute;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public sealed partial class ChatAgent
{
    private void RecordSessionError(string sessionId)
    {
        var state = _sessionErrorStates.GetOrAdd(sessionId, _ => new PerSessionErrorState());
        Interlocked.Increment(ref state.ErrorCount);
    }

    private bool IsSessionCircuitOpen(string sessionId)
    {
        if (!_sessionErrorStates.TryGetValue(sessionId, out var state)) return false;
        if (state.CircuitOpenUntil.HasValue && DateTime.UtcNow < state.CircuitOpenUntil.Value)
            return true;
        if (state.ErrorCount >= _sessionMaxErrors)
        {
            state.CircuitOpenUntil = DateTime.UtcNow + _sessionCircuitDuration;
            return true;
        }
        return false;
    }

    private void ResetSessionErrors(string sessionId)
    {
        if (_sessionErrorStates.TryGetValue(sessionId, out var state))
        {
            Interlocked.Exchange(ref state.ErrorCount, 0);
            state.CircuitOpenUntil = null;
        }
        var now = DateTime.UtcNow;
        foreach (var kv in _sessionErrorStates)
        {
            if (kv.Value.ErrorCount == 0 && kv.Value.CircuitOpenUntil == null)
                _sessionErrorStates.TryRemove(kv.Key, out _);
        }
    }

    private async Task<AgentSession> TryRestoreFromCheckpointAsync(ISessionHandle? sessionHandle, AgentSession session, CancellationToken ct)
    {
        if (_checkpointStore == null || sessionHandle == null) return session;
        var sessionId = sessionHandle.Name;
        try
        {
            var cp = await _checkpointStore.FindNearestAsync(sessionId, long.MaxValue, ct).ConfigureAwait(false);
            if (cp?.data == null) return session;

            var cpData = JsonSerializer.Deserialize<CheckpointData>(Encoding.UTF8.GetString(cp.Value.data));
            if (cpData?.SessionData == null) return session;

            var currentMsgs = sessionHandle.Messages.Count;
            if (cpData.MsgCount <= currentMsgs) return session;

            if (string.IsNullOrEmpty(cpData.SessionData) || cpData.SessionData.Length < 20) return session;
            JsonElement restoredElement;
            try { restoredElement = JsonDocument.Parse(cpData.SessionData).RootElement.Clone(); }
            catch { return session; }

            _logger.LogInformation("Restoring session {SessionId} from checkpoint at msgCount={MsgCount} (current={Current})",
                sessionId, cpData.MsgCount, currentMsgs);

            var restored = await _agent.DeserializeSessionAsync(restoredElement, cancellationToken: ct).ConfigureAwait(false);

            var restoredJson = await _agent.SerializeSessionAsync(restored, cancellationToken: ct).ConfigureAwait(false);
            sessionHandle.UpdateFromJson(restoredJson.GetRawText());

            return restored;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checkpoint restore failed for session {SessionId}", sessionId);
            return session;
        }
    }

    private sealed record CheckpointData
    {
        public string Session { get; init; } = "";
        public long Tokens { get; init; }
        public int MsgCount { get; init; }
        public string? SessionData { get; init; }
    }

    private async Task SaveCheckpointAsync(string sessionId, IList<ChatMessage>? messages, AgentSession? session, CancellationToken ct)
    {
        if (_checkpointStore == null || messages == null || messages.Count == 0) return;

        var lockObj = _sessionCheckpointLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long tokenCount = 0;
            foreach (var msg in messages)
            {
                if (!string.IsNullOrEmpty(msg.Text))
                    tokenCount += TokenEstimator.Estimate(msg.Text);
            }
            var key = $"session:{sessionId}:pos:{tokenCount}";

            var sessionCounter = _sessionCheckpointCounters.AddOrUpdate(sessionId, 1, (_, v) => v + 1);
            string? sessionData = null;
            if (session != null && sessionCounter % 10 == 0)
            {
                try
                {
                    var sessionJson = await _agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false);
                    sessionData = sessionJson.GetRawText();
                }
                catch { _logger?.LogDebug("Session serialization failed (best-effort)"); }
            }

            var data = JsonSerializer.Serialize(new CheckpointData
            {
                Session = sessionId,
                Tokens = tokenCount,
                MsgCount = messages.Count,
                SessionData = sessionData
            });
            await _checkpointStore.StoreAsync(key, Encoding.UTF8.GetBytes(data), tokenCount, ct).ConfigureAwait(false);

            if (sessionCounter == 200)
            {
                try
                {
                    _sessionCheckpointCounters.TryRemove(sessionId, out _);
                    await _checkpointStore.InvalidateSessionAsync(sessionId, ct).ConfigureAwait(false);
                    _sessionCheckpointCounters.AddOrUpdate(sessionId, 1, (_, _) => 1);
                    await _checkpointStore.StoreAsync(key, Encoding.UTF8.GetBytes(data), tokenCount, ct).ConfigureAwait(false);
                }
                catch { _logger?.LogDebug("Checkpoint compaction failed (best-effort)"); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SaveCheckpointAsync failed for session {SessionId}", sessionId);
        }
        finally
        {
            lockObj.Release();
        }
    }

    private void SaveCheckpointFireAndForget(string sessionId, IList<ChatMessage>? messages, AgentSession? session, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await SaveCheckpointAsync(sessionId, messages, session, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Checkpoint fire-and-forget failed for session {SessionId}", sessionId); }
        });
    }

    private async Task<AgentSession> CreateAgentSessionFromHandleAsync(ISessionHandle handle, CancellationToken ct)
    {
        var json = handle.SerializeToJson();
        if (string.IsNullOrEmpty(json))
            return await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        var element = JsonDocument.Parse(json).RootElement;
        return await _agent.DeserializeSessionAsync(element, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task SaveSessionToHandleAsync(AgentSession session, ISessionHandle handle, CancellationToken ct)
    {
        var json = await _agent.SerializeSessionAsync(session, cancellationToken: ct).ConfigureAwait(false);
        handle.UpdateFromJson(json.GetRawText());
    }

    private async Task<string> TrySpanRoutingAsync(
        string message, string originalText, L1State l1State, AgentSession session, CancellationToken ct)
    {
        if (_proAgent == null) return originalText;
        var spanRouter = new ResponseSpanRouter();
        var refinePrompt = l1State.ToSpanRoutingHandoff(message);
        var result = await _proAgent.RunAsync(
            [new ChatMessage(ChatRole.User, refinePrompt)], session,
            cancellationToken: ct).ConfigureAwait(false);
        var refined = ApplyBlockedOutput(result.Messages?.LastOrDefault()?.Text ?? "");
        if (string.IsNullOrWhiteSpace(refined) || refined.Length <= 10)
            return originalText;

        var refinedSpans = spanRouter.ParseSpans(refined);
        var stitched = spanRouter.Stitch(l1State.Spans,
            l1State.Spans.Where(s => s.UncertaintyScore >= 0.4).ToList(),
            refinedSpans.Select(s => s.Text).ToList());
        return $"[FusionRoute: refined {l1State.Spans.Count(s => s.UncertaintyScore >= 0.4)}/{l1State.Spans.Count} spans]\n\n{stitched}";
    }

    private async Task<string> FullRegenerationAsync(
        string message, string reason, L1State l1State, AgentSession session, CancellationToken ct)
    {
        if (_proAgent == null) return message;
        var l1Handoff = l1State.ToHandoff(ResultFormat.Toon);
        var l2Messages = new[]
        {
            new ChatMessage(ChatRole.System,
                "You are the Pro assistant. A Flash assistant attempted this query " +
                "but could not produce a satisfactory answer. Below is the structured " +
                "exploration state from the Flash attempt.\n\n" + l1Handoff),
            new ChatMessage(ChatRole.User,
                $"The Flash assistant escalated for reason: {reason}\n\n" +
                $"Original query: {message}")
        };
        var result = await _proAgent.RunAsync(l2Messages, session, cancellationToken: ct).ConfigureAwait(false);
        var text = ApplyBlockedOutput(result.Messages?.LastOrDefault()?.Text ?? "");
        return $"[Auto-upgraded to Pro: {reason}]\n\n{text}";
    }

    private static List<(string Name, string Arguments, string Result)> ExtractFileToolCalls(IList<ChatMessage> messages)
    {
        var calls = new List<(string Name, string Arguments, string Result)>();
        var callMap = new Dictionary<string, (string Name, string Arguments)>();

        foreach (var msg in messages)
        {
            if (msg.Contents == null) continue;

            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fcc && fcc.Name != null)
                {
                    var callId = fcc.CallId ?? Guid.NewGuid().ToString();
                    var args = fcc.Arguments != null
                        ? JsonSerializer.Serialize(fcc.Arguments)
                        : "";
                    callMap[callId] = (fcc.Name, args);
                }
                else if (content is FunctionResultContent frc)
                {
                    var key = frc.CallId ?? "";
                    if (callMap.TryGetValue(key, out var callInfo) && FileToolNames.Contains(callInfo.Name))
                    {
                        calls.Add((callInfo.Name, callInfo.Arguments, frc.Result?.ToString() ?? ""));
                        callMap.Remove(key);
                    }
                }
            }
        }

        return calls;
    }

    private async Task<(bool HasErrors, List<ChatMessage> ErrorMessages, double Difficulty)> PostGenerationGrammarCheckAsync(
        IList<ChatMessage> messages, CancellationToken ct)
    {
        var toolCalls = ExtractFileToolCalls(messages);
        if (toolCalls.Count == 0)
            return (false, [], 0);

        var ctx = new MessageContext("", ct);
        foreach (var (name, args, result) in toolCalls)
            ctx.ToolCalls.Add((name, args, result));

        // Run full post-generation pipeline including CriticRepair
        ctx = await _pipelineRunner.RunPostGenerationAsync(ctx).ConfigureAwait(false);

        // Collect ALL blocking flags (not just GrammarCheckBlocked)
        var hasBlockers = ctx.GrammarCheckBlocked
            || ctx.AntiPatternBlocked
            || ctx.QualityGateBlocked
            || ctx.DoDBlocked;

        if (hasBlockers)
        {
            // CriticRepairStep already synthesized repair hints into ctx.Messages
            // as System messages. Extract them for retry feedback.
            var repairMessages = ctx.Messages
                .Where(m => m.Role == ChatRole.System)
                .ToList();

            // Extract difficulty from CriticRepairState if available
            var difficulty = 0.5; // default medium
            if (ctx.TryGet<CriticRepairState>("CriticRepairState", out var repairState) && repairState != null)
                difficulty = repairState.LastDifficulty;

            return (true, repairMessages, difficulty);
        }

        return (false, [], 0);
    }
}
