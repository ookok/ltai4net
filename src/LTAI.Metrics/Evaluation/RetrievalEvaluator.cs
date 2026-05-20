using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;

namespace LTAI.Metrics.Evaluation;

public sealed record RecallAtKResult
{
    public int K { get; init; }
    public double Recall { get; init; }
    public double Precision { get; init; }
    public double MRR { get; init; }
    public double NDCG { get; init; }
    public double MAP { get; init; }
    public int TotalQueries { get; init; }
    public int TotalRelevantDocs { get; init; }
    public int TotalRetrievedRelevant { get; init; }
}

public sealed record RetrievalReport
{
    public string EvaluatorVersion { get; init; } = "1.0";
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<RecallAtKResult> MetricsAtK { get; init; } = new();
    public Dictionary<string, RecallAtKResult> PerBucketMetrics { get; init; } = new();
    public double AverageLatencyMs { get; init; }
    public int TotalDocumentsInStore { get; init; }
    public int TotalQueriesEvaluated { get; init; }
}

public sealed class RetrievalEvaluator
{
    public RetrievalReport Evaluate(
        Func<string, List<KnowledgeSearchResult>> retrieveFn,
        List<(string query, List<string> relevantDocIds, string bucket)> queries,
        List<int> kValues)
    {
        var allResults = new List<SingleQueryResult>();
        var bucketResults = new Dictionary<string, List<SingleQueryResult>>();

        foreach (var (query, relevantIds, bucket) in queries)
        {
            var retrieved = retrieveFn(query);
            var retrievedIds = retrieved.Select(r => r.Id).ToList();

            var result = new SingleQueryResult
            {
                Query = query,
                Bucket = bucket,
                RelevantDocIds = relevantIds,
                RetrievedDocIds = retrievedIds,
                RetrievedScores = retrieved.Select(r => r.Score).ToList()
            };

            allResults.Add(result);

            if (!bucketResults.ContainsKey(bucket))
                bucketResults[bucket] = new();
            bucketResults[bucket].Add(result);
        }

        var metricsAtK = kValues.Select(k => ComputeMetricsAtK(allResults, k)).ToList();

        var perBucket = new Dictionary<string, RecallAtKResult>();
        foreach (var (bucket, results) in bucketResults)
        {
            perBucket[bucket] = ComputeMetricsAtK(results, kValues.Min());
        }

        return new RetrievalReport
        {
            MetricsAtK = metricsAtK,
            PerBucketMetrics = perBucket,
            TotalQueriesEvaluated = queries.Count,
            TotalDocumentsInStore = 0
        };
    }

    public RetrievalReport EvaluateWithRetrieval(
        AgenticRAG rag,
        List<(string query, List<string> relevantDocIds, string bucket)> queries,
        List<int> kValues)
    {
        return Evaluate(
            q => rag.Search(q, RAGMode.Iterative),
            queries,
            kValues);
    }

    private static RecallAtKResult ComputeMetricsAtK(List<SingleQueryResult> results, int k)
    {
        int totalQueries = results.Count;
        if (totalQueries == 0)
            return new RecallAtKResult { K = k, TotalQueries = 0 };

        double recallSum = 0, precisionSum = 0, mrrSum = 0, ndcgSum = 0, mapSum = 0;
        int totalRelevant = 0, totalFound = 0;

        foreach (var r in results)
        {
            var topK = r.RetrievedDocIds.Take(k).ToList();
            var founded = topK.Count(id => r.RelevantDocIds.Contains(id));

            totalRelevant += r.RelevantDocIds.Count;
            totalFound += founded;

            recallSum += r.RelevantDocIds.Count > 0
                ? (double)founded / r.RelevantDocIds.Count
                : 0;

            precisionSum += topK.Count > 0
                ? (double)founded / topK.Count
                : 0;

            mrrSum += ComputeMRR(r.RelevantDocIds, topK);

            ndcgSum += ComputeNDCG(r.RelevantDocIds, topK, r.RetrievedScores.Take(k).ToList());

            mapSum += ComputeAP(r.RelevantDocIds, topK);
        }

        return new RecallAtKResult
        {
            K = k,
            Recall = recallSum / totalQueries,
            Precision = precisionSum / totalQueries,
            MRR = mrrSum / totalQueries,
            NDCG = ndcgSum / totalQueries,
            MAP = mapSum / totalQueries,
            TotalQueries = totalQueries,
            TotalRelevantDocs = totalRelevant,
            TotalRetrievedRelevant = totalFound
        };
    }

    private static double ComputeMRR(List<string> relevant, List<string> retrieved)
    {
        for (int i = 0; i < retrieved.Count; i++)
        {
            if (relevant.Contains(retrieved[i]))
                return 1.0 / (i + 1);
        }
        return 0;
    }

    private static double ComputeNDCG(List<string> relevant, List<string> retrieved, List<double> scores)
    {
        var relevanceMap = new Dictionary<string, double>();
        foreach (var docId in relevant)
            relevanceMap[docId] = 1.0;

        var dcg = 0.0;
        for (int i = 0; i < retrieved.Count; i++)
        {
            var rel = relevanceMap.TryGetValue(retrieved[i], out var r) ? r : 0.0;
            var discount = Math.Log2(i + 2);
            dcg += rel / discount;
        }

        var idealRel = Enumerable.Repeat(1.0, Math.Min(relevant.Count, retrieved.Count)).ToList();
        var idcg = 0.0;
        for (int i = 0; i < idealRel.Count; i++)
            idcg += idealRel[i] / Math.Log2(i + 2);

        return idcg > 0 ? dcg / idcg : 0;
    }

    private static double ComputeAP(List<string> relevant, List<string> retrieved)
    {
        double sumPrecision = 0;
        int hits = 0;

        for (int i = 0; i < retrieved.Count; i++)
        {
            if (relevant.Contains(retrieved[i]))
            {
                hits++;
                sumPrecision += (double)hits / (i + 1);
            }
        }

        return relevant.Count > 0 ? sumPrecision / relevant.Count : 0;
    }

    private sealed class SingleQueryResult
    {
        public string Query { get; init; } = "";
        public string Bucket { get; init; } = "general";
        public List<string> RelevantDocIds { get; init; } = new();
        public List<string> RetrievedDocIds { get; init; } = new();
        public List<double> RetrievedScores { get; init; } = new();
    }
}
