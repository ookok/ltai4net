using Microsoft.Extensions.Logging;

namespace LTAI.Capability.Reasoning;

public sealed class DialecticalReasoner
{
    private readonly ILogger<DialecticalReasoner> _logger;

    public DialecticalReasoner(ILogger<DialecticalReasoner> logger)
    {
        _logger = logger;
    }

    public async Task<DialecticalResult> AnalyzeAsync(
        string topic,
        string? thesis = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Dialectical analysis: {Topic}", topic[..Math.Min(topic.Length, 100)]);

        var thesisText = thesis ?? ExtractThesis(topic);
        var antithesis = GenerateAntithesis(thesisText);
        var synthesis = GenerateSynthesis(thesisText, antithesis);
        var critique = GenerateCritique(synthesis);

        return await Task.FromResult(new DialecticalResult
        {
            Topic = topic,
            Thesis = thesisText,
            Antithesis = antithesis,
            Synthesis = synthesis,
            Critique = critique
        });
    }

    private static string ExtractThesis(string topic)
    {
        if (topic.Contains("should") || topic.Contains("best") || topic.Contains("better"))
            return topic;

        var words = topic.Split(' ').Take(5);
        return $"{string.Join(" ", words)} is the optimal approach";
    }

    private static string GenerateAntithesis(string thesis)
    {
        var opposites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["optimal"] = "suboptimal",
            ["best"] = "problematic",
            ["efficient"] = "costly",
            ["simple"] = "oversimplified",
            ["fast"] = "risky",
            ["safe"] = "limiting",
            ["scalable"] = "complex",
            ["reliable"] = "brittle",
            ["flexible"] = "unpredictable",
            ["secure"] = "restrictive"
        };

        var antithesis = thesis;
        foreach (var (word, opposite) in opposites)
        {
            if (thesis.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                antithesis = thesis.Replace(word, opposite, StringComparison.OrdinalIgnoreCase);
                antithesis += $". However, this approach may have significant drawbacks.";
                break;
            }
        }

        if (antithesis == thesis)
            antithesis = $"Contrary to the thesis, there are important counterarguments: the approach may fail in edge cases, incur hidden costs, or create unintended consequences.";

        return antithesis;
    }

    private static string GenerateSynthesis(string thesis, string antithesis)
    {
        var topics = thesis.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Take(3)
            .ToList();

        var keywords = string.Join(", ", topics);

        return $"Synthesis: Integrating both perspectives, the optimal path balances the strengths of the thesis while mitigating the risks identified by the antithesis. " +
               $"Key areas for integration: {keywords}. A phased approach with continuous evaluation addresses both concerns.";
    }

    private static string GenerateCritique(string synthesis)
    {
        return $"Critique: While the synthesis offers a balanced view, it may be vulnerable to: " +
               $"(1) excessive compromise leading to mediocrity, " +
               $"(2) implementation complexity from trying to satisfy all constraints, " +
               $"(3) timing risks from phased execution. " +
               $"Recommendation: establish clear success metrics and fail-fast checkpoints.";
    }
}

public sealed class DialecticalResult
{
    public string Topic { get; init; } = "";
    public string Thesis { get; init; } = "";
    public string Antithesis { get; init; } = "";
    public string Synthesis { get; init; } = "";
    public string Critique { get; init; } = "";
}

public sealed class AttributionReasoner
{
    private readonly ILogger<AttributionReasoner> _logger;
    private readonly List<CausalLink> _causalChain = new();

    public AttributionReasoner(ILogger<AttributionReasoner> logger)
    {
        _logger = logger;
    }

    public async Task<AttributionResult> TraceAsync(
        string event_,
        List<string>? evidence = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Attribution tracing: {Event}", event_[..Math.Min(event_.Length, 100)]);

        var result = new AttributionResult { Event = event_ };

        if (evidence != null && evidence.Count > 0)
        {
            result.EvidenceChain = BuildEvidenceChain(event_, evidence);
            result.RootCause = IdentifyRootCause(result.EvidenceChain);
            result.Confidence = CalculateConfidence(result.EvidenceChain);
        }
        else
        {
            result.EvidenceChain.Add(new CausalLink
            {
                From = "Unknown cause",
                To = event_,
                Strength = 0.3,
                Description = "Insufficient evidence to trace causal chain"
            });
            result.RootCause = "Indeterminate - more evidence needed";
            result.Confidence = 0.1;
        }

        return await Task.FromResult(result);
    }

    private List<CausalLink> BuildEvidenceChain(string target, List<string> evidence)
    {
        var chain = new List<CausalLink>();
        var sorted = evidence
            .Select((e, i) => (Index: i, Text: e))
            .OrderByDescending(x => RelevanceScore(x.Text, target))
            .ToList();

        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var current = sorted[i];
            var next = sorted[i + 1];

            if (RelevanceScore(current.Text, next.Text) > 0.3)
            {
                chain.Add(new CausalLink
                {
                    From = current.Text[..Math.Min(current.Text.Length, 80)],
                    To = next.Text[..Math.Min(next.Text.Length, 80)],
                    Strength = RelevanceScore(current.Text, next.Text),
                    Description = $"Evidence piece {current.Index + 1} → {next.Index + 1}"
                });
            }
        }

        if (chain.Count == 0 && sorted.Count > 0)
        {
            chain.Add(new CausalLink
            {
                From = sorted[0].Text[..Math.Min(sorted[0].Text.Length, 80)],
                To = target,
                Strength = 0.5,
                Description = "Primary evidence → target event"
            });
        }

        return chain;
    }

    private string IdentifyRootCause(List<CausalLink> chain)
    {
        if (chain.Count == 0) return "Unknown";

        var sources = chain.Select(c => c.From).Distinct().ToList();
        var targets = chain.Select(c => c.To).ToHashSet();

        foreach (var source in sources)
        {
            if (!targets.Contains(source))
                return source;
        }

        return sources.First();
    }

    private static double CalculateConfidence(List<CausalLink> chain)
    {
        if (chain.Count == 0) return 0.1;
        return Math.Min(0.95, chain.Average(c => c.Strength) * Math.Min(1.0, chain.Count * 0.15));
    }

    private static double RelevanceScore(string a, string b)
    {
        var wordsA = a.Split(' ').Select(w => w.ToLowerInvariant()).ToHashSet();
        var wordsB = b.Split(' ').Select(w => w.ToLowerInvariant()).ToHashSet();

        if (wordsA.Count == 0 || wordsB.Count == 0) return 0;

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return (double)intersection / union;
    }
}

public sealed class AttributionResult
{
    public string Event { get; init; } = "";
    public List<CausalLink> EvidenceChain { get; set; } = new();
    public string RootCause { get; set; } = "";
    public double Confidence { get; set; }
}

public sealed class CausalLink
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public double Strength { get; init; }
    public string Description { get; init; } = "";
}
