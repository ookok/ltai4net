using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.Knowledge.Core.Models;
using LTAI.Knowledge.Vector.Interfaces;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public class TemporalCompressor
{
    private const double HotThreshold = 1800;
    private const double WarmThreshold = 86400;
    private const int HotAccessMin = 5;
    private const int WarmAccessMin = 2;

    private readonly Dictionary<string, CompressedEntry> _compressed = new();
    private readonly ILogger<TemporalCompressor> _logger;

    public CompressStats Stats { get; set; } = new(0, 0, 0, 0, 0, 0);

    public TemporalCompressor(ILogger<TemporalCompressor> logger)
    {
        _logger = logger;
    }

    public string Classify(EventEntry entry, int accessCount = 0, double lastAccess = 0.0)
    {
        double age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ParseTimestamp(entry.Timestamp);

        if (age <= HotThreshold && accessCount >= HotAccessMin) return "hot";
        if (age <= WarmThreshold && accessCount >= WarmAccessMin) return "warm";
        return "cold";
    }

    public CompressedEntry? Compress(EventEntry entry, string tier)
    {
        var keywords = ExtractKeywords(entry.Content);

        string summary = tier switch
        {
            "hot" => entry.Content,
            "warm" => SummarizeWarm(entry.Content),
            "cold" => SummarizeCold(entry.Content),
            _ => entry.Content
        };

        var compressed = new CompressedEntry(
            Id: $"comp_{entry.Id}", OriginalId: entry.Id, Tier: tier,
            Summary: summary, Keywords: keywords, Timestamp: entry.Timestamp,
            OriginalSize: Encoding.UTF8.GetByteCount(entry.Content),
            CompressedSize: Encoding.UTF8.GetByteCount(summary));

        _compressed[compressed.Id] = compressed;

        Stats = Stats with
        {
            HotCount = Stats.HotCount + (tier == "hot" ? 1 : 0),
            WarmCount = Stats.WarmCount + (tier == "warm" ? 1 : 0),
            ColdCount = Stats.ColdCount + (tier == "cold" ? 1 : 0),
            TotalCompressed = Stats.TotalCompressed + 1,
            BytesSaved = Stats.BytesSaved + Math.Max(0, compressed.OriginalSize - compressed.CompressedSize),
            LastCompress = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        return compressed;
    }

    public static List<string> ExtractKeywords(string text, int limit = 5)
    {
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, @"[\p{L}\d]{2,}"))
        {
            var word = m.Value.ToLowerInvariant();
            freq[word] = freq.GetValueOrDefault(word) + 1;
        }
        return freq.OrderByDescending(kv => kv.Value).Take(limit).Select(kv => kv.Key).ToList();
    }

    public static string SummarizeWarm(string text)
    {
        var sentences = Regex.Split(text, @"[。.!！?？\n]").Where(s => s.Length > 5).Take(3);
        var result = string.Join(". ", sentences);
        return result.Length > 300 ? result[..297] + "..." : result;
    }

    public static string SummarizeCold(string text)
    {
        var first = Regex.Split(text, @"[。.!！?？\n]").FirstOrDefault(s => s.Length > 5) ?? text;
        return first.Length > 150 ? first[..147] + "..." : first;
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["hot_count"] = Stats.HotCount, ["warm_count"] = Stats.WarmCount,
        ["cold_count"] = Stats.ColdCount, ["total_compressed"] = Stats.TotalCompressed,
        ["bytes_saved"] = Stats.BytesSaved
    };

    private static double ParseTimestamp(string ts)
    {
        if (double.TryParse(ts, out var unix)) return unix;
        if (DateTime.TryParse(ts, out var dt)) return new DateTimeOffset(dt).ToUnixTimeSeconds();
        return 0;
    }
}

public class SignalCleaner
{
    private const int MinContentLength = 5;
    private const int MaxContentLength = 50000;
    private const double MaxRepetitionRatio = 0.6;
    private const double MinAlphaRatio = 0.05;

    private readonly LinkedList<(string hash, double time)> _seenHashes = new();
    private readonly ILogger<SignalCleaner> _logger;

    public CleanReport Report { get; private set; } = new(0, 0, 0, new(), 0.0, new());

