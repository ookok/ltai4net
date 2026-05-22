namespace LTAI.Knowledge.Core;

public sealed record CrossReference(string SourceDocId, string TargetDocId, string Anchor, double Confidence);
public sealed record DocumentConflict(string Doc1, string Doc2, string Topic, string Detail, double Severity);

public sealed class MultiDocFusionEngine
{
    private static readonly Lazy<MultiDocFusionEngine> _instance = new(() => new MultiDocFusionEngine());
    public static MultiDocFusionEngine Instance => _instance.Value;

    private MultiDocFusionEngine() { }

    public async Task<FusionResult> FuseAsync(List<(string id, string content, DateTime? timestamp)> docs, string query)
    {
        var refs = CrossReference(docs);
        var conflicts = DetectConflicts(docs);
        var complementary = FindComplementary(docs, query);
        var synthesized = await SynthesizeAsync(docs, refs, conflicts, query);

        return new FusionResult
        {
            SynthesizedContent = synthesized,
            CrossReferences = refs,
            Conflicts = conflicts,
            ComplementaryPairs = complementary,
            DocCount = docs.Count,
            TemporalOrder = InferTemporalOrder(docs)
        };
    }

    public List<CrossReference> CrossReference(List<(string id, string content, DateTime? timestamp)> docs)
    {
        var refs = new List<CrossReference>();
        for (var i = 0; i < docs.Count; i++)
        {
            for (var j = i + 1; j < docs.Count; j++)
            {
                var words_i = new HashSet<string>(docs[i].content.ToLower().Split(' ').Where(w => w.Length > 3));
                var words_j = new HashSet<string>(docs[j].content.ToLower().Split(' ').Where(w => w.Length > 3));
                var overlap = words_i.Intersect(words_j).ToList();
                if (overlap.Count >= 5)
                {
                    var anchor = overlap.Take(3).FirstOrDefault() ?? "";
                    refs.Add(new CrossReference(docs[i].id, docs[j].id, anchor,
                        Math.Min(1.0, overlap.Count / 10.0)));
                }
            }
        }
        return refs;
    }

    public List<DocumentConflict> DetectConflicts(List<(string id, string content, DateTime? timestamp)> docs)
    {
        var conflicts = new List<DocumentConflict>();
        for (var i = 0; i < docs.Count; i++)
        {
            for (var j = i + 1; j < docs.Count; j++)
            {
                var hasNo = docs[i].content.Contains("不") && docs[j].content.Contains("是");
                var hasOpposite = (docs[i].content.Contains("高") && docs[j].content.Contains("低")) ||
                                  (docs[i].content.Contains("大") && docs[j].content.Contains("小"));
                if (hasNo || hasOpposite)
                {
                    conflicts.Add(new DocumentConflict(docs[i].id, docs[j].id, "数据矛盾",
                        $"{docs[i].id}与{docs[j].id}内容存在矛盾", 0.5));
                }
            }
        }
        return conflicts;
    }

    public List<(string doc1, string doc2)> FindComplementary(List<(string id, string content, DateTime? timestamp)> docs, string query)
    {
        var pairs = new List<(string, string)>();
        var qw = new HashSet<string>(query.ToLower().Split(' ').Where(w => w.Length > 2));
        for (var i = 0; i < docs.Count; i++)
        {
            for (var j = i + 1; j < docs.Count; j++)
            {
                var score_i = qw.Count(w => docs[i].content.ToLower().Contains(w));
                var score_j = qw.Count(w => docs[j].content.ToLower().Contains(w));
                if (score_i > 0 && score_j > 0 && score_i != score_j)
                    pairs.Add((docs[i].id, docs[j].id));
            }
        }
        return pairs.Take(5).ToList();
    }

    private Task<string> SynthesizeAsync(List<(string id, string content, DateTime? timestamp)> docs,
        List<CrossReference> refs, List<DocumentConflict> conflicts, string query)
    {
        var parts = new List<string>
        {
            $"## 多文档融合结果 (基于 {docs.Count} 篇文档)",
            $"查询: {query}",
            ""
        };

        if (refs.Count > 0)
            parts.Add($"**交叉引用**: {refs.Count} 处关联发现");

        if (conflicts.Count > 0)
            parts.Add($"**冲突**: {conflicts.Count} 处矛盾 (已标注)");

        parts.Add("\n## 综合摘要");
        foreach (var d in docs.Take(3))
            parts.Add($"- [{d.id}] {d.content[..Math.Min(120, d.content.Length)]}...");

        return Task.FromResult(string.Join("\n", parts));
    }

    private List<string> InferTemporalOrder(List<(string id, string content, DateTime? timestamp)> docs)
    {
        return docs.OrderBy(d => d.timestamp ?? DateTime.MaxValue).Select(d => d.id).ToList();
    }
}

public sealed class FusionResult
{
    public string SynthesizedContent { get; set; } = "";
    public List<CrossReference> CrossReferences { get; set; } = new();
    public List<DocumentConflict> Conflicts { get; set; } = new();
    public List<(string, string)> ComplementaryPairs { get; set; } = new();
    public int DocCount { get; set; }
    public List<string> TemporalOrder { get; set; } = new();
}
