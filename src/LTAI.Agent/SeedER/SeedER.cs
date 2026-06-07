using System.Text;
using LTAI.Agent.Formats;
using LTAI.Agent.Vector;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.SeedER;

/// <summary>
/// SeedER (Structural Entity Discovery & Exploratory Retrieval):
/// knowledge graph retrieval that replaces flat similarity matching with
/// structured path exploration and multi-hop reasoning.
///
/// GoS-inspired pipeline:
///   1. Entity Linking — locate seed nodes in KG from user query
///   2. GoS Loop — FSM-guided exploration with drill-down / backtrack:
///      a. Explore paths at current depth (preferring refinement edges)
///      b. Extract frontier (highest-confidence path)
///      c. FSM: advance to next level, backtrack, or report
///   3. Path Reasoning (optional) — LLM scores paths for relevance
///   4. Answer Construction — build consolidated answer from top paths
/// </summary>
public sealed class SeedER
{
    private readonly KgStore _store;
    private readonly PathExplorer _explorer;
    private readonly IChatClient? _llm;
    private readonly ILogger<SeedER>? _logger;

    public SeedER(KgStore store, PathExplorer explorer,
        IChatClient? llm = null, ILogger<SeedER>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _explorer = explorer ?? throw new ArgumentNullException(nameof(explorer));
        _llm = llm;
        _logger = logger;
    }

    /// <summary>
    /// Run the full SeedER pipeline with GoS-inspired FSM-guided exploration.
    /// </summary>
    public async Task<SeedERResult> ExploreAsync(
        string query,
        int maxDepth = 3,
        int maxPaths = 50,
        HashSet<string>? includeRelations = null,
        HashSet<string>? excludeRelations = null,
        bool enableReasoning = true,
        CancellationToken ct = default)
    {
        _logger?.LogInformation("SeedER: query=\"{Q}\" depth={D} paths={P}", query, maxDepth, maxPaths);

        // Step 1: Entity linking — find seed nodes from query
        var seeds = await LinkEntitiesAsync(query, ct).ConfigureAwait(false);
        _logger?.LogInformation("SeedER: found {N} seed entities", seeds.Count);

        if (seeds.Count == 0)
        {
            return new SeedERResult
            {
                Query = query,
                EntitiesFound = 0,
                PathsExplored = 0,
                ConsolidatedAnswer = "No relevant entities found in the knowledge graph."
            };
        }

        var seedIds = seeds.Select(s => s.Id).ToList();

        // Step 2: GoS-inspired FSM-guided exploration loop
        var fsm = new BeliefFSM(gapDelta: 0.3, minSupport: 2, maxSteps: 2);
        var allPaths = new List<ExplorationPath>();
        int? backtrackLevel = null;

        while (fsm.StateLabel != "report" && fsm.TotalSteps < maxDepth * 2)
        {
            fsm.TickStep();
            _logger?.LogInformation("SeedER: GoS round={R} level={L} label={Label}",
                fsm.TotalSteps, fsm.State, fsm.StateLabel);

            // Explore at current FSM state depth
            var roundPaths = await _explorer.ExploreAsync(
                seedIds,
                maxDepth: fsm.State,
                maxPaths: maxPaths,
                includeRelations: includeRelations,
                excludeRelations: excludeRelations,
                preferRefinements: true,
                backtrackPruneLevel: backtrackLevel,
                cancellationToken: ct).ConfigureAwait(false);

            // Merge new paths
            foreach (var p in roundPaths)
            {
                if (!allPaths.Any(x => x.Target.Id == p.Target.Id && x.Length == p.Length))
                    allPaths.Add(p);
            }

            if (allPaths.Count == 0) break;

            // FSM: should we advance?
            var shouldAdvance = fsm.MaybeAdvance(allPaths);

            // FSM: check backtrack on the frontier path
            var frontier = fsm.ExtractFrontier(allPaths);
            if (frontier != null && fsm.State > 1)
            {
                var backtrackTo = fsm.CheckBacktrack(frontier, allPaths);
                if (backtrackTo.HasValue)
                {
                    _logger?.LogInformation("SeedER: backtrack to level {L}", backtrackTo.Value);
                    fsm.SetState(backtrackTo.Value);
                    backtrackLevel = backtrackTo.Value;

                    // Prune paths below backtrack level
                    var keepNodeId = frontier.Steps[backtrackTo.Value].Node.Id;
                    allPaths = PathExplorer.PruneBelowLevel(allPaths, backtrackTo.Value, keepNodeId);
                    continue; // re-explore from this level
                }
            }

            if (shouldAdvance)
            {
                if (fsm.State >= maxDepth)
                {
                    fsm.StateLabel = "report";
                    _logger?.LogInformation("SeedER: max depth reached, reporting");
                }
                else
                {
                    fsm.SetState(fsm.State + 1);
                    backtrackLevel = null;
                    _logger?.LogInformation("SeedER: advancing to level {L}", fsm.State);
                }
            }
            else
            {
                _logger?.LogInformation("SeedER: staying at level {L}, gathering more evidence", fsm.State);
            }

            // Safety: max steps regardless of FSM state
            if (fsm.TotalSteps >= maxDepth * 2)
            {
                fsm.StateLabel = "report";
            }
        }

        _logger?.LogInformation("SeedER: GoS loop done, {N} total paths", allPaths.Count);

        // Step 3: LLM path reasoning
        List<ExplorationPath> reasonedPaths = allPaths;
        string? llmReasoning = null;

        if (enableReasoning && _llm != null && allPaths.Count > 0)
        {
            (reasonedPaths, llmReasoning) = await ReasonPathsAsync(
                query, allPaths, ct).ConfigureAwait(false);
        }

        // Step 4: Build answer
        var answer = BuildAnswer(query, reasonedPaths, seeds);

        return new SeedERResult
        {
            Query = query,
            Paths = allPaths,
            ReasoningPaths = reasonedPaths,
            EntitiesFound = seeds.Count,
            PathsExplored = allPaths.Count,
            LlmReasoning = llmReasoning,
            ConsolidatedAnswer = answer,
            FsmLevel = fsm.State,
            FsmLabel = fsm.StateLabel,
            FsmTotalSteps = fsm.TotalSteps,
        };
    }

