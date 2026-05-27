using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Governors;

public sealed record RetrievalResult
{
    public MemoryNode Node { get; init; } = null!;
    public double Score { get; init; }
    public string Route { get; init; } = ""; // "system1" or "system2"
    public string? Reasoning { get; init; }
}

public sealed record MemoryQueryResult
{
    public string Query { get; init; } = "";
    public IReadOnlyList<RetrievalResult> Results { get; init; } = Array.Empty<RetrievalResult>();
    public IReadOnlyList<string> System2ReasoningPath { get; init; } = Array.Empty<string>();
    public string DominantRoute { get; init; } = "";
    public long LatencyMs { get; init; }
    public bool FallbackUsed { get; init; }
}

public sealed class DualRouteRetriever
{
    private readonly MemoryGraph _graph;
    private readonly Func<string, float[]>? _embedder;
    private readonly Func<string, CancellationToken, Task<string>>? _l1Reasoner;
    private readonly ILogger<DualRouteRetriever> _logger;

    private int _retrievalCount;
    private int _system1Count;
    private int _system2Count;

    public int RetrievalCount => _retrievalCount;
    public int System1Count => _system1Count;
    public int System2Count => _system2Count;
    public double System2Ratio => _retrievalCount > 0 ? (double)_system2Count / _retrievalCount : 0;

    public DualRouteRetriever(
        MemoryGraph graph,
        Func<string, float[]>? embedder = null,
        Func<string, CancellationToken, Task<string>>? l1Reasoner = null,
        ILogger<DualRouteRetriever>? logger = null)
    {
        _graph = graph;
        _embedder = embedder;
        _l1Reasoner = l1Reasoner;
        _logger = logger ?? NullLogger<DualRouteRetriever>.Instance;
    }

    public async Task<MemoryQueryResult> QueryAsync(
        string query,
        int topK = 10,
        bool enableSystem2 = true,
        CancellationToken ct = default)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();
        Interlocked.Increment(ref _retrievalCount);

        var system1 = RunSystem1(query, topK * 2);
        Interlocked.Increment(ref _system1Count);

        List<RetrievalResult> system2 = new();
        var reasoningPath = new List<string>();
        bool fallbackUsed = false;

        if (enableSystem2 && _l1Reasoner != null && system1.Count > 0)
        {
            try
            {
                var sys2Context = BuildSystem1Context(system1.Take(5).ToList());

                var system2Prompt =
                    "You are a memory selection agent. Given:\n\n" +
                    $"Query: {query}\n\n" +
                    $"Candidate memories (from similarity search):\n{sys2Context}\n\n" +
                    "Determine which memories are RELEVANT and STRUCTURALLY important. " +
                    "Output JSON: {\"relevant_ids\": [\"id1\", \"id2\"], " +
                    "\"reasoning_path\": [\"domain X → concept Y → detail Z\"], " +
                    "\"fallback\": false}";

                var response = await _l1Reasoner(system2Prompt, ct).ConfigureAwait(false);
                reasoningPath.Add($"S2: {response[..Math.Min(response.Length, 200)]}");

                var fallback = response.Contains("\"fallback\": true", StringComparison.OrdinalIgnoreCase);

                system2 = FilterByL1Response(system1, response);
                if (system2.Count > 0)
                    Interlocked.Increment(ref _system2Count);

                fallbackUsed = fallback;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "System-2 retrieval failed, falling back to System-1");
                fallbackUsed = true;
            }
        }

        else if (enableSystem2 && _graph.NodeCount > 0)
        {
            var traverseNodes = _graph.TopDownTraverse(maxResults: topK);
            system2 = traverseNodes
                .Select(n => new RetrievalResult
                {
                    Node = n,
                    Score = n.Importance * 0.7 + n.AccessCount * 0.01,
                    Route = "system2",
                    Reasoning = $"Top-down traversal: level {n.LayerLevel}"
                })
                .ToList();
        }

        var merged = MergeResults(system1, system2, topK);
        sw.Stop();

        var dominant = system2.Count > system1.Count / 2 ? "system2" : "system1";

        return new MemoryQueryResult
        {
            Query = query,
            Results = merged,
            System2ReasoningPath = reasoningPath,
            DominantRoute = dominant,
            LatencyMs = sw.ElapsedMilliseconds,
            FallbackUsed = fallbackUsed
        };
    }

    private List<RetrievalResult> RunSystem1(string query, int topK)
    {
        if (_embedder == null || _graph.NodeCount == 0)
        {
            var allNodes = _graph.QueryByDomain("").ToList();
            if (allNodes.Count == 0)
                allNodes = _graph.QueryByLayer(0).ToList();
            if (allNodes.Count == 0)
            {
                for (int l = 3; l >= 0; l--)
                {
                    allNodes = _graph.QueryByLayer(l).ToList();
                    if (allNodes.Count > 0) break;
                }
            }
            return allNodes
                .OrderByDescending(n => n.Importance)
                .Take(topK)
                .Select(n => new RetrievalResult { Node = n, Score = n.Importance, Route = "system1" })
                .ToList();
        }

        var queryEmb = _embedder(query);
        var layerNodes = _graph.QueryByLayer(0).ToList();
        if (layerNodes.Count == 0)
        {
            for (int l = 1; l <= 3; l++)
            {
                layerNodes = _graph.QueryByLayer(l).ToList();
                if (layerNodes.Count > 0) break;
            }
        }

        return layerNodes
            .Where(n => n.Embedding != null)
            .Select(n => new
            {
                Node = n,
                Score = CosineSim(queryEmb, n.Embedding!) * 0.6 + n.Importance * 0.3 + Math.Min(n.AccessCount * 0.01, 0.1)
            })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => new RetrievalResult
            {
                Node = x.Node,
                Score = x.Score,
                Route = "system1",
                Reasoning = null
            })
            .ToList();
    }

    private static List<RetrievalResult> FilterByL1Response(
        List<RetrievalResult> candidates, string l1Response)
    {
        var results = new List<RetrievalResult>();

        foreach (var candidate in candidates)
        {
            if (l1Response.Contains(candidate.Node.Id, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new RetrievalResult
                {
                    Node = candidate.Node,
                    Score = candidate.Score + 0.15,
                    Route = "system2",
                    Reasoning = "L1-selected via global reasoning"
                });
            }
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    private static List<RetrievalResult> MergeResults(
        List<RetrievalResult> s1, List<RetrievalResult> s2, int topK)
    {
        var merged = new Dictionary<string, RetrievalResult>();

        foreach (var r in s1)
        {
            merged[r.Node.Id] = r;
        }

        foreach (var r in s2)
        {
            if (merged.TryGetValue(r.Node.Id, out var existing))
            {
                merged[r.Node.Id] = new RetrievalResult
                {
                    Node = r.Node,
                    Score = Math.Max(existing.Score, r.Score),
                    Route = "system1+system2",
                    Reasoning = r.Reasoning ?? existing.Reasoning
                };
            }
            else
            {
                merged[r.Node.Id] = r;
            }
        }

        return merged.Values
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    private static string BuildSystem1Context(List<RetrievalResult> candidates)
    {
        var lines = new List<string>();
        foreach (var c in candidates)
        {
            lines.Add(
                $"- [{c.Node.Id}] ({c.Node.Domain}, L{c.Node.LayerLevel}, score={c.Score:F2}): {c.Node.Summary[..Math.Min(c.Node.Summary.Length, 80)]}");
        }
        return string.Join("\n", lines);
    }

    private static double CosineSim(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
