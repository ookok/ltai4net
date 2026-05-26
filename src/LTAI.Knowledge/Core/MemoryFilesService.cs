using System.Collections.Concurrent;
using LTAI.Knowledge.Core.Models;
using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Knowledge.Core;

public sealed class MemoryFilesService
{
    private readonly MemoryFileLoader _loader;
    private readonly KnowledgeGraph _knowledgeGraph;
    private readonly ILogger<MemoryFilesService> _logger;
    private readonly ConcurrentDictionary<string, MemoryFile> _files = new();
    private readonly ConcurrentDictionary<string, List<string>> _byDomain = new();
    private readonly ConcurrentDictionary<string, List<string>> _byTag = new();
    private MemoryMode _mode = MemoryMode.Files;
    private readonly string _memoryRoot;

    public IReadOnlyDictionary<string, MemoryFile> All => _files;
    public MemoryMode Mode => _mode;
    public int FileCount => _files.Count;

    public MemoryFilesService(
        MemoryFileLoader loader,
        KnowledgeGraph knowledgeGraph,
        ILogger<MemoryFilesService> logger,
        string? memoryRoot = null)
    {
        _loader = loader;
        _knowledgeGraph = knowledgeGraph;
        _logger = logger;
        _memoryRoot = memoryRoot ?? OptionService.Get("paths.memory") ?? Path.Combine(AppContext.BaseDirectory, "memory");
    }

    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_memoryRoot))
        {
            Directory.CreateDirectory(_memoryRoot);
            _logger.LogInformation("Memory root created: {Path}", _memoryRoot);
            return;
        }

        var mdFiles = Directory.GetFiles(_memoryRoot, "*.md", SearchOption.AllDirectories);
        _logger.LogInformation("Loading {Count} memory files from {Root}", mdFiles.Length, _memoryRoot);

        foreach (var file in mdFiles)
        {
            if (file.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)) continue;

            var mf = await _loader.LoadAsync(file, ct).ConfigureAwait(false);
            if (mf == null) continue;

            Register(mf);
            _logger.LogDebug("Loaded memory file: {Name} [{Domain}]", mf.Name, mf.Domain);
        }
    }

    public void Register(MemoryFile file)
    {
        _files[file.Name] = file;
        _byDomain.GetOrAdd(file.Domain, _ => new List<string>()).Add(file.Name);
        foreach (var tag in file.Tags)
            _byTag.GetOrAdd(tag, _ => new List<string>()).Add(file.Name);
    }

    public MemoryFile? Get(string name)
    {
        _files.TryGetValue(name, out var mf);
        return mf;
    }

    public List<MemoryFile> GetByDomain(string domain)
    {
        if (!_byDomain.TryGetValue(domain, out var names)) return new List<MemoryFile>();
        return names.Select(n => _files.TryGetValue(n, out var f) ? f : null).Where(f => f != null).Cast<MemoryFile>().ToList();
    }

    public List<MemoryFile> GetByTag(string tag)
    {
        if (!_byTag.TryGetValue(tag, out var names)) return new List<MemoryFile>();
        return names.Select(n => _files.TryGetValue(n, out var f) ? f : null).Where(f => f != null).Cast<MemoryFile>().ToList();
    }

    /// <summary>
    /// Selective retrieval: finds memory files relevant to a query/task.
    /// Uses trigger pattern matching and domain/tag overlap with knowledge graph entities.
    /// </summary>
    public List<MemoryFile> RetrieveRelevant(string task, string? domain = null, int topK = 5)
    {
        var scored = new List<(MemoryFile File, float Score)>();

        foreach (var mf in _files.Values.Where(f => f.IsActive))
        {
            float score = 0;

            if (domain != null && mf.Domain == domain)
                score += 2.0f;

            foreach (var trigger in mf.Triggers)
            {
                try
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(task, trigger.Pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        score += trigger.Weight;
                }
                catch
                {
                    if (task.Contains(trigger.Pattern, StringComparison.OrdinalIgnoreCase))
                        score += trigger.Weight;
                }
            }

            foreach (var tag in mf.Tags)
            {
                if (task.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    score += 0.5f;
            }

            if (!string.IsNullOrEmpty(mf.Topic) &&
                task.Contains(mf.Topic, StringComparison.OrdinalIgnoreCase))
                score += 1.0f;

            if (mf.Context.Length > 0 &&
                task.Contains(mf.Context[..Math.Min(50, mf.Context.Length)], StringComparison.OrdinalIgnoreCase))
                score += 1.0f;

            if (score > 0)
                score += (float)mf.Confidence * 0.2f;

            if (score > 0)
                scored.Add((mf, score));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .Select(s =>
            {
                s.File.Evolution.RecordAccess(true);
                return s.File;
            })
            .ToList();
    }

    /// <summary>
    /// Promote high-confidence KnowledgeGraph triplets to Memory Files.
    /// Triggered when KG's PredictabilityIndex falls below threshold.
    /// This implements the KG → Memory Files fallback: when graph is unreliable,
    /// export structured knowledge into filesystem memory.
    /// </summary>
    public async Task<List<MemoryFile>> PromoteFromKnowledgeGraph(int minFacts = 3, CancellationToken ct = default)
    {
        var promoted = new List<MemoryFile>();

        try
        {
            var allTriplets = _knowledgeGraph.GetTriplets();
            var groupedBySubject = allTriplets
                .Where(t => t.Confidence >= 0.8)
                .GroupBy(t => t.Subject)
                .Where(g => g.Count() >= minFacts)
                .ToList();

            foreach (var group in groupedBySubject)
            {
                var triplets = group.ToList();
                var subject = group.Key;
                var existingFile = _files.Values.FirstOrDefault(f =>
                    f.SourceEntityIds.Any(id => id.Contains(subject, StringComparison.OrdinalIgnoreCase)) ||
                    f.Name.Contains(subject, StringComparison.OrdinalIgnoreCase));

                if (existingFile != null && existingFile.IsVerified) continue;

                var domain = DetectDomain(subject, triplets);
                var facts = triplets.Select(t => new MemoryFileFact
                {
                    Statement = $"{t.Subject} {t.Predicate} {t.Object}",
                    Confidence = t.Confidence,
                    Source = t.SourceText
                }).ToList();

                var topic = subject.Length > 50 ? subject[..47] + "..." : subject;
                var entityId = KnowledgeGraph.EntityId(subject);
                var fileName = $"{SanitizeFileName(topic)}_{entityId}.md";
                var filePath = Path.Combine(_memoryRoot, domain, fileName);

                var memoryFile = new MemoryFile
                {
                    Name = topic,
                    Domain = domain,
                    Topic = topic,
                    Summary = $"Auto-generated memory from KnowledgeGraph for '{topic}'",
                    Facts = facts,
                    Tags = new List<string> { domain, "kg-promoted" },
                    SourceEntityIds = new List<string> { entityId },
                    Confidence = triplets.Average(t => t.Confidence),
                    Verification = new MemoryFileVerification
                    {
                        LastVerified = DateTime.UtcNow,
                        VerifiedBy = "kg_auto"
                    }
                };

                await _loader.SaveAsync(memoryFile, filePath, ct).ConfigureAwait(false);
                Register(memoryFile);
                promoted.Add(memoryFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to promote KnowledgeGraph to Memory Files");
        }

        _logger.LogInformation("Promoted {Count} KG topic groups to Memory Files", promoted.Count);
        return promoted;
    }

    /// <summary>
    /// Select memory mode based on KnowledgeGraph PredictabilityIndex.
    /// Classic: rely on StructMemory + KG (good when PI > 0.6).
    /// Files: use Memory Files with KG as supplement (good when PI < 0.6).
    /// </summary>
    public MemoryMode SelectMode()
    {
        try
        {
            var stats = _knowledgeGraph.GetStats();
            if (stats is not Dictionary<string, object> dict) return MemoryMode.Files;

            if (dict.TryGetValue("predictability", out var predObj))
            {
                var pred = (dynamic)predObj;
                double pi = (double)pred.pi;
                if (pi > 0.6)
                {
                    _mode = _files.Count > 0 ? MemoryMode.Files : MemoryMode.Classic;
                }
                else
                {
                    _mode = MemoryMode.Files;
                }
            }
        }
        catch
        {
            _mode = MemoryMode.Files;
        }

        return _mode;
    }

    /// <summary>
    /// Deduplicate: merge memory files with highly overlapping facts.
    /// Returns count of merged files.
    /// </summary>
    public async Task<int> DeduplicateAsync(CancellationToken ct = default)
    {
        var merged = 0;
        var checked_ = new HashSet<string>();

        foreach (var (name, file) in _files)
        {
            if (checked_.Contains(name)) continue;
            checked_.Add(name);

            var similar = _files.Values
                .Where(f => f.Name != name && !checked_.Contains(f.Name) &&
                    f.Facts.Count > 0 && file.Facts.Count > 0)
                .ToList();

            foreach (var other in similar)
            {
                var overlap = file.Facts.Count(f =>
                    other.Facts.Any(o => OverlapScore(f.Statement, o.Statement) > 0.7));

                var overlapRatio = (double)overlap / Math.Max(file.Facts.Count, 1);
                if (overlapRatio < 0.5) continue;

                var mergedFile = MergeTwo(file, other);
                await _loader.SaveAsync(mergedFile, file.SourceFile, ct).ConfigureAwait(false);

                _files.TryRemove(other.Name, out _);
                _files[mergedFile.Name] = mergedFile;
                if (other.SourceFile != null && File.Exists(other.SourceFile))
                    File.Delete(other.SourceFile);

                checked_.Add(other.Name);
                merged++;
            }
        }

        return merged;
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_files"] = _files.Count,
        ["active"] = _files.Values.Count(f => f.IsActive),
        ["reliable"] = _files.Values.Count(f => f.IsReliable),
        ["mode"] = _mode.ToString(),
        ["total_facts"] = _files.Values.Sum(f => f.Facts.Count),
        ["by_domain"] = _byDomain.ToDictionary(k => k.Key, v => v.Value.Count)
    };

    private static MemoryFile MergeTwo(MemoryFile primary, MemoryFile secondary)
    {
        var allFacts = new List<MemoryFileFact>(primary.Facts);
        foreach (var fact in secondary.Facts)
        {
            if (!allFacts.Any(f => OverlapScore(f.Statement, fact.Statement) > 0.9))
                allFacts.Add(fact);
        }

        return primary with
        {
            Summary = primary.Summary + "\n[merged with: " + secondary.Name + "]",
            Facts = allFacts,
            Tags = primary.Tags.Union(secondary.Tags).ToList(),
            SourceEntityIds = primary.SourceEntityIds.Union(secondary.SourceEntityIds).ToList(),
            Confidence = Math.Max(primary.Confidence, secondary.Confidence),
            Verification = primary.Verification.IsStale || secondary.Verification.IsStale
                ? new MemoryFileVerification() : primary.Verification
        };
    }

    private static double OverlapScore(string a, string b)
    {
        var wordsA = new HashSet<string>(a.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var wordsB = new HashSet<string>(b.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (wordsA.Count == 0 || wordsB.Count == 0) return 0;
        var intersection = wordsA.Intersect(wordsB).Count();
        return (double)intersection / Math.Min(wordsA.Count, wordsB.Count);
    }

    private static string DetectDomain(string entityLabel, List<Triplet> triplets)
    {
        var text = entityLabel + " " + string.Join(" ", triplets.Select(t => t.Predicate));
        var lower = text.ToLowerInvariant();

        if (lower.Contains("code") || lower.Contains("source") || lower.Contains("file")) return "code";
        if (lower.Contains("eia") || lower.Contains("环境") || lower.Contains("空气")) return "eia";
        if (lower.Contains("skill") || lower.Contains("workflow")) return "skills";
        if (lower.Contains("config") || lower.Contains("setting")) return "config";
        return "general";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return sanitized.Length > 50 ? sanitized[..50] : sanitized;
    }
}
