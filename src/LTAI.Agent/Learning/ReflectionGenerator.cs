using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Learning;

public sealed record ReflectionResult
{
    public string CausalReflection { get; init; } = "";
    public string CorrectiveStrategy { get; init; } = "";
    public string PreventiveGuideline { get; init; } = "";
    public bool HasContent => !string.IsNullOrWhiteSpace(CausalReflection);
}

public sealed class ReflectionGenerator
{
    private readonly IChatClient? _reflector;
    private readonly ILogger<ReflectionGenerator> _logger;

    public ReflectionGenerator(IChatClient? reflector = null, ILogger<ReflectionGenerator>? logger = null)
    {
        _reflector = reflector;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReflectionGenerator>.Instance;
    }

    public async Task<ReflectionResult> GenerateReflectionAsync(
        string query,
        string failedResponse,
        string failureReason,
        CancellationToken ct)
    {
        if (_reflector == null)
        {
            _logger.LogDebug("ReflectionGenerator: no reflector LLM configured");
            return new ReflectionResult();
        }

        try
        {
            var prompt = $@"You are a reflection assistant. Analyze the following agent interaction and produce a structured reflection.

Query: {query}

Agent Response (failed): {failedResponse}

Failure Reason: {failureReason}

Produce a structured analysis with three parts:
1. Root cause: What specifically went wrong in the agent's reasoning or tool usage?
2. Corrective strategy: What should the agent do differently next time?
3. Preventive guideline: What general rule would prevent this class of failure?

Output format:
## Causal Reflection
<root cause analysis>

## Corrective Strategy
<what to do differently>

## Preventive Guideline
<general rule>";

            var response = await _reflector.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], null, ct).ConfigureAwait(false);
            var text = response.Text ?? "";
            return ParseReflection(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReflectionGenerator failed");
            return new ReflectionResult();
        }
    }

    private static ReflectionResult ParseReflection(string text)
    {
        try
        {
            var causal = ExtractSection(text, "## Causal Reflection");
            var corrective = ExtractSection(text, "## Corrective Strategy");
            var preventive = ExtractSection(text, "## Preventive Guideline");
            return new ReflectionResult
            {
                CausalReflection = causal ?? "",
                CorrectiveStrategy = corrective ?? "",
                PreventiveGuideline = preventive ?? "",
            };
        }
        catch
        {
            return new ReflectionResult { CausalReflection = text.Length > 500 ? text[..500] : text };
        }
    }

    private static string? ExtractSection(string text, string sectionHeader)
    {
        var idx = text.IndexOf(sectionHeader, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = idx + sectionHeader.Length;
        var remaining = text[start..].TrimStart();

        var nextSection = remaining.IndexOf("## ", StringComparison.OrdinalIgnoreCase);
        return nextSection > 0 ? remaining[..nextSection].Trim() : remaining.Trim();
    }
}
