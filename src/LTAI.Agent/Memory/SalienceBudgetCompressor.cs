using System.Text;

namespace LTAI.Agent.Memory;

public sealed class SalienceBudgetCompressor
{
    private readonly int _maxTokens;

    public SalienceBudgetCompressor(int maxTokens = 4096)
    {
        _maxTokens = maxTokens;
    }

    public string Compress(List<TraversalResult> results, string query)
    {
        if (results.Count == 0) return "";

        var scored = results
            .Select(r => new
            {
                r.NodeId,
                r.Content,
                r.Score,
                Salience = ComputeSalience(r.Content, query, r.Score, r.Depth)
            })
            .OrderByDescending(x => x.Salience)
            .ToList();

        var sb = new StringBuilder();
        var tokens = 0;
        var first = true;

        foreach (var item in scored)
        {
            var estimatedTokens = item.Content.Length / 2 + 10;
            if (tokens + estimatedTokens > _maxTokens)
            {
                var remaining = _maxTokens - tokens;
                if (remaining > 20)
                {
                    var truncated = item.Content[..Math.Min(remaining * 2, item.Content.Length)];
                    sb.AppendLine($"  - {truncated}...[压缩]");
                }

                sb.AppendLine($"  ...还有 {scored.Count - scored.IndexOf(item)} 个低分节点已省略");
                break;
            }

            if (!first) sb.AppendLine();
            first = false;

            if (item.Salience > 0.7)
                sb.AppendLine($"  【高相关】{item.Content}");
            else
                sb.AppendLine($"  - {item.Content}");

            tokens += estimatedTokens;
        }

        return sb.ToString();
    }

    private static double ComputeSalience(string content, string query, double score, int depth)
    {
        var relevanceBoost = 0.0;
        if (!string.IsNullOrEmpty(query) && !string.IsNullOrEmpty(content))
        {
            var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matchCount = queryTerms.Count(q =>
                content.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (queryTerms.Length > 0)
                relevanceBoost = (double)matchCount / queryTerms.Length * 0.3;
        }

        var depthPenalty = depth > 2 ? (depth - 2) * 0.1 : 0.0;
        return score * 0.6 + relevanceBoost - depthPenalty;
    }
}
