using System.Text.Json;
using LTAI.Agent.Vector;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Indexing;

public sealed class KnowledgeExtractor
{
    private readonly KgStore _kg;
    private readonly IChatClient _llm;
    private readonly ILogger<KnowledgeExtractor> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public KnowledgeExtractor(KgStore kg, IChatClient llm, ILogger<KnowledgeExtractor> logger)
    {
        _kg = kg;
        _llm = llm;
        _logger = logger;
    }

    public async Task<string> ExtractFromDocumentAsync(
        long nodeId, string content, string? title = null, CancellationToken ct = default)
    {
        var prompt = $$"""
            从以下文档中提取关键知识，返回 JSON 数组（每条包含 "concept" 和 "summary"）：

            标题：{{title ?? "untitled"}}
            内容：
            {{content[..Math.Min(content.Length, 4000)]}}

            JSON 输出格式：
            [{"concept": "...", "summary": "..."}]
            """;

        try
        {
            var response = await _llm.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct).ConfigureAwait(false);
            var json = ExtractJson(response.Messages?.FirstOrDefault()?.Text ?? "[]");
            var facts = JsonSerializer.Deserialize<List<ExtractedFact>>(json, JsonOpts);
            if (facts == null || facts.Count == 0) return "No facts extracted";

            int ok = 0;
            foreach (var fact in facts)
            {
                var extId = $"fact:{nodeId}:{fact.Concept.GetHashCode():x}";
                var factId = await _kg.UpsertNode(
                    extId, "fact", fact.Concept,
                    source: $"node:{nodeId}",
                    props: new() { ["summary"] = fact.Summary }
                ).ConfigureAwait(false);

                await _kg.AddEdge(nodeId, factId, "HAS_FACT", weight: 0.8).ConfigureAwait(false);
                ok++;
            }

            _logger.LogInformation("Extracted {N} facts from node {Id}", ok, nodeId);
            return $"Extracted {ok} facts";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Knowledge extraction failed for node {Id}", nodeId);
            return $"Extraction failed: {ex.Message}";
        }
    }

    public async Task<string> ExtractFromTextAsync(
        string text, string source, CancellationToken ct = default)
    {
        var extId = $"inline:{source}:{text.GetHashCode():x}";
        var nodeId = await _kg.UpsertNode(
            extId, "note", source,
            source: source,
            props: new() { ["text"] = text[..Math.Min(text.Length, 500)] }
        ).ConfigureAwait(false);

        return await ExtractFromDocumentAsync(nodeId, text, source, ct).ConfigureAwait(false);
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end < 0) return "[]";
        return text[start..(end + 1)];
    }

    private sealed record ExtractedFact(string Concept, string Summary);
}
