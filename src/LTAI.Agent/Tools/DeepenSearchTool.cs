using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using LTAI.Agent.Vector;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

/// <summary>
/// DRIFT-inspired iterative deepen search tool.
/// Performs an initial knowledge graph search, identifies knowledge gaps
/// via LLM, generates follow-up queries, searches again, and combines results.
/// Enables multi-hop knowledge discovery without manual query refinement.
/// </summary>
[ToolDomain("knowledge")]
public sealed class DeepenSearchTool
{
    private readonly KbGraph _kbGraph;
    private readonly IChatClient _llm;
    private readonly ILogger<DeepenSearchTool>? _logger;

    public DeepenSearchTool(KbGraph kbGraph, IChatClient llm,
        ILogger<DeepenSearchTool>? logger = null)
    {
        _kbGraph = kbGraph ?? throw new ArgumentNullException(nameof(kbGraph));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _logger = logger;
    }

    [Description("迭代深化知识图谱搜索。当你需要深入探索某个主题时，先进行一次基础搜索，然后根据搜索结果自动生成追问并再次搜索，最后合并所有发现。适合研究性、探索性问题（如\"解释一下依赖注入的实现原理\"、\"详细分析这段代码的安全问题\"）。")]
    [ToolExample("详细分析ASP.NET Core中间件的工作原理")]
    [ToolExample("帮我深入调查这个性能问题的根因")]
    public async Task<string> DeepenSearchAsync(
        [Description("用户的原始问题，用于初始搜索和追问生成")] string query,
        [Description("迭代轮数（1-3），每轮会生成追问并搜索更深处")] int depth = 2,
        [Description("每轮搜索结果数")] int resultsPerRound = 5,
        CancellationToken ct = default)
    {
        depth = Math.Clamp(depth, 1, 3);
        resultsPerRound = Math.Clamp(resultsPerRound, 3, 10);

        _logger?.LogInformation("DeepenSearch: starting depth={D} for \"{Q}\"", depth, query);

        var allResults = new HashSet<string>();
        var currentQuery = query;
        var roundSummaries = new List<string>();

        for (int round = 0; round < depth; round++)
        {
            _logger?.LogInformation("DeepenSearch: round {R}/{D} query=\"{Q}\"",
                round + 1, depth, currentQuery);

            // Search current query
            var results = await _kbGraph.QueryAsync(currentQuery,
                topK: resultsPerRound, expandGraph: true, ct: ct).ConfigureAwait(false);

            if (results.Count == 0)
            {
                _logger?.LogInformation("DeepenSearch: no results at round {R}", round + 1);
                break;
            }

            var combined = string.Join("\n", results);
            var newItems = new List<string>();
            foreach (var r in results)
            {
                if (allResults.Add(r))
                    newItems.Add(r);
            }

            roundSummaries.Add($"""
                ## Round {round + 1}: "{currentQuery}" ({newItems.Count} new findings)
                {string.Join("\n", newItems.Select(r => "- " + r))}
                """);

            // Generate follow-up query for next round (unless last round)
            if (round < depth - 1)
            {
                currentQuery = await GenerateFollowUpAsync(
                    query, currentQuery, combined, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(currentQuery))
                    break;
            }
        }

        var output = $"""
            # Deepen Search Results: {query}

            Searched {depth} round(s), found {allResults.Count} items.

            {string.Join("\n\n", roundSummaries)}

            ## Consolidated View
            {string.Join("\n", allResults.Select(r => "- " + r))}
            """;

        _logger?.LogInformation("DeepenSearch: completed {R} rounds, {N} total items",
            roundSummaries.Count, allResults.Count);
        return output;
    }

    private async Task<string> GenerateFollowUpAsync(string originalQuery,
        string lastQuery, string searchResults, CancellationToken ct)
    {
        var prompt = $"""
            You are a research assistant doing iterative knowledge graph search.
            Your task: identify knowledge gaps and generate ONE follow-up query
            to deepen the search.

            Original question: {originalQuery}
            Last search query: {lastQuery}

            Search results from last round:
            {searchResults}

            Analyze what's still missing or worth exploring deeper.
            Return ONLY the follow-up query (1-2 sentences), no explanations.
            Focus on:
            - Missing details or incomplete explanations
            - Contradictory or unclear points
            - Related subtopics that could provide more context
            """;

        var resp = await _llm.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct)
            .ConfigureAwait(false);

        var followUp = resp.Text?.Trim();
        if (string.IsNullOrWhiteSpace(followUp) || followUp.Length < 5)
            return "";

        _logger?.LogInformation("DeepenSearch: follow-up: \"{F}\"", followUp);
        return followUp;
    }
}
