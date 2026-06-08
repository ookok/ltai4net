// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  EntityLinker — text → knowledge graph entity linking
//
//  Phase 4a: identifies entities in user queries and links them to
//  KgStore nodes via fuzzy name matching + kind-aware scoring.
//
//  Pipeline:
//    1. Tokenize query (split by whitespace/punctuation)
//    2. Fuzzy match tokens against KgStore node names
//    3. Score candidates by edit distance + kind priority
//    4. Return linked entity IDs + their neighbors
//
//  Fallback: when entity linking fails, falls back to FTS5 search.
// ═══════════════════════════════════════════════════════════════

using System.Text.RegularExpressions;

namespace LTAI.Agent.Vector.GraphRAG;

/// <summary>
/// Links text (user query) to knowledge graph entities via fuzzy
/// name matching. Uses a two-phase approach:
///
/// Phase 1 (fast): exact + prefix match against KgStore node names.
/// Phase 2 (fuzzy): edit-distance match for partial/approximate names.
///
/// Thread-safe after construction (KgStore access is async).
/// </summary>
public sealed partial class EntityLinker
{
    private readonly KgStore _store;
    private readonly int _maxCandidates;

    /// <summary>
    /// A linked entity candidate.
    /// </summary>
    /// <param name="NodeId">KgStore node ID.</param>
    /// <param name="Name">Entity name.</param>
    /// <param name="Kind">Entity kind (class, method, concept, etc.).</param>
    /// <param name="Score">Match confidence (0-1).</param>
    /// <param name="MatchType">How the match was made.</param>
    public sealed record LinkedEntity(
        long NodeId,
        string Name,
        string Kind,
        float Score,
        MatchType MatchType);

    /// <summary>How the entity was matched.</summary>
    public enum MatchType
    {
        Exact,     // exact string match
        Prefix,    // query token is a prefix of the entity name
        Fuzzy,     // edit distance match
        Substring, // query token found within entity name
        Fallback,  // FTS5 search fallback
    }

    public EntityLinker(KgStore store, int maxCandidates = 10)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _maxCandidates = maxCandidates;
    }

    /// <summary>
    /// Link tokens in a query to knowledge graph entities.
    /// Returns matched entities sorted by score (descending).
    /// </summary>
    /// <param name="query">User query text.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<LinkedEntity>> LinkAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var tokens = Tokenize(query);
        if (tokens.Length == 0) return [];

        var candidates = new List<LinkedEntity>();

        // Phase 1: Try exact + prefix match against all node names
        foreach (var token in tokens)
        {
            if (token.Length < 2) continue;

            var matched = await MatchTokenAsync(token, ct).ConfigureAwait(false);
            candidates.AddRange(matched);
        }

        // Phase 2: Fallback to FTS5 if no exact/prefix matches
        if (candidates.Count == 0)
        {
            var ftsHits = await _store.SearchFts(query, topN: 5).ConfigureAwait(false);
            foreach (var (nodeId, text, rank, kind) in ftsHits)
            {
                candidates.Add(new LinkedEntity(
                    nodeId, text.Length > 100 ? text[..100] : text,
                    kind, (float)(rank > 0 ? Math.Min(rank / 10.0, 1.0) : 0.3),
                    MatchType.Fallback));
            }
        }

        // Deduplicate and sort
        return candidates
            .GroupBy(e => e.NodeId)
            .Select(g => g.OrderByDescending(e => e.Score).First())
            .OrderByDescending(e => e.Score)
            .Take(_maxCandidates)
            .ToList();
    }

    /// <summary>
    /// Link a single token to KgStore nodes. Uses exact match first,
    /// then prefix match, then fuzzy/substring.
    /// </summary>
    private async Task<List<LinkedEntity>> MatchTokenAsync(string token, CancellationToken ct)
    {
        var results = new List<LinkedEntity>();
        var allNodes = await _store.GetAllNodes().ConfigureAwait(false);

        foreach (var node in allNodes)
        {
            if (string.IsNullOrEmpty(node.Name)) continue;

            var name = node.Name.Trim();
            float score;
            MatchType matchType;

            if (string.Equals(token, name, StringComparison.OrdinalIgnoreCase))
            {
                score = 1.0f;
                matchType = MatchType.Exact;
            }
            else if (name.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                     && token.Length >= 3)
            {
                score = 0.8f;
                matchType = MatchType.Prefix;
            }
            else if (name.Contains(token, StringComparison.OrdinalIgnoreCase)
                     && token.Length >= 4)
            {
                score = 0.5f;
                matchType = MatchType.Substring;
            }
            else if (ComputeLevenshtein(token, name) <= 2 && token.Length >= 4)
            {
                score = 0.6f;
                matchType = MatchType.Fuzzy;
            }
            else
            {
                continue;
            }

            // Kind-based boost
            score = node.Kind.ToLowerInvariant() switch
            {
                "class" => score * 1.2f,
                "method" or "function" => score * 1.1f,
                "interface" => score * 1.1f,
                "document" => score * 0.9f,
                _ => score,
            };

            results.Add(new LinkedEntity(
                node.Id, node.Name, node.Kind,
                Math.Min(score, 1.0f), matchType));
        }

        return results;
    }

    /// <summary>
    /// Tokenize query into meaningful tokens for matching.
    /// Handles mixed Chinese/English text and code identifiers.
    /// </summary>
    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex TokenPattern();

    private static string[] Tokenize(string text)
    {
        // Split by whitespace and common punctuation
        return TokenPattern().Matches(text)
            .Select(m => m.Value)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Levenshtein edit distance (capped at 3 for efficiency).
    /// </summary>
    private static int ComputeLevenshtein(string s, string t)
    {
        int n = s.Length, m = t.Length;
        if (n == 0) return Math.Min(m, 3);
        if (m == 0) return Math.Min(n, 3);
        if (Math.Abs(n - m) > 3) return 4; // early exit

        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int j = 1; j <= m; j++)
        {
            for (int i = 1; i <= n; i++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(
                    d[i - 1, j] + 1,
                    d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
                if (d[i, j] > 3) d[i, j] = 4; // cap at 3
            }
        }
        return d[n, m];
    }
}
