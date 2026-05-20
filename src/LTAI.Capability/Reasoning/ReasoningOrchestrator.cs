using LTAI.Core.System;
using Microsoft.Extensions.Logging;

namespace LTAI.Capability.Reasoning;

public sealed class ReasoningOrchestrator
{
    private readonly ILogger<ReasoningOrchestrator> _logger;
    private readonly MathReasoner _math;
    private readonly FormalLogicEngine _logic;
    private readonly DialecticalReasoner _dialectical;
    private readonly AttributionReasoner _attribution;

    public ReasoningOrchestrator(
        ILogger<ReasoningOrchestrator> logger,
        MathReasoner math,
        FormalLogicEngine logic,
        DialecticalReasoner dialectical,
        AttributionReasoner attribution)
    {
        _logger = logger;
        _math = math;
        _logic = logic;
        _dialectical = dialectical;
        _attribution = attribution;
    }

    public async Task<ReasoningReport> ReasonAsync(
        string query,
        ReasoningType[]? types = null,
        CancellationToken cancellationToken = default)
    {
        types ??= new[] { ReasoningType.Auto };
        var report = new ReasoningReport { Query = query };

        var detectedType = DetectType(query);
        if (types.Contains(ReasoningType.Auto))
            types = new[] { detectedType };

        var resolvedTypes = types.ToList();
        for (var i = 0; i < resolvedTypes.Count; i++)
        {
            if (resolvedTypes[i] == ReasoningType.Auto)
                resolvedTypes[i] = detectedType;
        }

        foreach (var type in resolvedTypes)
        {
            switch (type)
            {
                case ReasoningType.Math:
                    var mathResult = await _math.SolveAsync(query, cancellationToken);
                    report.Math = new ReasoningStep
                    {
                        Type = "math",
                        Result = mathResult.Solution,
                        Method = mathResult.Method,
                        Confidence = mathResult.Method != "unknown" ? 0.9 : 0.2
                    };
                    break;

                case ReasoningType.Logic:
                    var logicResult = await _logic.ReasonAsync(query, ReasoningMode.Forward, cancellationToken);
                    report.Logic = new ReasoningStep
                    {
                        Type = "logic",
                        Result = logicResult.Conclusion,
                        Method = logicResult.Mode,
                        Confidence = logicResult.Confidence,
                        Details = logicResult.Steps
                    };
                    break;

                case ReasoningType.Dialectical:
                    var dialecticalResult = await _dialectical.AnalyzeAsync(query, cancellationToken: cancellationToken);
                    report.Dialectical = new ReasoningStep
                    {
                        Type = "dialectical",
                        Result = dialecticalResult.Synthesis,
                        Method = "thesis-antithesis-synthesis",
                        Confidence = 0.75,
                        Details = new List<string>
                        {
                            $"Thesis: {dialecticalResult.Thesis}",
                            $"Antithesis: {dialecticalResult.Antithesis}",
                            $"Synthesis: {dialecticalResult.Synthesis}",
                            $"Critique: {dialecticalResult.Critique}"
                        }
                    };
                    break;

                case ReasoningType.Attribution:
                    var attrResult = await _attribution.TraceAsync(query, cancellationToken: cancellationToken);
                    report.Attribution = new ReasoningStep
                    {
                        Type = "attribution",
                        Result = $"Root cause: {attrResult.RootCause}",
                        Method = "causal-chain",
                        Confidence = attrResult.Confidence,
                        Details = attrResult.EvidenceChain.Select(c => $"{c.From} → {c.To} ({c.Strength:F2})").ToList()
                    };
                    break;
            }
        }

        report.OverallConfidence = ComputeOverallConfidence(report);
        _logger.LogInformation("Reasoning complete: {Type}, confidence={Conf:F2}",
            types[0], report.OverallConfidence);

        return report;
    }

    public async Task<string> EnhanceResponse(string originalQuery, string llmResponse)
    {
        if (string.IsNullOrWhiteSpace(llmResponse)) return llmResponse;

        var detectedType = DetectType(originalQuery);

        if (detectedType == ReasoningType.Math)
        {
            var mathSolve = await _math.SolveAsync(originalQuery);
            if (mathSolve.Method != "unknown")
                return $"{llmResponse}\n\n---\n**Verified Computation:**\n{mathSolve.Solution}";
        }

        return llmResponse;
    }

    private static ReasoningType DetectType(string query)
    {
        var result = ClassificationRegistry.ReasoningType.Classify(query);
        return result switch
        {
            "Math" => ReasoningType.Math,
            "Dialectical" => ReasoningType.Dialectical,
            "Attribution" => ReasoningType.Attribution,
            _ => ReasoningType.Logic
        };
    }

    private static double ComputeOverallConfidence(ReasoningReport report)
    {
        var steps = new[] { report.Math, report.Logic, report.Dialectical, report.Attribution };
        var valid = steps.Where(s => s != null).ToList();
        return valid.Count > 0 ? valid.Average(s => s!.Confidence) : 0.5;
    }
}

public sealed class ReasoningReport
{
    public string Query { get; init; } = "";
    public ReasoningStep? Math { get; set; }
    public ReasoningStep? Logic { get; set; }
    public ReasoningStep? Dialectical { get; set; }
    public ReasoningStep? Attribution { get; set; }
    public double OverallConfidence { get; set; }
}

public sealed class ReasoningStep
{
    public string Type { get; init; } = "";
    public string Result { get; init; } = "";
    public string Method { get; init; } = "";
    public double Confidence { get; init; }
    public List<string> Details { get; init; } = new();
}

public enum ReasoningType { Auto, Math, Logic, Dialectical, Attribution }
