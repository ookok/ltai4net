using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public sealed class HierarchicalChunker
{
    private readonly ILogger<HierarchicalChunker> _logger;

    public HierarchicalChunker(ILogger<HierarchicalChunker> logger) => _logger = logger;

    public List<Chunk> Chunk(string text, int maxChunkSize = 1000, int overlap = 100, string? title = null)
    {
        var chunks = new List<Chunk>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        var sections = SplitBySections(text);
        var idx = 0;

        foreach (var (heading, content) in sections)
        {
            if (content.Length <= maxChunkSize)
            {
                chunks.Add(new Chunk { Index = idx++, Heading = heading, Text = content.Trim(), Length = content.Length });
            }
            else
            {
                var subChunks = SplitByParagraphs(content, heading, maxChunkSize, overlap);
                foreach (var sc in subChunks) { sc.Index = idx++; chunks.Add(sc); }
            }
        }

        if (title != null)
        {
            foreach (var c in chunks) c.SourceTitle = title;
        }

        _logger.LogInformation("Chunked: {Chunks} from {Size} chars ({Sections} sections)", chunks.Count, text.Length, sections.Count);
        return chunks;
    }

    private static List<(string heading, string content)> SplitBySections(string text)
    {
        var sections = new List<(string, string)>();
        var matches = Regex.Matches(text, @"^(#{1,3})\s+(.+)$", RegexOptions.Multiline);
        if (matches.Count == 0)
        {
            sections.Add(("", text));
            return sections;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var heading = matches[i].Groups[2].Value;
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var content = text[start..end].Trim();
            var level = matches[i].Groups[1].Value.Length;
            sections.Add((new string('#', level) + " " + heading, content));
        }

        var beforeFirst = text[..matches[0].Index].Trim();
        if (!string.IsNullOrWhiteSpace(beforeFirst))
            sections.Insert(0, ("", beforeFirst));

        return sections;
    }

    private static List<Chunk> SplitByParagraphs(string text, string heading, int maxSize, int overlap)
    {
        var chunks = new List<Chunk>();
        var paragraphs = Regex.Split(text, @"\n\s*\n");
        var current = new StringBuilder();
        var currentHeading = heading;

        foreach (var para in paragraphs)
        {
            if (current.Length + para.Length > maxSize && current.Length > 0)
            {
                chunks.Add(new Chunk { Heading = currentHeading, Text = current.ToString().Trim(), Length = current.Length });
                var overlapText = current.ToString();
                current.Clear();
                if (overlap > 0 && overlapText.Length > overlap)
                    current.Append(overlapText[^overlap..]);
                currentHeading = heading + " (cont.)";
            }
            if (para.Trim().Length > 0) current.Append(para.Trim()).Append("\n\n");
        }

        if (current.Length > 0)
            chunks.Add(new Chunk { Heading = currentHeading, Text = current.ToString().Trim(), Length = current.Length });

        return chunks;
    }
}

public sealed class Chunk
{
    public int Index { get; set; }
    public string Heading { get; set; } = "";
    public string Text { get; set; } = "";
    public int Length { get; set; }
    public string? SourceTitle { get; set; }
    public string Id => $"{SourceTitle ?? "doc"}_{Index}";
}

public sealed class MultiDocFusion
{
    private readonly ILogger<MultiDocFusion> _logger;

    public MultiDocFusion(ILogger<MultiDocFusion> logger) => _logger = logger;

    public FusionResult Fuse(List<SearchHit> hits, string query)
    {
        var queryTerms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var scored = hits.Select(h =>
        {
            var docTerms = h.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var overlap = queryTerms.Intersect(docTerms).Count();
            var semanticScore = h.Score;
            var keywordScore = queryTerms.Count > 0 ? (double)overlap / queryTerms.Count : 0;
            var freshnessScore = 1.0 / (1.0 + (DateTime.UtcNow - h.Timestamp).TotalDays / 30.0);
            var positionScore = h.Position >= 0 ? 1.0 / (1.0 + h.Position * 0.1) : 0.5;

            return new
            {
                Hit = h,
                FinalScore = semanticScore * 0.4 + keywordScore * 0.25 + freshnessScore * 0.15 + positionScore * 0.2
            };
        }).OrderByDescending(x => x.FinalScore).ToList();

        var fused = new List<SearchHit>();
        var seenText = new HashSet<string>();
        foreach (var s in scored)
        {
            var key = s.Hit.Text[..Math.Min(s.Hit.Text.Length, 50)];
            if (seenText.Add(key))
                fused.Add(s.Hit with { Score = s.FinalScore });
        }

        _logger.LogInformation("Fusion: {In} → {Out} results", hits.Count, fused.Count);
        return new FusionResult { Results = fused.Take(10).ToList(), QueryTerms = queryTerms.Count, SourceCount = hits.Select(h => h.Source).Distinct().Count() };
    }
}

