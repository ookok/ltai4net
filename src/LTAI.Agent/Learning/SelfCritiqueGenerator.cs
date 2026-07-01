using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Learning;

public sealed record SelfCritique
{
    public string Category { get; init; } = "";
    public string Issue { get; init; } = "";
    public double Severity { get; init; }
    public string? FixSuggestion { get; init; }
}

public sealed class SelfCritiqueGenerator
{
    private readonly IChatClient? _critic;
    private readonly ILogger<SelfCritiqueGenerator> _logger;
    private static readonly double _severityThreshold = 0.5;

    public SelfCritiqueGenerator(IChatClient? critic = null, ILogger<SelfCritiqueGenerator>? logger = null)
    {
        _critic = critic;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SelfCritiqueGenerator>.Instance;
    }

    public async Task<List<SelfCritique>> GenerateCritiqueAsync(
        string query, string response, List<string> toolResults, CancellationToken ct)
    {
        if (_critic == null)
        {
            _logger.LogDebug("SelfCritiqueGenerator: no critic LLM configured");
            return [];
        }

        try
        {
            var prompt = $@"You are a self-critique assistant. Analyze the following AI response and identify specific issues.
Output ONLY a JSON array of issues. Each issue has: category, issue (brief description), severity (0.0-1.0), fix_suggestion (optional).

Categories: completeness, hallucination_risk, clarity, verbosity, tool_usage

Query: {query}

Tool Results:
{string.Join("\n", toolResults.Select(r => r.Length > 200 ? r[..200] + "..." : r))}

Response to critique:
{response}";
            var result = await _critic.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], null, ct).ConfigureAwait(false);
            var text = result.Text ?? "";
            return ParseCritique(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SelfCritiqueGenerator failed");
            return [];
        }
    }

    public bool HasSignificantIssues(List<SelfCritique> critiques)
    {
        return critiques.Any(c => c.Severity >= _severityThreshold);
    }

    public string BuildRefinePrompt(string query, string originalResponse, List<SelfCritique> critiques)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Please refine your previous response based on the following self-critique:");
        sb.AppendLine();
        sb.AppendLine("Original query: " + query);
        sb.AppendLine();
        sb.AppendLine("Critique:");
        foreach (var c in critiques.Where(c => c.Severity >= _severityThreshold))
        {
            sb.AppendLine($"- [{c.Category}] ({c.Severity:P0}) {c.Issue}");
            if (!string.IsNullOrEmpty(c.FixSuggestion))
                sb.AppendLine("  Fix: " + c.FixSuggestion);
        }
        sb.AppendLine();
        sb.AppendLine("Original response:");
        sb.AppendLine(originalResponse);
        sb.AppendLine();
        sb.AppendLine("Please output the revised version:");
        return sb.ToString();
    }

    private static List<SelfCritique> ParseCritique(string text)
    {
        try
        {
            var jsonStart = text.IndexOf('[');
            var jsonEnd = text.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = text[jsonStart..(jsonEnd + 1)];
                var items = JsonSerializer.Deserialize<List<JsonElement>>(json);
                if (items == null) return [];

                var critiques = new List<SelfCritique>();
                foreach (var item in items)
                {
                    critiques.Add(new SelfCritique
                    {
                        Category = item.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "",
                        Issue = item.TryGetProperty("issue", out var iss) ? iss.GetString() ?? "" : "",
                        Severity = item.TryGetProperty("severity", out var sev) ? sev.GetDouble() : 0,
                        FixSuggestion = item.TryGetProperty("fix_suggestion", out var fix) ? fix.GetString() : null,
                    });
                }
                return critiques;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SelfCritiqueGenerator: parse error: {ex.Message}");
        }
        return [];
    }
}
