using System.Diagnostics;
using LTAI.Core.System;
using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.TreeLLM.Prompting;

public sealed record RagPipelineResult
{
    public string Answer { get; init; } = "";
    public List<KnowledgeSearchResult> Sources { get; init; } = new();
    public string PromptUsed { get; init; } = "";
    public HallucinationVerdict? HallucinationCheck { get; init; }
    public double ElapsedMs { get; init; }
    public int TokensUsed { get; init; }
    public int SourceCount => Sources.Count;
}

public sealed class RagPipeline
{
    private readonly IChatClient _chatClient;
    private readonly AgenticRAG _agenticRAG;
    private readonly PromptBuilder _promptBuilder;
    private readonly ILogger<RagPipeline> _logger;

    public RagPipeline(
        IChatClient chatClient,
        AgenticRAG agenticRAG,
        PromptBuilder promptBuilder,
        ILogger<RagPipeline>? logger = null)
    {
        _chatClient = chatClient;
        _agenticRAG = agenticRAG;
        _promptBuilder = promptBuilder;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RagPipeline>.Instance;
    }

    public async Task<RagPipelineResult> AskAsync(
        string question,
        PromptBuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var opts = options ?? PromptBuildOptions.Default;

        var docs = _agenticRAG.Search(question, RAGMode.Iterative, domain: opts.Domain ?? "general");

        var prompt = await _promptBuilder.BuildSinglePrompt(question, docs, opts);

        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        var answer = response.Text ?? string.Empty;

        var hallucinationCheck = HallucinationGuard.Instance.CheckGeneration(
            answer,
            string.Join("\n", docs.Select(d => d.Content)));

        sw.Stop();

        _logger.LogInformation(
            "RAG pipeline completed: {SourceCount} sources, {ElapsedMs}ms, hallucinationScore={Score}",
            docs.Count, sw.ElapsedMilliseconds, hallucinationCheck.Score);

        return new RagPipelineResult
        {
            Answer = answer,
            Sources = docs,
            PromptUsed = prompt,
            HallucinationCheck = hallucinationCheck,
            ElapsedMs = sw.ElapsedMilliseconds,
            TokensUsed = EstimateTokens(prompt) + EstimateTokens(answer)
        };
    }

    public async IAsyncEnumerable<string> AskStreamingAsync(
        string question,
        PromptBuildOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var opts = options ?? PromptBuildOptions.Default;
        var docs = _agenticRAG.Search(question, RAGMode.Iterative, domain: opts.Domain ?? "general");
        var prompt = await _promptBuilder.BuildSinglePrompt(question, docs, opts);

        await foreach (var update in _chatClient.GetStreamingResponseAsync(prompt, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    private static int EstimateTokens(string text) =>
        TokenCounter.Estimate(text);
}