public sealed class FusionResult
{
    public List<SearchHit> Results { get; init; } = new();
    public int QueryTerms { get; init; }
    public int SourceCount { get; init; }
}

public sealed record SearchHit
{
    public string Text { get; init; } = "";
    public double Score { get; init; }
    public string Source { get; init; } = "";
    public int Position { get; init; } = -1;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class ProvenanceTracker
{
    private readonly ConcurrentDictionary<string, ProvenanceRecord> _records = new();

    public void Track(string contentId, string source, string? author = null, DateTime? timestamp = null)
    {
        _records[contentId] = new ProvenanceRecord
        {
            ContentId = contentId, Source = source,
            Author = author ?? "unknown",
            IngestedAt = DateTime.UtcNow,
            OriginalTimestamp = timestamp ?? DateTime.UtcNow
        };
    }

    public ProvenanceRecord? Get(string contentId) =>
        _records.TryGetValue(contentId, out var r) ? r : null;

    public List<ProvenanceRecord> GetBySource(string source) =>
        _records.Values.Where(r => r.Source.Contains(source, StringComparison.OrdinalIgnoreCase)).ToList();

    public int RecordCount => _records.Count;
}

public sealed class ProvenanceRecord
{
    public string ContentId { get; init; } = "";
    public string Source { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTime IngestedAt { get; init; }
    public DateTime OriginalTimestamp { get; init; }
    public int AccessCount { get; set; }
}

public sealed class PIIRedactor
{
    private static readonly Regex[] Patterns =
    {
        new(@"\b\d{3}[-.]?\d{4}[-.]?\d{4}\b", RegexOptions.Compiled),
        new(@"\b\d{17}[\dXx]\b", RegexOptions.Compiled),
        new(@"\b1[3-9]\d{9}\b", RegexOptions.Compiled),
        new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled),
        new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled),
        new(@"[A-Za-z0-9+/]{40,}={0,2}", RegexOptions.Compiled),
    };

    public static string Redact(string text)
    {
        text = Patterns[0].Replace(text, "[PHONE]");
        text = Patterns[1].Replace(text, "[ID]");
        text = Patterns[2].Replace(text, "[MOBILE]");
        text = Patterns[3].Replace(text, "[EMAIL]");
        text = Patterns[4].Replace(text, "[IP]");
        text = Patterns[5].Replace(text, "[TOKEN]");
        return text;
    }

    public static bool ContainsPII(string text) => Patterns.Any(p => p.IsMatch(text));
    public static int CountPII(string text) => Patterns.Sum(p => p.Matches(text).Count);
}

public sealed class LearningEngine
{
    private readonly ConcurrentDictionary<string, double> _sourceWeights = new();
    private readonly List<LearningEvent> _history = new();
    private double _globalQuality = 0.5;

    public void RecordFeedback(string source, bool isRelevant, double quality = 0.5)
    {
        var weight = _sourceWeights.GetOrAdd(source, 0.5);
        _sourceWeights[source] = weight * 0.9 + (isRelevant ? 1.0 : 0.0) * 0.1;
        _globalQuality = _globalQuality * 0.95 + quality * 0.05;
        _history.Add(new LearningEvent { Source = source, IsRelevant = isRelevant, Timestamp = DateTime.UtcNow });
        if (_history.Count > 1000) _history.RemoveRange(0, 200);
    }

    public double GetSourceWeight(string source) => _sourceWeights.GetValueOrDefault(source, 0.5);
    public double GlobalQuality => _globalQuality;
    public int EventCount => _history.Count;

    public IReadOnlyList<(string source, double weight)> GetTopSources(int n = 5) =>
        _sourceWeights.OrderByDescending(kvp => kvp.Value).Take(n)
            .Select(kvp => (kvp.Key, kvp.Value)).ToList().AsReadOnly();
}

public sealed class LearningEvent
{
    public string Source { get; init; } = "";
    public bool IsRelevant { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed class ContextWiki
{
    private readonly ConcurrentDictionary<string, string> _wiki = new();

    public void Upsert(string key, string value) => _wiki[key] = value;
    public string? Get(string key) => _wiki.TryGetValue(key, out var v) ? v : null;

    public string Summarize(string query, int maxItems = 3)
    {
        var matches = _wiki
            .Where(kvp => kvp.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          kvp.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(maxItems).ToList();

        if (matches.Count == 0) return "No relevant context found.";
        return string.Join("\n", matches.Select(m => $"[{m.Key}] {m.Value[..Math.Min(m.Value.Length, 200)]}"));
    }

    public int EntryCount => _wiki.Count;

    public void ImportFromChunks(List<Chunk> chunks)
    {
        foreach (var c in chunks)
            if (!string.IsNullOrEmpty(c.Heading))
                Upsert(c.Heading, c.Text[..Math.Min(c.Text.Length, 500)]);
    }
}
