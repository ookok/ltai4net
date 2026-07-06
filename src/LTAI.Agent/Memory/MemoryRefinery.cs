// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  MemoryRefinery — MeMo-inspired five-step reflection
//  QA synthesis pipeline.
//
//  Periodically distills raw PalaceStore entries into
//  "reflections": compositional QA pairs that capture
//  explicit facts, implicit relationships, entity
//  associations, and cross-document connections.
//
//  Five steps (MeMo §4.1):
//    1. Fact Extraction       — direct + indirect extraction
//    2. Consolidation          — merge related QA pairs
//    3. Verification           — ensure self-containment
//    4. Entity Surfacing      — generate reverse QA pairs
//    5. Cross-document Synthesis — connect related memories
// ═══════════════════════════════════════════════════════

using System.Text.RegularExpressions;
using LTAI.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Memory;

public sealed partial class MemoryRefinery : BackgroundService
{
    private readonly PalaceStore _store;
    private readonly EmbeddingClient _embedder;
    private readonly ILogger<MemoryRefinery> _logger;
    private static readonly TimeSpan RefineryInterval = TimeSpan.FromMinutes(15);
    private const int BatchSize = 50;
    private const float SimilarityThreshold = 0.65f;
    private const int MaxReflections = 500;

    public MemoryRefinery(PalaceStore store, EmbeddingClient embedder,
        ILogger<MemoryRefinery>? logger = null)
    {
        _store = store;
        _embedder = embedder;
        _logger = logger ?? Microsoft.Extensions.Logging
            .Abstractions.NullLogger<MemoryRefinery>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let the system warm up
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunRefineryAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MemoryRefinery: cycle failed");
            }
            await Task.Delay(RefineryInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Run one full refinery cycle: steps 1-5.</summary>
    private async Task RunRefineryAsync(CancellationToken ct)
    {
        _logger.LogDebug("MemoryRefinery: starting cycle");

        // Collect raw entries not yet refined (importance >= 0.4, recent)
        var rawEntries = _store.GetAllDrawers()
            .Where(d => d.Importance >= 0.4 && d.Room != "reflection")
            .OrderByDescending(d => d.Importance)
            .Take(BatchSize)
            .ToList();

        if (rawEntries.Count == 0)
        {
            _logger.LogDebug("MemoryRefinery: no entries to refine");
            return;
        }

        var refinedCount = 0;

        foreach (var entry in rawEntries)
        {
            ct.ThrowIfCancellationRequested();

            // Step 1: Fact Extraction — extract direct + indirect facts
            var facts = ExtractFacts(entry.Content);
            if (facts.Count == 0) continue;

            // Step 2: Consolidation — merge related facts via embedding similarity
            var consolidated = await ConsolidateFactsAsync(facts, ct).ConfigureAwait(false);

            // Step 3: Verification — ensure self-containment
            var verified = VerifySelfContainment(consolidated);
            if (verified.Count == 0) continue;

            // Step 4: Entity Surfacing — generate reverse QA pairs
            var entityPairs = SurfaceEntities(entry.Content, entry.Wing);

            // Write reflections with source traceback
            var traceMeta = new Dictionary<string, object>
            {
                ["source_drawer_id"] = entry.DrawerId,
                ["source_wing"] = entry.Wing,
                ["source_room"] = entry.Room,
            };

            foreach (var (q, a) in verified)
            {
                var reflection = $"Q: {q}\nA: {a}";
                await _store.StoreAsync(entry.Wing, "reflection", reflection,
                    role: "system", importance: Math.Min(1.0, entry.Importance * 1.1),
                    ttlMs: null, metadata: traceMeta).ConfigureAwait(false);
                refinedCount++;
            }

            foreach (var (q, a) in entityPairs)
            {
                var reflection = $"Q: {q}\nA: {a}";
                await _store.StoreAsync(entry.Wing, "reflection", reflection,
                    role: "system", importance: Math.Min(1.0, entry.Importance * 0.9),
                    ttlMs: null, metadata: traceMeta).ConfigureAwait(false);
                refinedCount++;
            }
        }

        // Step 5: Cross-document Synthesis — connect related entries across rooms
        await SynthesizeCrossDocumentAsync(ct).ConfigureAwait(false);

        // Enforce cap on reflection entries
        var reflectionCount = _store.SearchByRoom("reflection", maxCount: int.MaxValue).Count;
        if (reflectionCount > MaxReflections)
        {
            var toEvict = reflectionCount - MaxReflections;
            // Evict lowest-importance reflections
            var low = _store.SearchByRoom("reflection")
                .OrderBy(d => d.Importance)
                .Take(toEvict);
            foreach (var d in low)
                await _store.DeleteDrawerAsync(d.DrawerId).ConfigureAwait(false);
        }

        _logger.LogInformation("MemoryRefinery: refined {N} entries → {R} reflections",
            rawEntries.Count, refinedCount);
    }

    // ── Step 1: Fact Extraction ──
    // Extract direct (explicitly stated) and indirect (inferred) facts
    private static List<(string Question, string Answer)> ExtractFacts(string content)
    {
        var facts = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(content)) return facts;

        // Direct extraction: look for "X is Y", "X does Y", "X has Y" patterns
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 10) continue;