    public SignalCleaner(ILogger<SignalCleaner> logger)
    {
        _logger = logger;
    }

    public List<CleanResult> Clean(Dictionary<string, object> msg, string sessionId = "")
    {
        var content = msg.GetValueOrDefault("content", "")?.ToString() ?? "";
        var role = msg.GetValueOrDefault("role", "user")?.ToString() ?? "user";
        var results = new List<CleanResult>();

        var outlier = CheckOutlier(content);
        results.Add(outlier);
        if (!outlier.Passed) return results;

        var coherence = CheckCoherence(content);
        results.Add(coherence);

        var dedup = CheckDedup(content);
        results.Add(dedup);

        return results;
    }

    public bool IsClean(List<CleanResult> results) => results.All(r => r.Passed);
    public double QualityScore(List<CleanResult> results) =>
        results.Count == 0 ? 0 : results.Average(r => r.QualityScore);

    public CleanResult CheckOutlier(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new("outlier", false, "empty content", 0);
        if (content.Length < MinContentLength)
            return new("outlier", false, $"too short: {content.Length}", 0.2);
        if (content.Length > MaxContentLength)
            return new("outlier", false, $"too long: {content.Length}", 0.3);

        double alphaRatio = (double)Regex.Matches(content, @"[\p{L}]").Count / Math.Max(content.Length, 1);
        if (alphaRatio < MinAlphaRatio)
            return new("outlier", false, $"low alpha ratio: {alphaRatio:F2}", 0.1);

        var words = Regex.Split(content, @"\s+").Where(w => w.Length > 1).ToList();
        if (words.Count > 0)
        {
            var mostFreq = words.GroupBy(w => w).Max(g => g.Count());
            double repRatio = (double)mostFreq / words.Count;
            if (repRatio > MaxRepetitionRatio)
                return new("outlier", false, $"high repetition: {repRatio:F2}", 0.3);
        }

        return new("outlier", true, "", 1.0);
    }

    public CleanResult CheckCoherence(string content)
    {
        if (Regex.IsMatch(content, @"(yes.*no|no.*yes|是.*否|否.*是|对.*错|错.*对)", RegexOptions.IgnoreCase))
            return new("coherence", false, "contradictory yes/no oscillation", 0.3);

        if (Regex.IsMatch(content, @"(\b\w{1,3}\b\s*){10,}"))
            return new("coherence", false, "gibberish short tokens", 0.1);

        return new("coherence", true, "", 1.0);
    }

    public CleanResult CheckDedup(string content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var existing = _seenHashes.FirstOrDefault(h => h.hash == hash);
        if (existing.hash == hash && now - existing.time < 300)
            return new("dedup", false, "duplicate within 300s", 0.1);

        _seenHashes.AddLast((hash, now));
        if (_seenHashes.Count > 1000)
            _seenHashes.RemoveFirst();

        return new("dedup", true, "", 1.0);
    }

    public Dictionary<string, object> GetReport() => new()
    {
        ["total"] = Report.Total, ["passed"] = Report.Passed,
        ["rejected"] = Report.Rejected, ["avg_quality"] = Report.AvgQuality
    };
}

public class StructMemory
{
    private const double ConsolidationThreshold = 300;
    private const int SemanticSeeds = 15;
    private const int DefaultTopK = 60;
    private const int DefaultSynthesis = 5;
    private const int MaxEntries = 10000;
    private const int MaxSynthesis = 500;

    private readonly Dictionary<string, EventEntry> _entries = new();
    private readonly List<SynthesisBlock> _synthesis = new();
    private MutableMemoryBuffer _buffer = new();
    private double _lastConsolidation;
    private readonly List<ExcludeRule> _excludeRules = new();
    private readonly Dictionary<string, int> _excludeStats = new() { ["blocked_total"] = 0, ["blocked_by_rule"] = 0 };
    private readonly IVectorStore _vectorStore;
    private readonly ILogger<StructMemory> _logger;

    public TemporalCompressor Compressor { get; }
    public SignalCleaner Cleaner { get; }

