// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  EventExtractor — SAG-style event + entity extraction from document chunks
//
//  For each document chunk, extracts:
//    1. One event — the core semantic unit (a few sentences describing what happens)
//    2. N entities — people, things, concepts, code symbols mentioned
//
//  Events and entities are stored as KgStore nodes with kind="event" and
//  kind="entity" respectively, linked via the entity name appearing in
//  the event text (no pre-built edges, enabling SAG-style dynamic hyperedges).
// ═══════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LTAI.Agent.Vector;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Indexing;

/// <summary>Result of event extraction for one chunk.</summary>
public sealed record ExtractionResult(
    string EventName,
    string EventSummary,
    IReadOnlyList<string> Entities,
    string Source);

/// <summary>
/// Extracts events and entities from document chunks.
/// Two modes:
///   1. LLM-based (high quality) — uses an LLM call to extract events + entities
///   2. Heuristic (zero LLM) — rule-based extraction for speed
/// </summary>
public sealed class EventExtractor
{
    private readonly KgStore _store;
    private readonly ILogger<EventExtractor> _logger;

    // Regular expression patterns for entity extraction
    private static readonly Regex CapitalizedWords = new(
        @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+)*\b", RegexOptions.Compiled);
    private static readonly Regex CodeSymbols = new(
        @"\b[A-Za-z_]\w*(?:<[^>]+>)?\b", RegexOptions.Compiled);

    public EventExtractor(KgStore store, ILogger<EventExtractor>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<EventExtractor>.Instance;
    }

    /// <summary>
    /// Extract events and entities from a document chunk.
    /// Uses heuristic extraction (zero LLM) for speed.
    /// </summary>
    public ExtractionResult Extract(string chunkText, string source, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(chunkText))
            return new ExtractionResult("", "", [], source);

        // Extract entities
        var entities = ExtractEntities(chunkText);

        // Generate event name
        var eventName = GenerateEventName(chunkText, title);

        // Generate event summary (first significant sentence or two)
        var summary = GenerateSummary(chunkText);

        return new ExtractionResult(eventName, summary, entities, source);
    }

    /// <summary>
    /// Extract and persist events + entities to the KgStore.
    /// Stores event as a node with kind="event" and entities as nodes with kind="entity".
    /// </summary>
    public async Task<bool> PersistAsync(string chunkText, string source, string? title = null,
        CancellationToken ct = default)
    {
        try
        {
            var result = Extract(chunkText, source, title);
            if (string.IsNullOrWhiteSpace(result.EventName)) return false;

            // Store event node
            var eventExtId = $"event:{source}:{result.EventName.GetHashCode():x8}";
            var props = new Dictionary<string, object?> { ["summary"] = result.EventSummary, ["entities"] = string.Join(", ", result.Entities) };
            var eventId = await _store.UpsertNode(
                extId: eventExtId,
                kind: "event",
                name: result.EventName,
                source: source,
                props: props).ConfigureAwait(false);

            // Add event text as doc
            await _store.ReplaceDocsAsync(eventId, [(result.EventSummary, "markdown", source)])
                .ConfigureAwait(false);

            // Store entity nodes
            foreach (var entityName in result.Entities.Distinct())
            {
                var entityExtId = $"entity:{source}:{entityName.GetHashCode():x8}";
                var entityId = await _store.UpsertNode(
                    extId: entityExtId,
                    kind: "entity",
                    name: entityName,
                    source: source).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EventExtractor: persistence failed for source '{Source}'", source);
            return false;
        }
    }

    // ─── Heuristic Extraction ───

    /// <summary>Extract named entities from text (capitalized terms, code symbols, domain terms).</summary>
    public static IReadOnlyList<string> ExtractEntities(string text)
    {
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Capitalized multi-word phrases (person names, organization names, product names)
        foreach (Match m in CapitalizedWords.Matches(text))
        {
            var word = m.Value.Trim();
            if (word.Length >= 3 && !StopWords.Contains(word.ToLowerInvariant()))
                entities.Add(word);
        }

        // 2. Code-like symbols (class names, function names, identifiers)
        foreach (Match m in CodeSymbols.Matches(text))
        {
            var word = m.Value.Trim();
            // Filter: PascalCase, camelCase_with_underscore, or ALL_CAPS
            if (word.Length >= 4 && (char.IsUpper(word[0]) || word.Contains('_')))
            {
                // Avoid common English words misidentified as symbols
                if (!CommonWords.Contains(word.ToLowerInvariant()))
                    entities.Add(word);
            }
        }

        // 3. Domain-specific technical terms from context (terms followed by parentheses)
        var techTerms = Regex.Matches(text, @"\b([A-Za-z]\w+)\s*\([^)]{0,100}\)");
        foreach (Match m in techTerms)
        {
            var term = m.Groups[1].Value.Trim();
            if (term.Length >= 3)
                entities.Add(term);
        }

        return entities.Take(20).ToList(); // Max 20 entities per chunk
    }

    /// <summary>Generate a concise event name from chunk text.</summary>
    public static string GenerateEventName(string text, string? title)
    {
        // Prefer title
        if (!string.IsNullOrWhiteSpace(title))
            return title.Trim();

        // First heading in text
        var headingMatch = Regex.Match(text, @"^#{1,3}\s+(.+)$", RegexOptions.Multiline);
        if (headingMatch.Success)
            return headingMatch.Groups[1].Value.Trim();

        // First sentence (up to 80 chars)
        var firstSentence = Regex.Match(text, @"^(.{10,80}?[.!?\n])");
        if (firstSentence.Success)
            return firstSentence.Groups[1].Value.Trim().Truncate(80);

        // First line
        var firstLine = text.Split('\n').FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(firstLine))
            return firstLine.Truncate(80);

        return "Untitled Event";
    }

    /// <summary>Generate a compact event summary.</summary>
    public static string GenerateSummary(string text)
    {
        // Clean text: remove markdown headers, code blocks, excessive whitespace
        var cleaned = Regex.Replace(text, @"```[\s\S]*?```", "");
        cleaned = Regex.Replace(cleaned, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");

        // Take first 2-3 meaningful sentences
        var sentences = cleaned
            .Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length >= 20)
            .Take(3)
            .ToList();

        if (sentences.Count > 0)
            return string.Join(". ", sentences) + ".";

        // Fallback: first 300 chars
        return cleaned.Length <= 300 ? cleaned : cleaned[..300] + "...";
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "this", "that", "with", "from", "into", "about", "which", "when", "where",
        "what", "how", "why", "there", "their", "your", "have", "will", "would", "could",
        "should", "shall", "can", "may", "might", "must", "been", "being", "having",
        "doing", "does", "did", "done", "going", "getting", "using", "based",
    };

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "private", "protected", "static", "void", "int", "string", "bool",
        "class", "interface", "struct", "enum", "record", "var", "new", "return",
        "if", "else", "for", "while", "foreach", "switch", "case", "break", "continue",
        "async", "await", "task", "null", "true", "false", "this", "base", "using",
        "import", "from", "namespace", "package", "module", "function", "method",
        "const", "let", "type", "typeof", "keyof", "extends", "implements",
    };
}

file static class StringExt
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
