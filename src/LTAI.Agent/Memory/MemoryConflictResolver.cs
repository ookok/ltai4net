using System.Text.RegularExpressions;

namespace LTAI.Agent.Memory;

public partial class MemoryConflictResolver
{
    public IReadOnlyList<(PalaceStore.Drawer Drawer, double Score)> Resolve(
        IReadOnlyList<(PalaceStore.Drawer Drawer, double Score)> candidates,
        double relevanceWeight = 0.5,
        double temporalWeight = 0.3,
        double sourceWeight = 0.2)
    {
        if (candidates.Count < 2) return candidates;

        var groups = GroupBySharedEntities(candidates);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new List<(PalaceStore.Drawer Drawer, double Score)>();

        foreach (var group in groups)
        {
            if (group.Count == 1)
            {
                result.Add(group[0]);
                continue;
            }

            var best = group
                .Select(g =>
                {
                    var age = Math.Max(0, now - g.Drawer.CreatedAt);
                    var freshness = Math.Exp(-age / 300_000.0);
                    var room = g.Drawer.Room ?? "";
                    var sourceConf = room.Contains("reflection", StringComparison.OrdinalIgnoreCase) ? 0.6
                        : room.EndsWith(".entity", StringComparison.OrdinalIgnoreCase) ? 0.8
                        : 1.0;
                    var arbScore = relevanceWeight * g.Score
                        + temporalWeight * freshness
                        + sourceWeight * sourceConf;
                    return (g.Drawer, g.Score, arbScore);
                })
                .OrderByDescending(x => x.arbScore)
                .First();

            result.Add((best.Drawer, best.Score));
        }

        return result.OrderByDescending(r => r.Score).ToList();
    }

    private List<List<(PalaceStore.Drawer Drawer, double Score)>> GroupBySharedEntities(
        IReadOnlyList<(PalaceStore.Drawer Drawer, double Score)> candidates)
    {
        var groups = new List<List<(PalaceStore.Drawer Drawer, double Score)>>();
        var assigned = new HashSet<string>();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (assigned.Contains(candidates[i].Drawer.DrawerId)) continue;

            var group = new List<(PalaceStore.Drawer Drawer, double Score)> { candidates[i] };
            assigned.Add(candidates[i].Drawer.DrawerId);
            var iEntities = ExtractEntities(candidates[i].Drawer.Content);

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (assigned.Contains(candidates[j].Drawer.DrawerId)) continue;

                var jEntities = ExtractEntities(candidates[j].Drawer.Content);
                if (SharesEntity(iEntities, jEntities))
                {
                    group.Add(candidates[j]);
                    assigned.Add(candidates[j].Drawer.DrawerId);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static List<string> ExtractEntities(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new List<string>();
        var entities = new List<string>();
        try
        {
            foreach (Match m in EntityPattern().Matches(content))
            {
                var e = m.Groups[1].Value.Trim();
                if (e.Length >= 3 && e.Length <= 60)
                    entities.Add(e);
            }
        }
        catch (RegexMatchTimeoutException) { }
        return entities;
    }

    private static bool SharesEntity(List<string> a, List<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return false;
        foreach (var ea in a)
            foreach (var eb in b)
                if (string.Equals(ea, eb, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    [GeneratedRegex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b", RegexOptions.Compiled, 500)]
    private static partial Regex EntityPattern();
}
