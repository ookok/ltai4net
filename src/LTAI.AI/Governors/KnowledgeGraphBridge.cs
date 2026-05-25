using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Core.Models;
using Microsoft.Extensions.AI;
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
    private readonly IChatClient? _llm;
    private readonly ILogger<KnowledgeGraphBridge> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public KnowledgeGraphBridge(KnowledgeGraph graph, IChatClient? llm = null, ILogger<KnowledgeGraphBridge>? logger = null)
    {
        _graph = graph;
        _llm = llm;
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

    public async Task<int> IngestWithJsonModeAsync(string content, CancellationToken ct = default)
    {
        if (_llm is null)
        {
            _logger.LogWarning("KnowledgeGraphBridge: Cannot use JSON mode, no IChatClient configured");
            return 0;
        }

        var prompt = $$"""
            Extract all entities and relations from the following text. Output ONLY valid JSON with no additional text.

            {
              "entities": [
                { "id": "unique_entity_id", "label": "Human-readable entity name", "properties": { "key": "value" } }
              ],
              "relations": [
                { "subject": "entity_id_from_entities", "predicate": "relationship_name", "object": "entity_id_from_entities", "source_text": "original text fragment", "confidence": 0.9 }
              ]
            }

            Text to analyze:
            {{content}}
            """;

        try
        {
            var response = await _llm.GetResponseAsync(
                prompt,
                new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.Json,
                    Temperature = 0.1f
                },
                ct).ConfigureAwait(false);

            var json = response.Text ?? "";
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("KnowledgeGraphBridge: Empty LLM response for JSON extraction");
                return 0;
            }

            var extraction = JsonSerializer.Deserialize<JsonGraphExtraction>(json, JsonOpts);
            if (extraction is null)
            {
                _logger.LogWarning("KnowledgeGraphBridge: Failed to deserialize JSON extraction");
                return 0;
            }

            int count = 0;

            if (extraction.Entities is not null)
            {
                foreach (var e in extraction.Entities)
                {
                    var entityId = string.IsNullOrWhiteSpace(e.Id) ? KnowledgeGraph.EntityId(e.Label) : e.Id;
                    _graph.AddEntity(new Entity(entityId, e.Label, e.Properties));
                    count++;
                }
            }

            if (extraction.Relations is not null && extraction.Entities is not null)
            {
                var entityIds = new HashSet<string>(extraction.Entities
                    .Select(e => string.IsNullOrWhiteSpace(e.Id) ? KnowledgeGraph.EntityId(e.Label) : e.Id));

                foreach (var r in extraction.Relations)
                {
                    var subjId = entityIds.Contains(r.Subject) ? r.Subject : KnowledgeGraph.EntityId(r.Subject);
                    var objId = entityIds.Contains(r.Object) ? r.Object : KnowledgeGraph.EntityId(r.Object);
                    var props = new Dictionary<string, object?>();
                    if (!string.IsNullOrEmpty(r.SourceText)) props["source_text"] = r.SourceText;
                    if (r.Confidence.HasValue) props["confidence"] = r.Confidence.Value;
                    _graph.AddRelation(subjId, objId, r.Predicate, props!);
                    count++;
                }
            }

            _logger.LogInformation("Knowledge graph ingested {Count} items from JSON extraction", count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KnowledgeGraphBridge: JSON extraction failed");
            return 0;
        }
    }

    public int IngestTeachingResult(string query, L2TeachingResult teaching, bool useJsonMode = false)
    {
        if (useJsonMode)
        {
            var text = $"Query: {query}\n\n" +
                       $"Answer: {teaching.Answer}\n\n" +
                       $"Reasoning: {teaching.ReasoningSteps}\n\n" +
                       $"Concepts: {teaching.KeyConcepts}\n\n" +
                       $"Explanation: {teaching.SimplifiedExplanation}";
            return IngestWithJsonModeAsync(text).GetAwaiter().GetResult();
        }

        var triplets = ExtractTripletsFromTeaching(query, teaching);
        var count = _graph.AddTripletsToGraph(triplets);
        _logger.LogInformation("Knowledge graph ingested {Count} triplets from L2 teaching", count);
        return count;
    }

    public int IngestExperience(string query, string response, string label, bool useJsonMode = false)
    {
        var combinedText = $"{query}. {response}";

        if (useJsonMode)
        {
            return IngestWithJsonModeAsync(combinedText).GetAwaiter().GetResult();
        }

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

internal sealed class JsonGraphEntity
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public Dictionary<string, object>? Properties { get; set; }
}

internal sealed class JsonGraphRelation
{
    public string Subject { get; set; } = "";
    public string Predicate { get; set; } = "";
    public string Object { get; set; } = "";
    public string? SourceText { get; set; }
    public double? Confidence { get; set; }
}

internal sealed class JsonGraphExtraction
{
    public List<JsonGraphEntity>? Entities { get; set; }
    public List<JsonGraphRelation>? Relations { get; set; }
}
