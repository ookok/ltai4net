using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Planning.Planning;

public sealed class DiffusionPlanner
{
    public static readonly Lazy<DiffusionPlanner> Instance = new(() => new DiffusionPlanner());

    public static readonly Dictionary<string, List<string>> DomainTools = new()
    {
        ["eia"] = new() { "gaussian_plume", "noise_attenuation", "tabular_reason", "dispersion_coeff" },
        ["emergency"] = new() { "risk_assessment", "evacuation_plan", "resource_allocation" },
        ["code"] = new() { "code_analyze", "code_review", "git_diff", "lint" },
        ["document"] = new() { "extract_text", "parse_table", "summarize", "translate" },
        ["research"] = new() { "web_search", "knowledge_query", "data_analysis", "report_generate" },
        ["general"] = new() { "web_search", "knowledge_query" },
    };

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, List<(string, double)>> _domainTools = new();
    private readonly List<DiffusionStep> _history = new();
    private readonly Lock _lock = new();

    public DiffusionPlanner(ILogger<DiffusionPlanner> logger)
    {
        _logger = logger;
    }

    internal DiffusionPlanner()
        : this((ILogger<DiffusionPlanner>)NullLogger<DiffusionPlanner>.Instance)
    {
    }

    public async Task<RefinedPlan> Refine(
        string intent,
        string domain,
        Func<string, string, CancellationToken, Task<string>>? llmCall = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedDomain = domain.ToLowerInvariant();
        var availableTools = GetAvailableTools(normalizedDomain);

        // Stage 1: Skeleton — determine WHAT to do
        var skeletonPrompt = BuildSkeletonPrompt(intent, normalizedDomain);
        string skeleton;
        if (llmCall is not null)
        {
            skeleton = await llmCall(skeletonPrompt, normalizedDomain, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            skeleton = GenerateTemplateSkeleton(intent, normalizedDomain, availableTools);
        }

        var step1 = new DiffusionStep
        {
            Stage = (int)DiffusionStage.Skeleton,
            PlanText = skeleton,
            ToolsUsed = new(),
            RefinementNotes = llmCall is not null ? "LLM-generated skeleton" : "Template-based skeleton"
        };

        // Stage 2: Tools — determine WHICH tools
        var toolsPrompt = BuildToolsPrompt(skeleton, normalizedDomain, availableTools);
        string toolsResponse;
        if (llmCall is not null)
        {
            toolsResponse = await llmCall(toolsPrompt, normalizedDomain, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            toolsResponse = string.Join("\n", availableTools.Select(t => $"tool({t})"));
        }

        var selectedTools = ParseToolsFromText(toolsResponse);
        if (selectedTools.Count == 0)
        {
            selectedTools = availableTools;
        }

        var step2 = new DiffusionStep
        {
            Stage = (int)DiffusionStage.Tools,
            PlanText = toolsResponse,
            ToolsUsed = selectedTools,
            Confidence = selectedTools.Count > 0 ? 0.7 : 0.2,
            RefinementNotes = $"Selected {selectedTools.Count} tools for {normalizedDomain}"
        };

        // Stage 3: Params — determine HOW, bind parameters
        var finalPlan = BuildFinalPlan(skeleton, selectedTools, normalizedDomain, intent);
        var step3 = new DiffusionStep
        {
            Stage = (int)DiffusionStage.Params,
            PlanText = finalPlan,
            ToolsUsed = selectedTools,
            Confidence = 0.85,
            RefinementNotes = "Parameter binding and final plan assembly"
        };

        var steps = new List<DiffusionStep> { step1, step2, step3 };

        var plan = new RefinedPlan
        {
            Intent = intent,
            Domain = normalizedDomain,
            Steps = steps,
            FinalPlan = finalPlan,
            ToolsSequence = selectedTools,
            EstimatedTokens = EstimateTokens(finalPlan),
        };

        plan.Confidence = ComputeConfidence(plan);

        lock (_lock)
        {
            _history.AddRange(steps);
        }

        _logger.LogInformation(
            "DiffusionPlanner refined plan | domain={Domain} tools={Count} confidence={Confidence:F2}",
            normalizedDomain, selectedTools.Count, plan.Confidence);

        return plan;
    }

    public string BuildSkeletonPrompt(string intent, string domain)
    {
        return $"""
            You are a planning skeleton builder. Given a user intent and domain, produce a high-level
            plan skeleton describing WHAT needs to be done — do not select specific tools yet.

            Intent: {intent}
            Domain: {domain}

            Output a structured skeleton plan with these sections:
            - Goal: What the user wants to achieve
            - Approach: High-level strategy for the {domain} domain
            - Key Considerations: Important domain-specific factors
            - Expected Outputs: What deliverables are expected
            - Constraints: Any limitations or requirements

            Keep it concise and actionable.
            """;
    }

    public string BuildToolsPrompt(string skeleton, string domain, List<string> availableTools)
    {
        var toolsList = string.Join("\n", availableTools.Select(t => $"  - {t}"));
        return $"""
            You are a tool selection planner. Given a plan skeleton and a list of available tools
            for the '{domain}' domain, select the most appropriate tools and specify their order.

            Skeleton Plan:
            {skeleton}

            Available Tools for {domain}:
            {toolsList}

            For each selected tool, reference it in one of these formats:
              [tool_name]
              tool_name(description of what it should do)

            Select only tools that are relevant to the plan. Order them logically.
            You may include brief explanations between tool references.
            """;
    }

    public List<string> ParseToolsFromText(string text)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bracketMatches = Regex.Matches(text, @"\[([^\]]+)\]");
        foreach (Match m in bracketMatches)
        {
            var name = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name) && !name.Contains(' ') && name.Length > 1)
            {
                tools.Add(name);
            }
        }

