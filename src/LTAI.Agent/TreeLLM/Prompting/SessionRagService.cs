using System.Diagnostics;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Prompting;

public sealed record SessionRagResult
{
    public string Answer { get; init; } = "";
    public List<KnowledgeSearchResult> LongTermSources { get; init; } = new();
    public List<EventEntry> MemorySources { get; init; } = new();
    public string PromptUsed { get; init; } = "";
    public HallucinationVerdict? HallucinationCheck { get; init; }
    public double ElapsedMs { get; init; }
    public int TurnCount { get; init; }
}

public sealed class SessionRagService
{
    private readonly IChatClient _chatClient;
    private readonly AgenticRAG _agenticRAG;
    private readonly StructMemory _structMemory;
    private readonly PromptBuilder _promptBuilder;
    private readonly ContextBudget _contextBudget;
    private readonly ILogger<SessionRagService> _logger;

    public SessionRagService(
        IChatClient chatClient,
        AgenticRAG agenticRAG,
        StructMemory structMemory,
        PromptBuilder promptBuilder,
        ILogger<SessionRagService>? logger = null,
        ContextBudget? contextBudget = null)
    {
        _chatClient = chatClient;
        _agenticRAG = agenticRAG;
        _structMemory = structMemory;
        _promptBuilder = promptBuilder;
        _contextBudget = contextBudget ?? ContextBudget.Instance;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionRagService>.Instance;
    }

    public async Task<SessionRagResult> AskAsync(
        string sessionId,
        string question,
        PromptBuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var opts = options ?? PromptBuildOptions.Default;

        var sessionSnapshot = SessionResilience.Instance.Restore(sessionId);

        var historyMessages = sessionSnapshot?.Messages
            ?.Select(m => new Dictionary<string, object>
            {
                ["role"] = m.GetValueOrDefault("role", "user"),
                ["content"] = m.GetValueOrDefault("content", "")
            }).ToList() ?? new();

        await _structMemory.BindEvents(sessionId, historyMessages).ConfigureAwait(false);
        var (memoryEvents, memorySynthesis) = await _structMemory.RetrieveForQuery(question).ConfigureAwait(false);

        var sessionContext = _structMemory.GetContextBlock(question, memoryEvents, memorySynthesis);
        opts.SessionContext = string.IsNullOrEmpty(sessionContext) ? null : sessionContext;

        var longTermDocs = _agenticRAG.Search(question, RAGMode.Iterative, domain: opts.Domain ?? "general");

        // Pass REAL history to budget check (was previously empty list)
        var historyTokens = historyMessages.Sum(m => _contextBudget.EstimateTokens(
            m.GetValueOrDefault("content", "")?.ToString() ?? ""));
        var promptTokens = _contextBudget.EstimateTokens(question);
        var totalTokens = historyTokens + promptTokens;
        if (totalTokens > 50000 * 0.85)
        {
            _logger.LogWarning("SessionRAG: context near limit ({Tokens}/50000)", totalTokens);
        }

        // Use multi-turn chat messages INCLUDING raw dialog history
        var messages = await _promptBuilder.BuildChatMessages(
            question, longTermDocs, historyMessages,
            maxHistoryTokens: 8000, options: opts).ConfigureAwait(false);

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var answer = response.Text ?? string.Empty;

        var hallucinationCheck = HallucinationGuard.Instance.CheckGeneration(
            answer,
            string.Join("\n", longTermDocs.Select(d => d.Content)));

        var eventId = Guid.NewGuid().ToString("N")[..12];
        await _structMemory.BindEvents(sessionId, new()
        {
            new() { ["role"] = "user", ["content"] = question },
            new() { ["role"] = "assistant", ["content"] = answer }
        }, eventId);

        SessionResilience.Instance.Save(sessionId, question, answer,
            intent: RetrievalFramework.Instance.Classify(question).ToString());

        sw.Stop();

        _logger.LogInformation(
            "SessionRAG: session={SessionId}, sources={LongTermCount}/{MemoryCount}, {ElapsedMs}ms",
            sessionId, longTermDocs.Count, memoryEvents.Count, sw.ElapsedMilliseconds);

        return new SessionRagResult
        {
            Answer = answer,
            LongTermSources = longTermDocs,
            MemorySources = memoryEvents,
            PromptUsed = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? question,
            HallucinationCheck = hallucinationCheck,
            ElapsedMs = sw.ElapsedMilliseconds,
            TurnCount = sessionSnapshot?.TurnCount + 1 ?? 1
        };
    }

