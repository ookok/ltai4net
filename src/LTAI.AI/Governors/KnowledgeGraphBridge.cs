using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record GraphKnowledgeResult
{
    public string Answer { get; init; } = "";
    public List<string> RelatedEntities { get; init; } = new();
    public List<Triplet> SupportingTriplets { get; init; } = new();
    public bool FoundInGraph { get; init; }
}

public sealed class KnowledgeGraphBridge
{
    private readonly KnowledgeGraph _graph;
    private readonly ILogger<KnowledgeGraphBridge> _logger;

    public KnowledgeGraphBridge(KnowledgeGraph graph, ILogger<KnowledgeGraphBridge>? logger = null)
    {
        _graph = graph;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeGraphBridge>.Instance;
    }

    public GraphKnowledgeResult QueryKnowledge(string query)
    {
        var entities = _graph.EntityLinking(query);
        if (entities.Count == 0)
            return new GraphKnowledgeResult { FoundInGraph = false };

        var triplets = _graph.GetTriplets()
            .Where(t => entities.Contains(EntityId(t.Subject)) || entities.Contains(EntityId(t.Object)))
            .Take(10)
            .ToList();

        if (triplets.Count == 0)
            return new GraphKnowledgeResult
            {
                FoundInGraph = true,
                RelatedEntities = entities
            };

        var answer = BuildAnswerFromTriplets(query, triplets);

        return new GraphKnowledgeResult
        {
            Answer = answer,
            FoundInGraph = true,
            RelatedEntities = entities,
            SupportingTriplets = triplets
        };
    }

    public int IngestTeachingResult(string query, L2TeachingResult teaching)
    {
        var triplets = ExtractTripletsFromTeaching(query, teaching);
        var count = _graph.AddTripletsToGraph(triplets);
        _logger.LogInformation("Knowledge graph ingested {Count} triplets from L2 teaching", count);
        return count;
    }

    public int IngestExperience(string query, string response, string label)
    {
        var combinedText = $"{query}. {response}";
        var triplets = KnowledgeGraph.ExtractTripletsRegex(combinedText);
        var count = _graph.AddTripletsToGraph(triplets);
        if (count > 0)
            _logger.LogDebug("Knowledge graph ingested {Count} triplets from experience", count);
        return count;
    }

    public Dictionary<string, object> GetGraphStats() => _graph.GetStats();

    private static List<Triplet> ExtractTripletsFromTeaching(string query, L2TeachingResult teaching)
    {
        var triplets = new List<Triplet>();

        var concepts = teaching.KeyConcepts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var queryEntity = ExtractMainEntity(query);

        foreach (var concept in concepts)
        {
            triplets.Add(new Triplet(queryEntity, "relates_to", concept.Trim(), "", 0.8f));
        }

        if (!string.IsNullOrEmpty(teaching.ReasoningSteps))
        {
            var steps = Regex.Split(teaching.ReasoningSteps, @"Step\s*\d+[:.]?\s*");
            for (int i = 0; i < steps.Length - 1; i++)
            {
                if (!string.IsNullOrWhiteSpace(steps[i]) && !string.IsNullOrWhiteSpace(steps[i + 1]))
                {
                    var stepEntity = ExtractMainEntity(steps[i]);
                    var nextEntity = ExtractMainEntity(steps[i + 1]);
                    if (!string.IsNullOrEmpty(stepEntity) && !string.IsNullOrEmpty(nextEntity))
                        triplets.Add(new Triplet(stepEntity, "leads_to", nextEntity, "", 0.6f));
                }
            }
        }

        var simplifiedEntity = ExtractMainEntity(teaching.SimplifiedExplanation);
        if (!string.IsNullOrEmpty(simplifiedEntity) && simplifiedEntity != queryEntity)
        {
            triplets.Add(new Triplet(queryEntity, "explained_as", simplifiedEntity, "", 0.7f));
        }

        var directTriplets = KnowledgeGraph.ExtractTripletsRegex(teaching.Answer);
        triplets.AddRange(directTriplets);

        return triplets;
    }

    private static string ExtractMainEntity(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unknown";

        var trimmed = text.Trim();
        var patterns = new[]
        {
            @"^(?:The\s+)?(\w+(?:\s+\w+){0,3})\s+(?:is|are|was|were|has|have)",
            @"^(\w+(?:\s+\w+){0,3})\s+(?:是|有|属于)",
            @"^(\w+(?:\s+\w+){0,3})\s+(?:refers to|means|represents)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups[1].Value.Trim();
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[0] : "unknown";
    }

    private static string BuildAnswerFromTriplets(string query, List<Triplet> triplets)
    {
        var entities = new HashSet<string>();
        var relations = new List<string>();

        foreach (var t in triplets)
        {
            entities.Add(t.Subject);
            entities.Add(t.Object);
            relations.Add($"{t.Subject} {t.Predicate} {t.Object}");
        }

        if (relations.Count == 0)
            return "I found related information but cannot form a complete answer.";

        return $"Based on knowledge graph: {string.Join("; ", relations)}";
    }

    private static string EntityId(string label) => KnowledgeGraph.EntityId(label);
}
