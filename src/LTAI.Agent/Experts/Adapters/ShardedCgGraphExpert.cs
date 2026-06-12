using System.Collections.Concurrent;
using LTAI.Agent.Vector;

namespace LTAI.Agent.Experts.Adapters;

/// <summary>
/// Auto-sharding code graph expert. On first query, discovers namespaces
/// from the underlying CgGraph and creates per-namespace sub-experts.
/// Delegates queries to matched sub-experts in parallel, merging results.
///
/// Replaces the monolithic CgGraphExpert: instead of searching all code
/// at once, only the relevant module's k-hop neighborhood is queried.
/// </summary>
public sealed class ShardedCgGraphExpert : IExpertModule
{
    private readonly CgGraph _cgGraph;
    private readonly ConcurrentDictionary<string, NamespacedCgGraphExpert> _shards = new();
    private volatile bool _discovered;

    public string ExpertId => "codegraph/sharded";
    public ExpertDomain Domain => ExpertDomain.CodeGraph;
    public string CapabilityDescription =>
        "代码图谱专家（自动分片）：按命名空间自动拆分为模块级子专家。" +
        "支持调用链追踪、类型继承分析、符号定义查询。适用场景：bug 定位/影响分析/API 用法。";
    public IReadOnlyList<string> KnowledgeTags => new[] { "code", "callgraph", "symbols", "dependency" };
    public float MinConfidence => 0.35f; // Code symbols: high precision, exact/suffix matches

    public ShardedCgGraphExpert(CgGraph cgGraph)
    {
        _cgGraph = cgGraph;
    }

    public async Task<ExpertResponse> QueryAsync(ExpertQuery query, CancellationToken ct = default)
    {
        await EnsureShardsAsync(ct).ConfigureAwait(false);

        if (_shards.IsEmpty)
        {
            // No shards discovered — fallback to full graph query
            var result = await _cgGraph.QueryAsync(query.Query, topK: query.MaxResults, ct: ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result))
                return NoAnswerResponse();
            return SingleResultResponse(result);
        }

        // Match query against known namespaces: if query mentions a namespace, use that shard.
        // Otherwise, query all shards in parallel and merge.
        var matchedShards = MatchShards(query);
        var tasks = matchedShards.Select(async s =>
        {
            var response = await s.QueryAsync(query, ct).ConfigureAwait(false);
            return (s.ExpertId, response);
        });

        var allResponses = await Task.WhenAll(tasks).ConfigureAwait(false);
        var answered = allResponses.Where(r => !r.response.NoAnswer).ToList();

        if (answered.Count == 0)
            return NoAnswerResponse();

        var content = string.Join("\n\n---\n\n", answered.Select(r =>
            $"### {r.ExpertId}\n{r.response.Content}"));
        var citations = answered.SelectMany(r => r.response.Citations).ToList();

        return new ExpertResponse(ExpertId, content,
            answered.Average(r => r.response.Confidence), citations,
            new ProvenanceInfo("cg.db", DateTime.UtcNow));
    }

    private async Task EnsureShardsAsync(CancellationToken ct)
    {
        if (_discovered) return;
        try
        {
            var namespaces = await _cgGraph.GetNamespacesAsync(ct).ConfigureAwait(false);
            foreach (var (ns, _) in namespaces)
            {
                _shards.TryAdd(ns, new NamespacedCgGraphExpert(_cgGraph, ns));
            }
            _discovered = true;
        }
        catch
        {
            _discovered = true; // Don't retry on failure
        }
    }

    private List<NamespacedCgGraphExpert> MatchShards(ExpertQuery query)
    {
        var text = query.Query;
        var matched = new List<NamespacedCgGraphExpert>();

        foreach (var (ns, shard) in _shards)
        {
            // Direct match: query contains "LTAI.Agent"
            if (text.Contains(ns, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(shard);
                continue;
            }

            // Short name match: "Agent" matches "LTAI.Agent"
            var shortName = ns.Split('.').Last();
            if (shortName.Length > 3 && text.Contains(shortName, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(shard);
                continue;
            }
        }

        // If no direct match, query top-2 shards by namespace length (prefer specific namespaces)
        if (matched.Count == 0 && _shards.Count > 0)
        {
            matched.AddRange(_shards.Values
                .OrderByDescending(s => s.ExpertId.Count(c => c == '.'))
                .Take(2));
        }

        return matched;
    }

    private ExpertResponse NoAnswerResponse() =>
        new(ExpertId, string.Empty, 0f, [],
            new ProvenanceInfo("cg.db", DateTime.UtcNow),
            NoAnswer: true, ClarifyQuestion: "代码图谱未构建或未找到匹配的代码。");

    private ExpertResponse SingleResultResponse(string result) =>
        new(ExpertId, result, 0.80f,
            new[] { new Citation("cg-0", "Code graph result", "cg.db", CitationType.Code) },
            new ProvenanceInfo("cg.db", DateTime.UtcNow));
}
