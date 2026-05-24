using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record QueryClassification
{
    public string Intent { get; init; } = "deep";
    public List<string> Emotions { get; init; } = new();
    public string Domain { get; init; } = "general";
    public bool IsVague { get; init; }
    public List<string> SuggestedTools { get; init; } = new();

    public static QueryClassification Default => new();
}

public sealed class UnifiedQueryClassifier
{
    private readonly ILogger<UnifiedQueryClassifier>? _logger;
    private readonly ConcurrentDictionary<string, QueryClassification> _cache = new();

    public UnifiedQueryClassifier(ILogger<UnifiedQueryClassifier>? logger = null)
    {
        _logger = logger;
    }

    public async Task<QueryClassification> ClassifyAsync(
        string query, IChatClient llm, string flashModel, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(query, out var cached))
            return cached;

        try
        {
            var prompt = $"""
                Classify this query. Output ONLY a JSON object with these fields:
                - intent: "fast" (simple/quick/greeting), "deep" (complex/analysis/multi-step), or "reflex" (direct command)
                - emotions: array of detected emotions from [urgent, angry, confused, happy, sad, neutral]
                - domain: "code", "document", "knowledge", "network", "system", or "general"
                - vague: true if the query is ambiguous/missing key details, false if specific/actionable
                - tools: array of tool names from [web_search, filesystem_list, filesystem_read, shell_exec, git_log, git_diff, env_sysinfo, env_processes, datetime_now] that would help answer

                Query: "{query}"

                JSON:
                """;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You classify user queries. Output ONLY valid JSON. No explanation."),
                new(ChatRole.User, prompt)
            };
            var options = new ChatOptions
            {
                ModelId = flashModel,
                Temperature = 0f,
                MaxOutputTokens = 256,
                Tools = new List<AITool>()
            };

            var result = await llm.GetResponseAsync(messages, options, ct);
            var json = result.Text?.Trim() ?? "";

            var classification = ParseClassification(json);
            _cache[query] = classification;
            return classification;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unified classification failed, using defaults");
            return QueryClassification.Default;
        }
    }

    private static QueryClassification ParseClassification(string json)
    {
        try
        {
            // Strip markdown fences
            if (json.StartsWith("```"))
            {
                var end = json.LastIndexOf("```", StringComparison.Ordinal);
                if (end > 3) json = json[3..end].Trim();
                if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                    json = json[4..].Trim();
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new QueryClassification
            {
                Intent = root.TryGetProperty("intent", out var i) ? i.GetString() ?? "deep" : "deep",
                Emotions = root.TryGetProperty("emotions", out var e)
                    ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : new(),
                Domain = root.TryGetProperty("domain", out var d) ? d.GetString() ?? "general" : "general",
                IsVague = root.TryGetProperty("vague", out var v) && v.GetBoolean(),
                SuggestedTools = root.TryGetProperty("tools", out var t)
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : new()
            };
        }
        catch
        {
            return QueryClassification.Default;
        }
    }

    public void ClearCache() => _cache.Clear();
}
