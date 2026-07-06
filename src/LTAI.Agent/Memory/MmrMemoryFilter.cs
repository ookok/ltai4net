using LTAI.AI;
using LTAI.Agent.Vector;

namespace LTAI.Agent.Memory;

public sealed class MmrMemoryFilter
{
    private readonly EmbeddingClient _embedder;

    public MmrMemoryFilter(EmbeddingClient embedder)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    public IReadOnlyList<(PalaceStore.Drawer Drawer, double Score)> Filter(
        IReadOnlyList<(PalaceStore.Drawer Drawer, double Score)> candidates,
        string query,
        int maxTokens,
        double lambda = 0.7)
    {
        if (candidates.Count <= 1)
            return candidates;

        var selected = new List<(PalaceStore.Drawer Drawer, double Score, float[]? Emb)>();
        var remaining = new List<(PalaceStore.Drawer Drawer, double Score, float[]? Emb)>();
        var queryTokens = (query.Length / 4) + 1;
        var budgetChars = maxTokens * 4 - queryTokens;
        if (budgetChars <= 0) return candidates;

        foreach (var (d, s) in candidates)
        {
            var emb = d.Embedding;
            remaining.Add((d, s, emb));
        }

        while (remaining.Count > 0 && budgetChars > 0)
        {
            int bestIdx = -1;
            double bestScore = double.MinValue;

            for (int i = 0; i < remaining.Count; i++)
            {
                var (drawer, score, emb) = remaining[i];
                double maxRedundancy = 0;

                if (selected.Count > 0 && emb != null)
                {
                    foreach (var (_, _, selEmb) in selected)
                    {
                        if (selEmb != null)
                        {
                            var sim = VectorMath.CosineSimilarity(emb.AsSpan(), selEmb.AsSpan());
                            if (sim > maxRedundancy) maxRedundancy = sim;
                        }
                    }
                }

                var mmr = lambda * score - (1.0 - lambda) * maxRedundancy;
                if (mmr > bestScore)
                {
                    bestScore = mmr;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) break;

            var best = remaining[bestIdx];
            var entryChars = best.Drawer.Content.Length;
            if (entryChars <= budgetChars)
            {
                selected.Add(best);
                budgetChars -= entryChars;
            }
            remaining.RemoveAt(bestIdx);
        }

        if (selected.Count == 0 && candidates.Count > 0)
            return [candidates[0]];

        return selected.Select(s => (s.Drawer, s.Score)).ToList();
    }
}
