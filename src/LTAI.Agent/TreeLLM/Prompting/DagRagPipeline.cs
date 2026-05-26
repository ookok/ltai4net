using System.Diagnostics;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Prompting;

public sealed class DagRagPipeline
{
    private readonly IChatClient _chatClient;
    private readonly AgenticRAG _agenticRAG;
    private readonly PromptBuilder _promptBuilder;
    private readonly int _maxParallelSearches;
    private readonly ILogger<DagRagPipeline> _logger;

    public DagRagPipeline(
        IChatClient chatClient,
        AgenticRAG agenticRAG,
        PromptBuilder promptBuilder,
        int maxParallelSearches = 4,
        ILogger<DagRagPipeline>? logger = null)
    {
        _chatClient = chatClient;
        _agenticRAG = agenticRAG;
        _promptBuilder = promptBuilder;
        _maxParallelSearches = maxParallelSearches;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DagRagPipeline>.Instance;
    }

    public async Task<RagPipelineResult> AskAsync(
        string question,
        PromptBuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var opts = options ?? PromptBuildOptions.Default;

        var searchTasks = new List<Task<List<KnowledgeSearchResult>>>
        {
            _agenticRAG.SearchAsync(question, RAGMode.Iterative, domain: opts.Domain ?? "general"),
            _agenticRAG.SearchAsync(question, RAGMode.MultiAgent, domain: opts.Domain ?? "general"),
            _agenticRAG.SearchAsync(question, RAGMode.Reflective, domain: opts.Domain ?? "general")
        };

        var variants = GenerateQueryVariants(question);
        foreach (var variant in variants.Take(_maxParallelSearches - 3))
        {
            searchTasks.Add(
                _agenticRAG.SearchAsync(variant, RAGMode.Iterative, domain: opts.Domain ?? "general"));
        }

        var allResults = await Task.WhenAll(searchTasks).ConfigureAwait(false);
        var mergedDocs = MergeAndDeduplicate(allResults.ToList());

        var prompt = await _promptBuilder.BuildSinglePrompt(question, mergedDocs, opts).ConfigureAwait(false);
        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var answer = response.Text ?? string.Empty;

        sw.Stop();

        _logger.LogInformation(
            "DAG RAG pipeline: {SourceCount} sources from {ParallelSearches} parallel searches, {ElapsedMs}ms",
            mergedDocs.Count, searchTasks.Count, sw.ElapsedMilliseconds);

        return new RagPipelineResult
        {
            Answer = answer,
            Sources = mergedDocs,
            PromptUsed = prompt,
            ElapsedMs = sw.ElapsedMilliseconds,
            TokensUsed = TokenCounter.Estimate(answer) + TokenCounter.Estimate(prompt)
        };
    }

    public async IAsyncEnumerable<string> AskStreamingAsync(
        string question,
        PromptBuildOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var opts = options ?? PromptBuildOptions.Default;

        var searchTasks = new List<Task<List<KnowledgeSearchResult>>>
        {
            _agenticRAG.SearchAsync(question, RAGMode.Iterative, domain: opts.Domain ?? "general"),
            _agenticRAG.SearchAsync(question, RAGMode.MultiAgent, domain: opts.Domain ?? "general")
        };

        var variants = GenerateQueryVariants(question);
        foreach (var variant in variants.Take(_maxParallelSearches - 2))
        {
            searchTasks.Add(
                _agenticRAG.SearchAsync(variant, RAGMode.Iterative, domain: opts.Domain ?? "general"));
        }

        var allResults = await Task.WhenAll(searchTasks).ConfigureAwait(false);
        var mergedDocs = MergeAndDeduplicate(allResults.ToList());
        var prompt = await _promptBuilder.BuildSinglePrompt(question, mergedDocs, opts).ConfigureAwait(false);

        await foreach (var update in _chatClient.GetStreamingResponseAsync(prompt, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    private static List<KnowledgeSearchResult> MergeAndDeduplicate(
        List<List<KnowledgeSearchResult>> resultSets)
    {
        var merged = new List<KnowledgeSearchResult>();
        var seenIds = new HashSet<string>();

        foreach (var results in resultSets)
        {
            foreach (var r in results)
            {
                if (string.IsNullOrEmpty(r.Id) || seenIds.Add(r.Id))
                    merged.Add(r);
            }
        }

        return merged
            .OrderByDescending(r => r.Score)
            .DistinctBy(r => r.Id ?? r.Content[..Math.Min(40, r.Content.Length)])
            .Take(30)
            .ToList();
    }

    private static List<string> GenerateQueryVariants(string query)
    {
        var variants = new List<string>();

        var lower = query.ToLower();
        if (!lower.Contains("how") && !lower.Contains("what") && !lower.Contains("why"))
            variants.Add($"Explain the key concepts behind: {query}");
        if (query.Length > 30)
            variants.Add(query[..Math.Min(40, query.Length / 2)]);

        variants.Add(query.Replace("?", "").Trim());

        return variants.Distinct().Where(v => v.Length > 5).Take(4).ToList();
    }
}