    public StructMemory(IVectorStore vectorStore, ILogger<StructMemory> logger,
        TemporalCompressor? compressor = null, SignalCleaner? cleaner = null)
    {
        _vectorStore = vectorStore;
        _logger = logger;
        Compressor = compressor ?? new TemporalCompressor(logger is ILogger<TemporalCompressor> tl ? tl : null!);
        Cleaner = cleaner ?? new SignalCleaner(logger is ILogger<SignalCleaner> sl ? sl : null!);
        SeedDefaultExclusions();
    }

    private void SeedDefaultExclusions()
    {
        _excludeRules.Add(new("^\\s*$", "empty/whitespace"));
        _excludeRules.Add(new("^[好的嗯哦啊唉]{1,3}$", "single-word acknowledgments"));
        _excludeRules.Add(new("^[^\\p{L}]*$", "no alphanumeric"));
    }

    public async Task<List<EventEntry>> BindEvents(string sessionId, List<Dictionary<string, object>> messages,
        string? timestamp = null)
    {
        var created = new List<EventEntry>();
        foreach (var msg in messages)
        {
            var (shouldExclude, rule) = ShouldExclude(msg);
            if (shouldExclude) continue;

            var cleanResults = Cleaner.Clean(msg, sessionId);
            if (!Cleaner.IsClean(cleanResults)) continue;

            var content = msg.GetValueOrDefault("content", "")?.ToString() ?? "";
            var role = msg.GetValueOrDefault("role", "user")?.ToString() ?? "user";
            var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var id = MakeId(sessionId, ts, role);

            var fact = ExtractFact(content);
            var rel = ExtractRel(content);
            var embedding = await ComputeEmbedding(content).ConfigureAwait(false);

            var entry = new EventEntry(id, sessionId, ts, role, content, fact, rel, embedding);
            _entries[id] = entry;
            _buffer.Entries.Add(entry);
            _buffer.FirstTimestamp = string.IsNullOrEmpty(_buffer.FirstTimestamp) ? ts : _buffer.FirstTimestamp;
            _buffer.LastTimestamp = ts;
            created.Add(entry);
        }

        PruneIfNeeded();
        return created;
    }