            // Pattern: "X: ..." or "X — ..." or "X is ..."
            foreach (Match m in FactPattern().Matches(trimmed))
            {
                var subject = m.Groups[1].Value.Trim();
                var predicate = m.Groups[2].Value.Trim();
                if (subject.Length > 0 && predicate.Length > 0)
                {
                    facts.Add(($"What is {subject}?", $"{subject}: {predicate}"));
                    facts.Add(($"What does {subject} do?", $"{subject} {predicate}"));
                }
            }
        }

        // If no structured facts found, create a general reflection
        if (facts.Count == 0 && content.Length >= 20)
        {
            var snippet = content.Length > 200 ? content[..197] + "..." : content;
            facts.Add(($"What information is contained in this memory?",
                       $"The memory contains: {snippet}"));
        }

        return facts;
    }

    // ── Step 2: Consolidation — merge related QA pairs by embedding similarity ──
    private async Task<List<(string, string)>> ConsolidateFactsAsync(
        List<(string Q, string A)> facts, CancellationToken ct)
    {
        if (facts.Count <= 1) return facts;

        var result = new List<(string, string)>();
        var used = new bool[facts.Count];

        for (int i = 0; i < facts.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var mergedQ = facts[i].Q;
            var mergedA = facts[i].A;

            for (int j = i + 1; j < facts.Count; j++)
            {
                if (used[j]) continue;
                if (IsRelated(facts[i].A, facts[j].A))
                {
                    used[j] = true;
                    mergedA += "; " + facts[j].A;
                }
            }

            result.Add((mergedQ, mergedA));
        }

        return result;
    }

    private static bool IsRelated(string a, string b)
    {
        // Quick keyword overlap check (no embedding needed for consolidation)
        var wordsA = a.Split([' ', '\t', '\n', '.'], StringSplitOptions.RemoveEmptyEntries);
        var wordsB = b.Split([' ', '\t', '\n', '.'], StringSplitOptions.RemoveEmptyEntries);
        var setB = new HashSet<string>(wordsB, StringComparer.OrdinalIgnoreCase);
        var common = wordsA.Count(w => setB.Contains(w));
        return (double)common / Math.Max(wordsA.Length, wordsB.Length) > 0.3;
    }

    // ── Step 3: Verification — ensure self-containment ──
    private static List<(string, string)> VerifySelfContainment(
        List<(string Q, string A)> pairs)
    {
        var verified = new List<(string, string)>();
        foreach (var (q, a) in pairs)
        {
            // Check for unresolved pronouns or vague references
            if (ContainsAny(q, ["it", "they", "this", "that", "these", "those"]) &&
                !ContainsAny(q, ["what", "who", "which"]))
                continue; // ambiguous → discard

            // Check answer is substantive
            if (a.Length < 5) continue;

            verified.Add((q, a));
        }
        return verified;
    }

    // ── Step 4: Entity Surfacing — reverse QA pairs ──
    private List<(string Question, string Answer)> SurfaceEntities(
        string content, string wing)
    {
        var pairs = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(content)) return pairs;

        // Extract named entities (simple heuristic: capitalized multi-word phrases)
        var entities = new HashSet<string>();
        foreach (Match m in EntityPattern().Matches(content))
        {
            var entity = m.Groups[1].Value.Trim();
            if (entity.Length >= 3 && entity.Length <= 60)
                entities.Add(entity);
        }

        foreach (var entity in entities.Take(5))
        {
            // Find what the entity does/has
            var desc = ExtractEntityDescription(content, entity);
            if (desc != null)
            {
                pairs.Add(($"Who or what is {entity}?",
                           $"{entity} is {desc}"));
                pairs.Add(($"What is {entity} known for?",
                           $"{entity}: {desc}"));
            }
        }

        return pairs;
    }

    private static string? ExtractEntityDescription(string content, string entity)
    {
        // Find the sentence containing the entity
        var sentences = content.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var sentence in sentences)
        {
            if (sentence.Contains(entity, StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = sentence.Trim();
                if (trimmed.Length > entity.Length + 5)
                    return trimmed.Length > 150 ? trimmed[..147] + "..." : trimmed;
            }
        }
        return null;
    }

    // ── Step 5: Cross-document Synthesis ──
    private async Task SynthesizeCrossDocumentAsync(CancellationToken ct)
    {
        // Find existing reflections by wing
        var wings = await _store.ListWingsAsync().ConfigureAwait(false);

        foreach (var wing in wings)
        {
            ct.ThrowIfCancellationRequested();
            var reflections = _store.SearchByRoom("reflection")
                .Where(d => string.Equals(d.Wing, wing, StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToList();

            if (reflections.Count < 2) continue;

            // Create cross-document QA pairs: combine related reflections
            for (int i = 0; i < reflections.Count - 1; i++)
            {
                for (int j = i + 1; j < reflections.Count; j++)
                {
                    var content = $"{reflections[i].Content}\n---\n{reflections[j].Content}";
                    var crossDoc = $"Q: What is the relationship between these memories?\n" +
                                   $"A: Memory 1: {reflections[i].Content}\n" +
                                   $"Memory 2: {reflections[j].Content}";

                    var crossMeta = new Dictionary<string, object>
                    {
                        ["source_drawer_id"] = reflections[i].DrawerId + "," + reflections[j].DrawerId,
                        ["source_wing"] = wing,
                    };
                    await _store.StoreAsync(wing, "reflection", crossDoc,
                        role: "system", importance: 0.5,
                        ttlMs: null, metadata: crossMeta).ConfigureAwait(false);
                }
            }
        }
    }

    // ── Helpers ──

    [GeneratedRegex(@"^(?:[-*•]?\s*)(\w[\w\s]*?)(?:\s*[:：—\-–]\s*)(.+)$", RegexOptions.Multiline | RegexOptions.Compiled, 500)]
    private static partial Regex FactPattern();

    [GeneratedRegex(@"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)+)\b", RegexOptions.Compiled, 500)]
    private static partial Regex EntityPattern();

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var kw in keywords)
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
