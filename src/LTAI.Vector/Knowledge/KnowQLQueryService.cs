using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Vector.Knowledge;

public sealed record KnowQLQuery
{
    public string Ask { get; init; } = "";
    public string? Where { get; init; }
    public bool Ground { get; init; } = true;
    public JsonElement? Shape { get; init; }
    public bool Confidence { get; init; } = true;
    public KnowQLBudget? Budget { get; init; }
}

public sealed record KnowQLBudget
{
    public string Tier { get; init; } = "standard";
    public int MaxDepth { get; init; } = 3;
    public int MaxLatencyMs { get; init; } = 500;
    public int MaxTokenBudget { get; init; } = 8000;
}

public sealed record KnowQLResponse
{
    public string QueryId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Ask { get; init; } = "";
    public List<KnowQLField> Fields { get; init; } = new();
    public string? RawResponse { get; init; }
    public double OverallConfidence { get; init; }
    public KnowQLBudget? BudgetUsed { get; init; }
    public long LatencyMs { get; init; }
}

public sealed record KnowQLField
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public string Type { get; init; } = "string";
    public double Confidence { get; init; }
    public List<KnowQLCitation> Citations { get; init; } = new();
}

public sealed record KnowQLCitation
{
    public string SourceId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public double Confidence { get; init; }
    public string EvidenceSnippet { get; init; } = "";
    public string Tier { get; init; } = "standard";
}

public sealed class KnowQLQueryService
{
    private readonly ArtifactStore _artifactStore;
    private readonly ProvenanceTracker _provenanceTracker;
    private readonly AgenticRAG _agenticRAG;
    private readonly KnowledgeGraph _knowledgeGraph;
    private readonly ILogger<KnowQLQueryService>? _logger;

    private const int DefaultMaxDepth = 3;
    private const int DefaultMaxLatencyMs = 500;
    private const int DefaultTokenBudget = 8000;

    public KnowQLQueryService(
        ArtifactStore artifactStore,
        ProvenanceTracker provenanceTracker,
        AgenticRAG agenticRAG,
        KnowledgeGraph knowledgeGraph,
        ILogger<KnowQLQueryService>? logger = null)
    {
        _artifactStore = artifactStore;
        _provenanceTracker = provenanceTracker;
        _agenticRAG = agenticRAG;
        _knowledgeGraph = knowledgeGraph;
        _logger = logger;
    }

    public async Task<KnowQLResponse> ExecuteAsync(
        KnowQLQuery query, string? domain = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var budget = query.Budget ?? new KnowQLBudget
        {
            MaxDepth = DefaultMaxDepth,
            MaxLatencyMs = DefaultMaxLatencyMs,
            MaxTokenBudget = DefaultTokenBudget
        };

        KnowQLResponse result;

        if (!string.IsNullOrEmpty(query.Where) && query.Where.StartsWith("context:"))
        {
            var contextId = query.Where.Replace("context:", "").Trim();
            result = await ExecuteFromContext(query, domain, contextId, budget, ct);
        }
        else
        {
            result = await ExecuteFromArtifacts(query, domain, budget, ct);
        }

        sw.Stop();
        return result with { LatencyMs = sw.ElapsedMilliseconds };
    }

    private async Task<KnowQLResponse> ExecuteFromContext(
        KnowQLQuery query, string? domain, string contextId,
        KnowQLBudget budget, CancellationToken ct)
    {
        var context = _artifactStore.GetContext(contextId);
        var fields = new List<KnowQLField>();

        if (context != null)
        {
            var artifacts = _artifactStore.GetArtifactsByContext(contextId);
            foreach (var artifact in artifacts.Take(budget.MaxDepth))
            {
                foreach (var field in artifact.Fields)
                {
                    if (MatchesAsk(query.Ask, field.Name))
                    {
                        fields.Add(ConvertField(field, query));
                    }
                }
            }
        }

        if (fields.Count == 0)
        {
            var ragFields = await RetrieveFromRAG(query, domain, budget, ct);
            fields.AddRange(ragFields);
        }

        return BuildResponse(query, fields, budget);
    }

