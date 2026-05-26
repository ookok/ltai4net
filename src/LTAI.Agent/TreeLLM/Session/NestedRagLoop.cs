using System.Diagnostics;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Session;

public sealed record NestedRagResult
{
    public string FinalAnswer { get; init; } = "";
    public List<SubQueryResult> SubResults { get; init; } = new();
    public int NestedRounds { get; init; }
    public int TotalSources { get; init; }
    public double ElapsedMs { get; init; }
    public double OverallConfidence { get; init; }
    public List<string> ReflectionNotes { get; init; } = new();
}

public sealed record SubQueryResult
{
    public string SubQuery { get; init; } = "";
    public List<KnowledgeSearchResult> Sources { get; init; } = new();
    public string Answer { get; init; } = "";
    public double Confidence { get; init; }
    public int Round { get; init; }
}

public sealed class NestedRagLoop
{
    private readonly AgenticRAG _agenticRAG;
    private readonly Prompting.PromptBuilder _promptBuilder;
    private readonly ILogger<NestedRagLoop>? _logger;

    public NestedRagLoop(
        AgenticRAG agenticRAG,
        Prompting.PromptBuilder? promptBuilder = null,
        ILogger<NestedRagLoop>? logger = null)
    {
        _agenticRAG = agenticRAG;
        _promptBuilder = promptBuilder ?? new Prompting.PromptBuilder();
        _logger = logger;
    }

    public async Task<NestedRagResult> SearchAsync(
        string query, string domain = "general",
        int maxNestedRounds = 3, int subQueriesPerRound = 4)
    {
        var sw = Stopwatch.StartNew();
        var allSubResults = new List<SubQueryResult>();
        var reflectionNotes = new List<string>();

        var subQuestions = DecomposeToSubQueries(query, subQueriesPerRound);

        for (int nestedRound = 0; nestedRound < maxNestedRounds; nestedRound++)
        {
            var roundResults = new List<SubQueryResult>();

            foreach (var subQ in subQuestions)
            {
                var sources = await _agenticRAG.SearchAsync(subQ, RAGMode.Iterative, domain: domain).ConfigureAwait(false);
                var confidence = sources.Count > 0
                    ? Math.Min(0.95, sources.Average(s => s.Score) * 2.0)
                    : 0.1;

                roundResults.Add(new SubQueryResult
                {
                    SubQuery = subQ,
                    Sources = sources,
                    Answer = string.Join("\n", sources.Select(s => s.Content[..Math.Min(500, s.Content.Length)])),
                    Confidence = confidence,
                    Round = nestedRound + 1
                });
            }

            allSubResults.AddRange(roundResults);

            var reflection = ReflectOnResults(query, roundResults, combinedContext: BuildCombinedContext(allSubResults));
            reflectionNotes.Add($"Round {nestedRound + 1}: {reflection.Completeness:F2} complete, needMore={reflection.NeedMore}");

            if (!reflection.NeedMore)
                break;

            subQuestions = reflection.RefinedQuestions.Count > 0
                ? reflection.RefinedQuestions.Take(subQueriesPerRound).ToList()
                : DecomposeToSubQueries(query, subQueriesPerRound);
        }

        var finalAnswer = SynthesizeFinalAnswer(query, allSubResults);
        var overallConfidence = allSubResults.Count > 0
            ? allSubResults.Average(r => r.Confidence)
            : 0;

        _logger?.LogInformation(
            "NestedRAG: {SubCount} sub-queries over {Rounds} rounds, {Sources} sources, {Ms}ms",
            allSubResults.Count, maxNestedRounds,
            allSubResults.Sum(r => r.Sources.Count), sw.ElapsedMilliseconds);

        return new NestedRagResult
        {
            FinalAnswer = finalAnswer,
            SubResults = allSubResults,
            NestedRounds = allSubResults.GroupBy(r => r.Round).Count(),
            TotalSources = allSubResults.Sum(r => r.Sources.Count),
            ElapsedMs = sw.ElapsedMilliseconds,
            OverallConfidence = overallConfidence,
            ReflectionNotes = reflectionNotes
        };
    }

    private List<string> DecomposeToSubQueries(string query, int count)
    {
        var decomposer = new QueryDecomposer(Microsoft.Extensions.Logging.Abstractions.NullLogger<QueryDecomposer>.Instance);
        var decomposed = decomposer.Decompose(query);

        if (decomposed.SubQueries.Count >= 2)
            return decomposed.SubQueries.Select(sq => sq.Query).Take(count).ToList();

        return GenerateSubQuestions(query, count);
    }

    private static List<string> GenerateSubQuestions(string query, int count)
    {
        var connectors = new[] { "背景和前提是什么", "关键细节和证据有哪些",
            "有哪些不同角度或方面", "相关案例或先例有哪些", "结论或建议是什么",
            "有什么限制条件或例外", "与其他概念的关系是什么", "如何验证或确认" };

        var subQuestions = new List<string>();
        for (int i = 0; i < Math.Min(count, connectors.Length); i++)
        {
            subQuestions.Add($"[SubQ{i + 1}] {connectors[i]}: {query}");
        }
        return subQuestions;
    }

    private sealed record ReflectionResult(
        bool NeedMore, double Completeness, List<string> RefinedQuestions);

    private static ReflectionResult ReflectOnResults(
        string originalQuery,
        List<SubQueryResult> results,
        string combinedContext)
    {
        var avgConfidence = results.Count > 0 ? results.Average(r => r.Confidence) : 0;
        var totalSources = results.Sum(r => r.Sources.Count);

        var completeness = Math.Min(1.0,
            avgConfidence * 0.5
            + (double)totalSources / Math.Max(1, results.Count * 5) * 0.3
            + (results.Count >= 3 ? 0.2 : 0.1));

        var needMore = completeness < 0.7 && results.Count < 20;

        var lowConfSubQs = results
            .Where(r => r.Confidence < 0.4)
            .Select(r => r.SubQuery)
            .ToList();

        var refinedQuestions = needMore && lowConfSubQs.Count > 0
            ? lowConfSubQs.Select(q => $"补充证据: {q} (置信度不足)").ToList()
            : new List<string>();

        return new ReflectionResult(needMore, completeness, refinedQuestions);
    }

    private static string BuildCombinedContext(List<SubQueryResult> allSubResults)
    {
        var parts = new List<string>();
        foreach (var r in allSubResults)
        {
            parts.Add($"### {r.SubQuery}");
            parts.Add(r.Answer);
        }
        return string.Join("\n\n", parts);
    }

    private static string SynthesizeFinalAnswer(string query, List<SubQueryResult> allSubResults)
    {
        if (allSubResults.Count == 0) return "(无结果)";

        var highConfidence = allSubResults
            .Where(r => r.Confidence >= 0.5)
            .OrderByDescending(r => r.Confidence)
            .Take(5)
            .ToList();

        var parts = new List<string>
        {
            $"## 综合回答 (基于 {allSubResults.Count} 个子问题 / {allSubResults.Sum(r => r.Sources.Count)} 个来源)\n"
        };

        foreach (var r in highConfidence)
        {
            parts.Add($"### {r.SubQuery} (相关度: {r.Confidence:F2})");
            parts.Add(r.Answer.Length > 600 ? r.Answer[..600] + "..." : r.Answer);
            parts.Add("");
        }

        return string.Join("\n", parts);
    }
}