    /// <summary>
    /// Entity linking: find seed nodes by matching query against the KG.
    /// Uses FTS5 + kind-weighted scoring to find the most relevant entities.
    /// </summary>
    internal async Task<List<NodeRow>> LinkEntitiesAsync(
        string query, CancellationToken ct = default)
    {
        // Strategy 1: FTS5 BM25 search across all entity kinds
        var ftsResults = await _store.SearchFts(query, topN: 15).ConfigureAwait(false);

        var seen = new HashSet<long>();
        var entities = new List<NodeRow>();

        foreach (var (nodeId, _, rank, _) in ftsResults.OrderByDescending(r => r.rank))
        {
            if (!seen.Add(nodeId)) continue;
            var node = await _store.GetNode(nodeId).ConfigureAwait(false);
            if (node != null) entities.Add(node);
            if (entities.Count >= 10) break;
        }

        if (entities.Count > 0) return entities;

        // Strategy 2: fallback to name-based search
        var nameResults = await _store.SearchNodesByName(query, 10).ConfigureAwait(false);
        if (nameResults.Count > 0) return nameResults;

        // Strategy 3: try each word as a separate FTS5 query
        var words = query.Split([' ', '\n', '\r', ',', '.', '，', '。', '、'],
            StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .Distinct()
            .ToList();

        foreach (var word in words)
        {
            var wordResults = await _store.SearchFts(word, topN: 5).ConfigureAwait(false);
            foreach (var (nodeId, _, _, _) in wordResults)
            {
                if (!seen.Add(nodeId)) continue;
                var node = await _store.GetNode(nodeId).ConfigureAwait(false);
                if (node != null) entities.Add(node);
                if (entities.Count >= 5) break;
            }
            if (entities.Count >= 5) break;
        }

        return entities;
    }

    internal async Task<(List<ExplorationPath> ranked, string? reasoning)> ReasonPathsAsync(
        string query, List<ExplorationPath> paths, CancellationToken ct = default)
    {
        if (_llm == null || paths.Count == 0)
            return (paths, null);

        var prompt = BuildReasoningPrompt(query, paths);

        try
        {
            var response = await _llm.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System,
                    "You are a knowledge graph path reasoning assistant. " +
                    "Analyze each exploration path and determine which paths are most " +
                    "relevant to answering the user's query. " +
                    "Output a JSON object with: {\"scores\": [0-10 per path], " +
                    "\"reasoning\": \"brief analysis of the evidence chain\"}"),
                new ChatMessage(ChatRole.User, prompt)
            ], cancellationToken: ct).ConfigureAwait(false);

            var text = response.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return (paths, null);

            return ParseReasoningResponse(text, paths);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SeedER: LLM reasoning failed, using structural scores");
            return (paths, null);
        }
    }

    private static string BuildReasoningPrompt(string query, List<ExplorationPath> paths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("query:");
        sb.Append(' ', 2); sb.AppendLine(ToonWriter.Quote(query));
        sb.AppendLine();
        sb.AppendLine($"paths[{paths.Count}]:");
        sb.AppendLine();

        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            sb.Append(' ', 2); sb.AppendLine($"# path {i + 1} score={path.Score:F3}");
            sb.Append(' ', 2); sb.AppendLine(path.ToToonString());
            sb.AppendLine();
        }

        sb.AppendLine("Score each path 0-10 for relevance. Respond with JSON:");
        sb.AppendLine("{\"scores\": [score_per_path], \"reasoning\": \"analysis\"}");

        return sb.ToString();
    }

    private static (List<ExplorationPath> ranked, string? reasoning) ParseReasoningResponse(
        string text, List<ExplorationPath> paths)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            string? reasoning = null;
            if (root.TryGetProperty("reasoning", out var r))
                reasoning = r.GetString();

            List<double>? scores = null;
            if (root.TryGetProperty("scores", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                scores = s.EnumerateArray()
                    .Select(e => Math.Clamp(e.GetDouble() / 10.0, 0, 1))
                    .ToList();
            }

            if (scores != null && scores.Count == paths.Count)
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    paths[i].Score = paths[i].Score * 0.3 + scores[i] * 0.7;
                }

                var ranked = paths.OrderByDescending(p => p.Score).ToList();
                return (ranked, reasoning);
            }
        }
        catch
        {
        }

        return (paths.OrderByDescending(p => p.Score).ToList(), null);
    }

    private static string BuildAnswer(string query, List<ExplorationPath> paths, List<NodeRow> seeds)
    {
        var sb = new StringBuilder();

        if (paths.Count == 0)
        {
            sb.AppendLine("## Relevant Entities");
            foreach (var seed in seeds)
            {
                sb.AppendLine($"- [{seed.Kind}] {seed.Name}" +
                    (string.IsNullOrEmpty(seed.Namespace) ? "" : $" ({seed.Namespace})"));
            }
            sb.AppendLine();
            sb.AppendLine("No structured paths found beyond these entities.");
            return sb.ToString();
        }

        sb.AppendLine("## Evidence Chains");
        for (int i = 0; i < Math.Min(paths.Count, 5); i++)
        {
            var path = paths[i];
            sb.AppendLine($"### Chain {i + 1} (confidence: {path.Score:F2})");
            sb.AppendLine(path.ToPathString());
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
