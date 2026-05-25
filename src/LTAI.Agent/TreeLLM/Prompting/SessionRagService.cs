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

        var prompt = await _promptBuilder.BuildSinglePrompt(question, longTermDocs, opts).ConfigureAwait(false);

        var (needsCompression, _, dropped) = _contextBudget.AddAndCheck("system", new List<Dictionary<string, string>>(), prompt);
        if (needsCompression || dropped > 0)
        {
            _logger.LogWarning(
                "SessionRAG: context budget warning. needsCompression={NeedsCompression}, dropped={Dropped}, tokens={TotalTokens}/{MaxTokens}",
                needsCompression, dropped,
                _contextBudget.GetStats()["total_tokens"], _contextBudget.GetStats()["max_tokens"]);
        }

        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var answer = response.Text ?? string.Empty;

        var hallucinationCheck = HallucinationGuard.Instance.CheckGeneration(
            answer,
            string.Join("\n", longTermDocs.Select(d => d.Content)));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        await _structMemory.BindEvents(sessionId, new()
        {
            new() { ["role"] = "user", ["content"] = question },
            new() { ["role"] = "assistant", ["content"] = answer }
        }, now);

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
            PromptUsed = prompt,
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
    }
}