    public async IAsyncEnumerable<string> AskStreamingAsync(
        string sessionId,
        string question,
        PromptBuildOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var opts = options ?? PromptBuildOptions.Default;

        var sessionSnapshot = SessionResilience.Instance.Restore(sessionId);
        var historyMessages = sessionSnapshot?.Messages
            ?.Select(m => new Dictionary<string, object>
            {
                ["role"] = m.GetValueOrDefault("role", "user"),
                ["content"] = m.GetValueOrDefault("content", "")
            }).ToList() ?? new();

        await _structMemory.BindEvents(sessionId, historyMessages).ConfigureAwait(false);
        var (memoryEvents, memorySynthesis) = await _structMemory.RetrieveForQuery(question).ConfigureAwait(false);
        opts.SessionContext = _structMemory.GetContextBlock(question, memoryEvents, memorySynthesis);

        var longTermDocs = _agenticRAG.Search(question, RAGMode.Iterative, domain: opts.Domain ?? "general");
        var prompt = await _promptBuilder.BuildSinglePrompt(question, longTermDocs, opts).ConfigureAwait(false);

        var fullAnswer = "";
        await foreach (var update in _chatClient.GetStreamingResponseAsync(prompt, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                fullAnswer += update.Text;
                yield return update.Text;
            }
        }

        SessionResilience.Instance.Save(sessionId, question, fullAnswer);

        var eventId = Guid.NewGuid().ToString("N")[..12];
        var messages = new List<Dictionary<string, object>>
        {
            new() { ["role"] = "user", ["content"] = question },
            new() { ["role"] = "assistant", ["content"] = fullAnswer }
        };
        var repaired = ValidateAndRepairMessages(messages);
        await _structMemory.BindEvents(sessionId, repaired, eventId);

        var hallucinationCheck = HallucinationGuard.Instance.CheckGeneration(
            fullAnswer,
            string.Join("\n", longTermDocs.Select(d => d.Content)));

        _logger.LogInformation(
            "SessionRAG Streaming: session={SessionId}, sources={LongTermCount}, answerLen={AnswerLen}, hallucination={HallucinationScore}",
            sessionId, longTermDocs.Count, fullAnswer.Length, hallucinationCheck?.Score ?? 0);
    }

    /// <summary>
    /// 7-phase message history validation and repair. From OpenFang's Session Repair pattern:
    /// 1. Remove empty messages
    /// 2. Fix role alternation (no consecutive same-role messages)
    /// 3. Remove duplicate content
    /// 4. Truncate oversized messages
    /// 5. Ensure system message is first
    /// 6. Remove orphaned tool messages
    /// 7. Cap total message count
    /// </summary>
    internal static List<Dictionary<string, object>> ValidateAndRepairMessages(
        List<Dictionary<string, object>> messages, int maxMessages = 50, int maxContentLen = 8000)
    {
        if (messages == null || messages.Count == 0)
            return new List<Dictionary<string, object>>();

        var result = new List<Dictionary<string, object>>();

        // Phase 1: Remove empty messages
        var filtered = messages
            .Where(m => m.TryGetValue("content", out var c) && c is string s && !string.IsNullOrWhiteSpace(s))
            .ToList();

        // Phase 2: Fix role alternation (merge consecutive same-role messages)
        string? lastRole = null;
        foreach (var msg in filtered)
        {
            var role = msg.GetValueOrDefault("role", "")?.ToString() ?? "";
            var content = msg.GetValueOrDefault("content", "")?.ToString() ?? "";

            if (role == lastRole && result.Count > 0)
            {
                var prevContent = result[^1].GetValueOrDefault("content", "")?.ToString() ?? "";
                result[^1]["content"] = prevContent + "\n\n" + content;
            }
            else
            {
                result.Add(new Dictionary<string, object>(msg));
                lastRole = role;
            }
        }

        // Phase 3: Remove duplicate content
        var seen = new HashSet<string>();
        result = result.Where(m =>
        {
            var content = (m.GetValueOrDefault("content", "")?.ToString() ?? "")[..Math.Min(200,
                (m.GetValueOrDefault("content", "")?.ToString() ?? "").Length)];
            return seen.Add(content);
        }).ToList();

        // Phase 4: Truncate oversized messages
        foreach (var msg in result)
        {
            if (msg.TryGetValue("content", out var c) && c is string s && s.Length > maxContentLen)
                msg["content"] = s[..maxContentLen] + "\n... [truncated]";
        }

        // Phase 5: Ensure system message is first
        var systemMsg = result.FirstOrDefault(m =>
            (m.GetValueOrDefault("role", "")?.ToString() ?? "").Equals("system", StringComparison.OrdinalIgnoreCase));
        if (systemMsg != null)
        {
            result.Remove(systemMsg);
            result.Insert(0, systemMsg);
        }

        // Phase 6: Remove orphaned tool messages (tool must follow assistant)
        for (var i = result.Count - 1; i >= 0; i--)
        {
            var role = result[i].GetValueOrDefault("role", "")?.ToString() ?? "";
            if (role == "tool" && (i == 0 || result[i - 1].GetValueOrDefault("role", "")?.ToString() != "assistant"))
            {
                // Convert orphaned tool to user message with context
                result[i]["role"] = "user";
                result[i]["content"] = "[Tool Response] " + (result[i].GetValueOrDefault("content", "")?.ToString() ?? "");
            }
        }

        // Phase 7: Cap total message count (keep system + last N messages)
        if (result.Count > maxMessages)
        {
            var sysIdx = result[0].GetValueOrDefault("role", "")?.ToString() == "system" ? 1 : 0;
            if (sysIdx > 0)
                result = new List<Dictionary<string, object>> { result[0] }
                    .Concat(result.Skip(result.Count - (maxMessages - 1))).ToList();
            else
                result = result.Skip(result.Count - maxMessages).ToList();
        }

        return result;
    }
}