    public async Task<List<SynthesisBlock>> ConsolidateIfNeeded()
    {
        if (_buffer.Entries.Count >= 3)
        {
            var elapsed = _buffer.Entries.Count > 1
                ? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ParseTimestamp(_buffer.FirstTimestamp))
                : 0;
            if (elapsed >= ConsolidationThreshold)
                return await Consolidate().ConfigureAwait(false);
        }
        return new();
    }

    private async Task<List<SynthesisBlock>> Consolidate()
    {
        var blocks = new List<SynthesisBlock>();
        if (_buffer.Entries.Count == 0) return blocks;

        var bufferText = string.Join("\n", _buffer.Entries.Select(e => $"[{e.Role}] {e.Content}"));
        var queryEmbedding = await ComputeEmbedding(bufferText).ConfigureAwait(false);
        var seeds = await SemanticRetrieve(queryEmbedding, SemanticSeeds).ConfigureAwait(false);
        var reconstructed = ReconstructEvents(seeds, _buffer.Entries);
        var synthText = await Synthesize(_buffer.Entries, reconstructed).ConfigureAwait(false);

        var block = new SynthesisBlock(
            $"syn_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            synthText,
            _buffer.Entries.Select(e => e.Id).ToList(),
            new() { _buffer.Entries.First().SessionId });

        _synthesis.Add(block);
        blocks.Add(block);
        _buffer.Entries.Clear();
        _lastConsolidation = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        PruneIfNeeded();
        return blocks;
    }

    public async Task<(List<EventEntry> Events, List<SynthesisBlock> Synthesis)> RetrieveForQuery(
        string query, int topK = DefaultTopK, int nSynthesis = DefaultSynthesis)
    {
        var queryEmbedding = await ComputeEmbedding(query).ConfigureAwait(false);
        var events = await SemanticRetrieve(queryEmbedding, topK).ConfigureAwait(false);

        var scoredSynth = new List<(SynthesisBlock Block, double Score)>();
        foreach (var s in _synthesis)
        {
            var synthEmbedding = await ComputeEmbedding(s.Content).ConfigureAwait(false);
            var sim = CosineSimilarity(queryEmbedding, synthEmbedding);
            scoredSynth.Add((s, sim));
        }
        var relevantSynth = scoredSynth.OrderByDescending(x => x.Score).Take(nSynthesis).Select(x => x.Block).ToList();

        return (events, relevantSynth);
    }

    public string GetContextBlock(string query = "", List<EventEntry>? entries = null,
        List<SynthesisBlock>? synthesis = null)
    {
        var sb = new StringBuilder();
        if (synthesis is { Count: > 0 })
        {
            sb.AppendLine("[RELEVANT MEMORY SYNTHESIS]");
            for (int i = 0; i < synthesis.Count; i++)
                sb.AppendLine($"S{i + 1}: {synthesis[i].Content}");
        }
        if (entries is { Count: > 0 })
        {
            sb.AppendLine("[RELATED PAST EVENTS]");
            foreach (var e in entries)
                sb.AppendLine($"[{e.Timestamp}] {e.Role.ToUpper()}: {e.Content[..Math.Min(e.Content.Length, 200)]}");
        }
        return sb.ToString();
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["entries_total"] = _entries.Count, ["synthesis_total"] = _synthesis.Count,
        ["buffer_size"] = _buffer.Entries.Count, ["exclude_rules"] = _excludeRules.Count
    };

    public List<Opinion> SynthesizeOpinions(int limit = 8)
    {
        var opinions = new List<Opinion>();
        foreach (var synth in _synthesis.TakeLast(20))
        {
            foreach (Match m in Regex.Matches(synth.Content, @"^[-*•]\s*(.+)$", RegexOptions.Multiline))
            {
                var text = m.Groups[1].Value.Trim();
                if (text.Length > 10)
                    opinions.Add(new Opinion(text, 0.5, 1, OpinionCategory(text)));
            }
        }
        return opinions.Take(limit).ToList();
    }

    public MentalModel BuildMentalModel(string name = "default")
    {
        var opinions = SynthesizeOpinions();
        return new MentalModel($"model_{name}", name, $"Mental model: {name}", opinions);
    }

    public CompressStats CompressAgedEntries()
    {
        foreach (var entry in _entries.Values.ToList())
        {
            var tier = Compressor.Classify(entry);
            if (tier != "hot") Compressor.Compress(entry, tier);
        }
        return Compressor.Stats;
    }

    private string ExtractFact(string text)
    {
        var sentences = Regex.Split(text, @"[。.!！?？\n]").Where(s => s.Length > 5).ToList();
        if (sentences.Count == 0) return text.Length > 200 ? text[..200] : text;
        return string.Join(". ", sentences.Take(3));
    }

    private string ExtractRel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var relations = new List<string>();

        foreach (Match m in Regex.Matches(text, @"(\S+)\s*(是|属于|为|即)\s*(\S+)"))
        {
            var subj = m.Groups[1].Value.Trim();
            var obj = m.Groups[3].Value.Trim();
            if (subj.Length > 1 && obj.Length > 1)
                relations.Add($"{subj} is_a {obj}");
        }

        foreach (Match m in Regex.Matches(text, @"(\S+)\s+is\s+(an?\s+)?(\S+)", RegexOptions.IgnoreCase))
        {
            var subj = m.Groups[1].Value.Trim();
            var obj = m.Groups[3].Value.Trim();
            if (subj.Length > 1 && obj.Length > 1)
                relations.Add($"{subj} is_a {obj}");
        }

        foreach (Match m in Regex.Matches(text, @"(\S+)\s*(有|拥有|具有|包含)\s*(\S+)"))
        {
            var subj = m.Groups[1].Value.Trim();
            var obj = m.Groups[3].Value.Trim();
            if (subj.Length > 1 && obj.Length > 1)
                relations.Add($"{subj} has {obj}");
        }

        foreach (Match m in Regex.Matches(text, @"(\S+)\s+has\s+(an?\s+)?(\S+)", RegexOptions.IgnoreCase))
        {
            var subj = m.Groups[1].Value.Trim();
            var obj = m.Groups[3].Value.Trim();
            if (subj.Length > 1 && obj.Length > 1)
                relations.Add($"{subj} has {obj}");
        }

        return relations.Count > 0 ? string.Join("; ", relations.Take(10)) : "";
    }

    private async Task<List<double>> ComputeEmbedding(string text)
    {
        try
        {
            var vec = await _vectorStore.EmbedAsync(text).ConfigureAwait(false);
            return vec.Select(f => (double)f).ToList();
        }
        catch { return new(); }
    }

    private async Task<List<EventEntry>> SemanticRetrieve(List<double> queryEmbedding, int topK)
    {
        var scored = new List<(EventEntry entry, double score)>();
        foreach (var entry in _entries.Values)
        {
            if (entry.Embedding is { Count: > 0 })
            {
                double score = CosineSimilarity(queryEmbedding, entry.Embedding);
                scored.Add((entry, score));
            }
        }
        return scored.OrderByDescending(s => s.score).Take(topK).Select(s => s.entry).ToList();
    }

    private static List<EventEntry> ReconstructEvents(List<EventEntry> seeds, List<EventEntry> bufferEntries)
    {
        var result = new List<EventEntry>(seeds);
        foreach (var be in bufferEntries)
            if (!result.Any(r => r.Id == be.Id))
                result.Add(be);
        result.Sort((a, b) => string.Compare(a.Timestamp, b.Timestamp, StringComparison.Ordinal));
        return result;
    }

    private Task<string> Synthesize(List<EventEntry> bufferEntries, List<EventEntry> reconstructed)
    {
        var text = string.Join("\n", reconstructed.Select(e =>
            $"[{e.Timestamp}] {e.Role}: {e.FactPerspective} {e.RelPerspective}".Trim()).Where(s => s.Length > 5));

        if (string.IsNullOrWhiteSpace(text))
            text = string.Join("\n", bufferEntries.Select(e => $"[{e.Role}] {e.Content[..Math.Min(e.Content.Length, 200)]}"));

        return Task.FromResult($"Consolidated {reconstructed.Count} events from buffer. Summary: {text[..Math.Min(text.Length, 1000)]}");
    }

    private void PruneIfNeeded()
    {
        if (_entries.Count > MaxEntries)
        {
            var toRemove = _entries.OrderBy(kv => kv.Value.Timestamp).Take(_entries.Count - MaxEntries).ToList();
            foreach (var kv in toRemove) _entries.Remove(kv.Key);
        }
        if (_synthesis.Count > MaxSynthesis)
            _synthesis.RemoveRange(0, _synthesis.Count - MaxSynthesis);
    }

    private (bool shouldExclude, ExcludeRule? rule) ShouldExclude(Dictionary<string, object> msg)
    {
        foreach (var rule in _excludeRules)
        {
            if (!rule.Enabled) continue;
            var field = rule.MatchField switch
            {
                "content" => msg.GetValueOrDefault("content", "")?.ToString() ?? "",
                "role" => msg.GetValueOrDefault("role", "")?.ToString() ?? "",
                _ => string.Join(" ", msg.Values)
            };
            if (Regex.IsMatch(field, rule.Pattern))
                return (true, rule);
        }
        return (false, null);
    }

    private static string OpinionCategory(string text)
    {
        if (Regex.IsMatch(text, @"喜欢|偏好|习惯")) return "preference";
        if (Regex.IsMatch(text, @"行为|行动|做")) return "behavior";
        if (Regex.IsMatch(text, @"技能|会|能|擅长")) return "skill";
        if (Regex.IsMatch(text, @"风险|危险|问题")) return "risk";
        if (Regex.IsMatch(text, @"关系|连接|关联")) return "relationship";
        return "general";
    }

    public static double CosineSimilarity(List<double> a, List<double> b)
    {
        if (a.Count == 0 || b.Count == 0 || a.Count != b.Count) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-10 ? 0 : dot / denom;
    }

    public static string MakeId(string session, string ts, string role)
    {
        var raw = $"{session}:{ts}:{role}";
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }

    private static double ParseTimestamp(string ts)
    {
        if (double.TryParse(ts, out var unix)) return unix;
        if (DateTime.TryParse(ts, out var dt)) return new DateTimeOffset(dt).ToUnixTimeSeconds();
        return 0;
    }
}