    private async Task<KnowQLResponse> ExecuteFromArtifacts(
        KnowQLQuery query, string? domain, KnowQLBudget budget, CancellationToken ct)
    {
        var fields = new List<KnowQLField>();
        var artifacts = domain != null
            ? _artifactStore.GetArtifactsByDomain(domain)
            : _artifactStore.QueryByTags(new() { "compiler" }, domain);

        foreach (var artifact in artifacts.Take(budget.MaxDepth))
        {
            foreach (var field in artifact.Fields)
            {
                if (MatchesAsk(query.Ask, field.Name) ||
                    string.IsNullOrEmpty(query.Ask) ||
                    query.Ask.Contains(field.Name, StringComparison.OrdinalIgnoreCase))
                {
                    fields.Add(ConvertField(field, query));
                }
            }
        }

        if (fields.Count == 0)
        {
            var ragFields = await RetrieveFromRAG(query, domain, budget, ct);
            fields.AddRange(ragFields);
        }

        return BuildResponse(query, fields, budget);
    }

    private async Task<List<KnowQLField>> RetrieveFromRAG(
        KnowQLQuery query, string? domain, KnowQLBudget budget, CancellationToken ct)
    {
        var fields = new List<KnowQLField>();
        var searchResults = _agenticRAG.Search(query.Ask, RAGMode.Iterative,
            domain: domain ?? "general");
        var results = searchResults.Take(budget.MaxDepth).ToList();

        foreach (var result in results)
        {
            fields.Add(new KnowQLField
            {
                Name = result.Id,
                Value = result.Content,
                Type = "text",
                Confidence = result.Score,
                Citations = new()
                {
                    new KnowQLCitation
                    {
                        SourceId = result.Source,
                        SourcePath = result.Source,
                        Confidence = result.Score,
                        EvidenceSnippet = result.Content.Length > 200
                            ? result.Content[..200] : result.Content,
                        Tier = budget.Tier
                    }
                }
            });
        }

        return fields;
    }

    private KnowQLResponse BuildResponse(
        KnowQLQuery query, List<KnowQLField> fields, KnowQLBudget budget)
    {
        double avgConfidence = fields.Count > 0
            ? fields.Average(f => f.Confidence)
            : 0;

        return new KnowQLResponse
        {
            Ask = query.Ask,
            Fields = fields,
            OverallConfidence = Math.Round(avgConfidence, 3),
            BudgetUsed = budget
        };
    }

    private KnowQLField ConvertField(ArtifactField field, KnowQLQuery query)
    {
        var citations = query.Ground
            ? field.Citations.Select(c => new KnowQLCitation
            {
                SourceId = c.SourceId,
                SourcePath = c.SourcePath,
                Confidence = c.Confidence,
                EvidenceSnippet = c.EvidenceSnippet,
                Tier = query.Budget?.Tier ?? "standard"
            }).ToList()
            : new();

        var cf = new KnowQLField
        {
            Name = field.Name,
            Value = field.Value,
            Type = field.Type,
            Confidence = query.Confidence ? field.Confidence : 0,
            Citations = citations
        };

        return cf;
    }

    private static bool MatchesAsk(string ask, string fieldName)
    {
        if (string.IsNullOrEmpty(ask)) return true;
        var askLower = ask.ToLower();
        var fieldLower = fieldName.ToLower().Replace("_", " ");
        return askLower.Contains(fieldLower) ||
               fieldLower.Contains(askLower) ||
               askLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                   .Any(w => fieldLower.Contains(w));
    }

    public string SerializeResponse(KnowQLResponse response)
    {
        return JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    public KnowQLQuery ParseQuery(string json)
    {
        return JsonSerializer.Deserialize<KnowQLQuery>(json,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            }) ?? new KnowQLQuery();
    }

    public Dictionary<string, object> GetQueryStats()
    {
        return new()
        {
            ["total_contexts"] = _artifactStore.GetAllContexts().Count,
            ["query_primitives"] = new[] { "ask", "where", "ground", "shape", "confidence", "budget" }
        };
    }
}
