using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LTAI.Tests;

public enum ChaosBehavior { Timeout, Error, EmptyResponse, Hallucination, LatencySpike }

public sealed record ChaosRule(
    string Name, string TriggerPattern, ChaosBehavior Behavior, int DelayMs = 5000);

public sealed class ChaoticChatClient : IChatClient
{
    private readonly List<(string trigger, Func<string, string> handler)> _routes = new();
    private readonly List<ChaosRule> _chaos = new();
    private readonly List<string> _injectedFailures = new();
    public IReadOnlyList<string> InjectedFailures => _injectedFailures;

    public ChaoticChatClient AddRoute(string trigger, Func<string, string> response)
    {
        _routes.Add((trigger, response));
        return this;
    }

    public ChaoticChatClient InjectChaos(ChaosRule rule)
    {
        _chaos.Add(rule);
        return this;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var msgList = messages.ToList();
        var lastMsg = msgList.LastOrDefault()?.Text ?? "";

        foreach (var rule in _chaos)
        {
            if (lastMsg.Contains(rule.TriggerPattern, StringComparison.OrdinalIgnoreCase))
            {
                _injectedFailures.Add($"{rule.Name}:{rule.Behavior}");

                if (rule.Behavior == ChaosBehavior.Timeout)
                    throw new TimeoutException($"Chaos[{rule.Name}]: timeout after {rule.DelayMs}ms");

                if (rule.Behavior == ChaosBehavior.Error)
                    throw new InvalidOperationException($"Chaos[{rule.Name}]: injected error");

                if (rule.Behavior == ChaosBehavior.LatencySpike)
                {
                    await Task.Delay(rule.DelayMs, ct);
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        $"[CHAOS-LATENCY] Response after {rule.DelayMs}ms delay"));
                }

                return rule.Behavior switch
                {
                    ChaosBehavior.EmptyResponse => new ChatResponse(new ChatMessage(ChatRole.Assistant, "")),
                    ChaosBehavior.Hallucination => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        $"[CHAOS-HALLUCINATION] Reference: GB 3095-2099 — TOTALLY FABRICATED STANDARD")),
                    _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, $"[CHAOS:{rule.Name}] ok"))
                };
            }
        }

        foreach (var (trigger, handler) in _routes)
        {
            if (lastMsg.Contains(trigger, StringComparison.OrdinalIgnoreCase))
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, handler(lastMsg)));
        }

        await Task.CompletedTask;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            $"FAKE: received '{lastMsg[..Math.Min(lastMsg.Length, 50)]}'"));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgList = messages.ToList();
        var lastMsg = msgList.LastOrDefault()?.Text ?? "";

        foreach (var rule in _chaos)
        {
            if (lastMsg.Contains(rule.TriggerPattern, StringComparison.OrdinalIgnoreCase))
            {
                _injectedFailures.Add($"{rule.Name}:{rule.Behavior}");
                if (rule.Behavior == ChaosBehavior.Error)
                    throw new InvalidOperationException($"Chaos[{rule.Name}]: injected error");
                if (rule.Behavior == ChaosBehavior.Timeout)
                { await Task.Delay(rule.DelayMs, ct); throw new TimeoutException($"Chaos[{rule.Name}]: timeout"); }
                break;
            }
        }

        foreach (var (trigger, handler) in _routes)
        {
            if (lastMsg.Contains(trigger, StringComparison.OrdinalIgnoreCase))
            {
                var text = handler(lastMsg);
                foreach (var chunk in text.Split(' '))
                    yield return new ChatResponseUpdate(ChatRole.Assistant, chunk + " ");
                yield break;
            }
        }
        yield return new ChatResponseUpdate(ChatRole.Assistant, "FAKE");
        await Task.CompletedTask;
    }

    public void Dispose() { }
    public object? GetService(Type t, object? k = null) => null;
}