        var funcMatches = Regex.Matches(text, @"(\w+)\(");
        foreach (Match m in funcMatches)
        {
            var name = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name) && name.Length > 1)
            {
                tools.Add(name);
            }
        }

        return tools.ToList();
    }

    public double ComputeConfidence(RefinedPlan plan)
    {
        if (plan.Steps.Count == 0)
            return 0.0;

        var stepCompleteness = 0.0;
        foreach (var step in plan.Steps)
        {
            if (!string.IsNullOrWhiteSpace(step.PlanText))
                stepCompleteness += 0.25;
            if (step.ToolsUsed.Count > 0)
                stepCompleteness += 0.25;
            if (!string.IsNullOrWhiteSpace(step.RefinementNotes))
                stepCompleteness += 0.25;
            if (step.Confidence > 0)
                stepCompleteness += 0.25;
        }
        stepCompleteness /= plan.Steps.Count;

        var toolScore = plan.ToolsSequence.Count switch
        {
            0 => 0.0,
            1 => 0.3,
            2 => 0.6,
            3 => 0.8,
            >= 4 => 1.0,
            _ => 0.0
        };

        var hasFinalPlan = !string.IsNullOrWhiteSpace(plan.FinalPlan) ? 0.3 : 0.0;
        var estimatedTokensScore = plan.EstimatedTokens > 0 ? 0.2 : 0.0;

        var confidence = stepCompleteness * 0.3 + toolScore * 0.2 + hasFinalPlan + estimatedTokensScore;
        return Math.Clamp(confidence, 0.0, 1.0);
    }

    public Dictionary<string, object?> GetStats()
    {
        lock (_lock)
        {
            var byStage = new Dictionary<int, int>();
            var totalConfidence = 0.0;

            foreach (var step in _history)
            {
                byStage[step.Stage] = byStage.GetValueOrDefault(step.Stage) + 1;
                totalConfidence += step.Confidence;
            }

            return new()
            {
                ["total_steps"] = _history.Count,
                ["by_stage"] = byStage,
                ["avg_confidence"] = _history.Count > 0 ? totalConfidence / _history.Count : 0.0,
                ["domain_tools_count"] = _domainTools.Count,
                ["registered_domains"] = _domainTools.Keys.ToList(),
            };
        }
    }

    private List<string> GetAvailableTools(string domain)
    {
        if (_domainTools.TryGetValue(domain, out var scored))
        {
            return scored.Select(s => s.Item1).ToList();
        }

        if (DomainTools.TryGetValue(domain, out var tools))
        {
            _domainTools[domain] = tools.Select(t => (t, 0.5)).ToList();
            return tools;
        }

        var general = DomainTools["general"];
        _domainTools[domain] = general.Select(t => (t, 0.3)).ToList();
        return general;
    }

    private static string GenerateTemplateSkeleton(string intent, string domain, List<string> availableTools)
    {
        var toolList = string.Join(", ", availableTools);
        return $"""
            Goal: {intent}
            Approach: Execute structured {domain} workflow with domain-specific tools.
            Key Considerations: {domain}-typical constraints and quality requirements.
            Expected Outputs: Results from {toolList}.
            Constraints: Use only available domain tools, ensure logical tool ordering.
            """;
    }

    private static string BuildFinalPlan(string skeleton, List<string> tools, string domain, string intent)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Plan: {intent}");
        sb.AppendLine($"# Domain: {domain}");
        sb.AppendLine();
        sb.AppendLine("## Pipeline");
        for (var i = 0; i < tools.Count; i++)
        {
            sb.AppendLine($"[Step {i + 1}] {tools[i]}()  -- apply to {domain} task");
        }
        sb.AppendLine();
        sb.AppendLine("## Skeleton");
        sb.AppendLine(skeleton);
        return sb.ToString();
    }

    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var wordChars = text.Count(char.IsLetterOrDigit);
        var nonWordChars = text.Length - wordChars;
        return (int)(wordChars / 3.5 + nonWordChars / 2.0);
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public static readonly NullLogger<T> Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
