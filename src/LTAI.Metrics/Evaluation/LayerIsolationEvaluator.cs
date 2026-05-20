using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;

namespace LTAI.Metrics.Evaluation;

public enum RAGLayer
{
    Chunking,
    Retrieval,
    Reranking,
    Generation
}

public sealed record LayerEvalResult
{
    public RAGLayer Layer { get; init; }
    public double Score { get; init; }
    public List<string> PassedChecks { get; init; } = new();
    public List<string> FailedChecks { get; init; } = new();
    public Dictionary<string, double> Metrics { get; init; } = new();
    public string RootCauseSuggestion { get; init; } = "";
}

public sealed record IsolationReport
{
    public string Query { get; init; } = "";
    public Dictionary<RAGLayer, LayerEvalResult> LayerResults { get; init; } = new();
    public RAGLayer IdentifiedRootLayer { get; init; }
    public string Summary { get; init; } = "";
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class LayerIsolationEvaluator
{
    private readonly DocumentStore _docStore;
    private readonly Reranker _reranker;

    public LayerIsolationEvaluator(DocumentStore docStore, Reranker reranker)
    {
        _docStore = docStore;
        _reranker = reranker;
    }

    public IsolationReport Evaluate(string query, string originalDocContent,
        List<string> expectedChunkContents, string expectedAnswerFragment,
        int topK = 5)
    {
        var results = new Dictionary<RAGLayer, LayerEvalResult>();
        var rootLayer = RAGLayer.Chunking;

        var chunkResult = EvaluateChunking(originalDocContent, expectedChunkContents);
        results[RAGLayer.Chunking] = chunkResult;

        if (chunkResult.Score < 0.5)
        {
            rootLayer = RAGLayer.Chunking;
            results[RAGLayer.Retrieval] = new LayerEvalResult
            {
                Layer = RAGLayer.Retrieval,
                Score = 0,
                FailedChecks = new() { "Skipped: Chunking layer failed" },
                RootCauseSuggestion = "Fix chunking before evaluating retrieval"
            };
            results[RAGLayer.Reranking] = new LayerEvalResult
            {
                Layer = RAGLayer.Reranking,
                Score = 0,
                FailedChecks = new() { "Skipped: Chunking layer failed" }
            };
            results[RAGLayer.Generation] = new LayerEvalResult
            {
                Layer = RAGLayer.Generation,
                Score = 0,
                FailedChecks = new() { "Skipped: Cannot generate without valid chunks" }
            };

            return BuildReport(query, results, rootLayer);
        }

        var retrievalResults = _docStore.SearchFts(query, "general", 20);
        var retrievedTexts = retrievalResults.Select(r => r.Content).ToList();

        var retrievalResult = EvaluateRetrieval(retrievedTexts, expectedChunkContents, topK);
        results[RAGLayer.Retrieval] = retrievalResult;

        if (retrievalResult.Score < 0.5)
        {
            rootLayer = RAGLayer.Retrieval;
            results[RAGLayer.Reranking] = new LayerEvalResult
            {
                Layer = RAGLayer.Reranking,
                Score = 0,
                FailedChecks = new() { "Skipped: Retrieval layer failed" },
                RootCauseSuggestion = "Fix embedding/BM25/hybrid search before evaluating reranker"
            };
            results[RAGLayer.Generation] = new LayerEvalResult
            {
                Layer = RAGLayer.Generation,
                Score = 0,
                FailedChecks = new() { "Skipped: No relevant documents retrieved" }
            };

            return BuildReport(query, results, rootLayer);
        }

        var rerankInput = retrievalResults.Select(r => new Dictionary<string, object>
        {
            ["id"] = r.Id,
            ["text"] = r.Content,
            ["score"] = r.Score,
            ["source"] = r.Source
        }).ToList();

        var rerankResult = EvaluateReranking(rerankInput, query, expectedChunkContents, topK);
        results[RAGLayer.Reranking] = rerankResult;

        if (rerankResult.Score < 0.3)
        {
            rootLayer = RAGLayer.Reranking;
            results[RAGLayer.Generation] = new LayerEvalResult
            {
                Layer = RAGLayer.Generation,
                Score = 0,
                FailedChecks = new() { "Skipped: Reranking quality too low" }
            };

            return BuildReport(query, results, rootLayer);
        }

        var topChunks = rerankResult.Metrics.TryGetValue("top_chunks", out var tc)
            ? retrievalResults.Take((int)Math.Min(tc, topK)).Select(r => r.Content).ToList()
            : retrievedTexts.Take(topK).ToList();

        var genResult = EvaluateGeneration(query, topChunks, expectedAnswerFragment);
        results[RAGLayer.Generation] = genResult;

        if (genResult.Score < 0.5)
            rootLayer = RAGLayer.Generation;

        return BuildReport(query, results, rootLayer);
    }

    private LayerEvalResult EvaluateChunking(string originalDoc, List<string> expectedChunks)
    {
        var passed = new List<string>();
        var failed = new List<string>();

        var chunks = DocumentStore.SplitChunks(originalDoc);
        int expectedCount = expectedChunks.Count;
        int foundChunks = 0;

        foreach (var expected in expectedChunks)
        {
            var match = chunks.FirstOrDefault(c =>
                c.Text.Contains(expected[..Math.Min(expected.Length, 20)]));
            if (match != null)
            {
                foundChunks++;
                passed.Add($"Found expected chunk: {expected[..Math.Min(expected.Length, 30)]}...");
            }
            else
            {
                failed.Add($"Missing expected chunk: {expected[..Math.Min(expected.Length, 30)]}...");
            }
        }

        double coverage = expectedCount > 0 ? (double)foundChunks / expectedCount : 0;

        var fragmentChecks = new List<(string fragment, bool intact)>();
        foreach (var expected in expectedChunks)
        {
            var foundInOne = chunks.Any(c => c.Text.Contains(expected));
            fragmentChecks.Add((expected[..Math.Min(expected.Length, 40)], foundInOne));
            if (!foundInOne)
            {
                var foundAcross = expected.Length > 30 &&
                    chunks.Any(c => c.Text.Contains(expected[..Math.Min(expected.Length, expected.Length / 2)]));
                if (foundAcross)
                    failed.Add($"Answer split across chunks: {expected[..Math.Min(expected.Length, 40)]}...");
            }
        }

        if (coverage < 0.5 || failed.Any(f => f.Contains("split")))
        {
            var rootSugg = coverage < 0.5
                ? "Chunk size too large or content not stored. Try smaller chunks or verify document indexing."
                : "Answer fragments split across chunks. Use semantic segmentation or increase overlap.";
            return new LayerEvalResult
            {
                Layer = RAGLayer.Chunking,
                Score = coverage,
                PassedChecks = passed,
                FailedChecks = failed,
                Metrics = new() { ["chunk_coverage"] = coverage, ["total_chunks"] = chunks.Count },
                RootCauseSuggestion = rootSugg
            };
        }

        return new LayerEvalResult
        {
            Layer = RAGLayer.Chunking,
            Score = coverage,
            PassedChecks = passed,
            FailedChecks = failed,
            Metrics = new() { ["chunk_coverage"] = coverage, ["total_chunks"] = chunks.Count }
        };
    }

    private LayerEvalResult EvaluateRetrieval(List<string> retrievedTexts,
        List<string> expectedChunks, int topK)
    {
        var topN = retrievedTexts.Take(topK).ToList();
        int found = 0;

        foreach (var expected in expectedChunks)
        {
            var key = expected[..Math.Min(expected.Length, 20)];
            if (topN.Any(t => t.Contains(key)))
                found++;
        }

        double recallAtK = expectedChunks.Count > 0
            ? (double)found / expectedChunks.Count : 0;

        double contextPrecision = topN.Count > 0
            ? (double)topN.Count(t =>
                expectedChunks.Any(e => t.Contains(e[..Math.Min(e.Length, 20)])))
                / topN.Count : 0;

        var passed = new List<string>();
        var failed = new List<string>();

        if (recallAtK >= 0.5)
            passed.Add($"Recall@{topK}={recallAtK:F2} >= 0.5");
        else
            failed.Add($"Recall@{topK}={recallAtK:F2} < 0.5");

        if (contextPrecision >= 0.4)
            passed.Add($"ContextPrecision={contextPrecision:F2} >= 0.4");
        else
            failed.Add($"ContextPrecision={contextPrecision:F2} < 0.4 - noise dominating results");

        var rootSugg = "";
        if (recallAtK < 0.3)
            rootSugg = "Severe recall failure. Check: embedding model quality, BM25 integration, document indexing freshness, query-rewriting for expression gap.";
        else if (contextPrecision < 0.3)
            rootSugg = "Recall OK but precision low. Add reranker or increase hybrid search RRF weight on BM25.";

        return new LayerEvalResult
        {
            Layer = RAGLayer.Retrieval,
            Score = (recallAtK + contextPrecision) / 2,
            PassedChecks = passed,
            FailedChecks = failed,
            Metrics = new()
            {
                [$"recall_at_{topK}"] = recallAtK,
                ["context_precision"] = contextPrecision,
                ["total_retrieved"] = retrievedTexts.Count
            },
            RootCauseSuggestion = rootSugg
        };
    }

    private LayerEvalResult EvaluateReranking(List<Dictionary<string, object>> candidates,
        string query, List<string> expectedChunks, int topK)
    {
        if (candidates.Count == 0)
        {
            return new LayerEvalResult
            {
                Layer = RAGLayer.Reranking,
                Score = 0,
                FailedChecks = new() { "No candidates to rerank" }
            };
        }

        var reranked = _reranker.Rerank(candidates, query, topK);
        var rankedTexts = reranked.RankedDocs.Select(r => r.Text).ToList();

        var expectedFirst = expectedChunks.Count > 0 ? expectedChunks[0][..Math.Min(expectedChunks[0].Length, 20)] : "";
        int firstMatchIndex = -1;
        for (int i = 0; i < rankedTexts.Count; i++)
        {
            if (rankedTexts[i].Contains(expectedFirst))
            {
                firstMatchIndex = i;
                break;
            }
        }

        double mrr = firstMatchIndex >= 0 ? 1.0 / (firstMatchIndex + 1) : 0;

        int foundInTopK = rankedTexts.Take(topK).Count(t =>
            expectedChunks.Any(e => t.Contains(e[..Math.Min(e.Length, 20)])));

        double precisionAtK = topK > 0 ? (double)foundInTopK / topK : 0;

        var passed = new List<string>();
        var failed = new List<string>();

        if (mrr >= 0.5)
            passed.Add($"MRR={mrr:F2} >= 0.5 - correct chunk ranked high");
        else if (mrr > 0)
            failed.Add($"MRR={mrr:F2} < 0.5 - correct chunk ranked low (position {firstMatchIndex + 1})");
        else
            failed.Add("MRR=0 - correct chunk not in reranked results at all");

        if (precisionAtK >= 0.6)
            passed.Add($"Precision@{topK}={precisionAtK:F2} >= 0.6");
        else
            failed.Add($"Precision@{topK}={precisionAtK:F2} < 0.6");

        var rootSugg = mrr < 0.2
            ? "Reranker failing to prioritize correct chunks. Verify reranker signal weights, or consider cross-encoder model as second-stage reranker."
            : "";

        return new LayerEvalResult
        {
            Layer = RAGLayer.Reranking,
            Score = (mrr + precisionAtK) / 2,
            PassedChecks = passed,
            FailedChecks = failed,
            Metrics = new()
            {
                ["mrr"] = mrr,
                ["precision_at_k"] = precisionAtK,
                ["first_match_index"] = firstMatchIndex,
                ["top_chunks"] = topK
            },
            RootCauseSuggestion = rootSugg
        };
    }

    private static LayerEvalResult EvaluateGeneration(string query,
        List<string> contextChunks, string expectedFragment)
    {
        if (contextChunks.Count == 0)
        {
            return new LayerEvalResult
            {
                Layer = RAGLayer.Generation,
                Score = 0,
                FailedChecks = new() { "No context to generate from" }
            };
        }

        var combinedContext = string.Join("\n", contextChunks);
        var contextContainsAnswer = combinedContext.Contains(expectedFragment);

        bool contextSufficient = combinedContext.Length >= 100;

        var passed = new List<string>();
        var failed = new List<string>();

        if (contextContainsAnswer)
            passed.Add("Expected answer fragment found in context");
        else
            failed.Add($"Expected fragment '{expectedFragment[..Math.Min(expectedFragment.Length, 30)]}...' not in context");

        if (contextSufficient)
            passed.Add($"Context size sufficient ({combinedContext.Length} chars)");
        else
            failed.Add($"Context too small ({combinedContext.Length} chars)");

        double score = (contextContainsAnswer ? 0.6 : 0) + (contextSufficient ? 0.4 : 0);

        var rootSugg = !contextContainsAnswer
            ? "Answer fragment missing from context. Check if earlier layers (chunking/retrieval/reranking) have the issue. If so, this is not a generation problem."
            : !contextSufficient
                ? "Context insufficient. Increase topK in retrieval or context window size."
                : "";

        return new LayerEvalResult
        {
            Layer = RAGLayer.Generation,
            Score = score,
            PassedChecks = passed,
            FailedChecks = failed,
            Metrics = new()
            {
                ["context_length"] = combinedContext.Length,
                ["contains_answer"] = contextContainsAnswer ? 1 : 0
            },
            RootCauseSuggestion = rootSugg
        };
    }

    private static IsolationReport BuildReport(string query,
        Dictionary<RAGLayer, LayerEvalResult> results, RAGLayer rootLayer)
    {
        var layerDescriptions = new Dictionary<RAGLayer, string>
        {
            [RAGLayer.Chunking] = "chunk 切分不合理，答案被拆散到多个 chunk 中",
            [RAGLayer.Retrieval] = "检索层未召回正确文档 (embedding 不适配、精确词盲区、查询表达鸿沟)",
            [RAGLayer.Reranking] = "正确文档已被召回但排序靠后，超出上下文窗口",
            [RAGLayer.Generation] = "上下文已包含答案但模型生成不完整或编造细节"
        };

        var summary = $"Root cause: {layerDescriptions.GetValueOrDefault(rootLayer, "Unknown")}. "
            + $"Score chain: Chunk={results[RAGLayer.Chunking].Score:F2} → "
            + $"Retrieve={results[RAGLayer.Retrieval].Score:F2} → "
            + $"Rerank={results[RAGLayer.Reranking].Score:F2} → "
            + $"Generate={results[RAGLayer.Generation].Score:F2}";

        return new IsolationReport
        {
            Query = query,
            LayerResults = results,
            IdentifiedRootLayer = rootLayer,
            Summary = summary
        };
    }
}
