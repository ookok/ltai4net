namespace LTAI.Core.Governors;

public sealed record DiffSafetyResult
{
    public bool Safe { get; init; }
    public string Reason { get; init; } = "";
    public double RiskScore { get; init; }
    public List<string> TriggeredPatterns { get; init; } = new();
}

public sealed class SemanticDiffAgent
{
    private static readonly HashSet<string> DestructiveVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete", "remove", "destroy", "erase", "wipe", "purge", "drop", "truncate",
        "unlink", "rmdir", "overwrite", "replace with null"
    };

    private static readonly HashSet<string> ProtectedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "rules", "skills", "memory", "prompts", "config", "audit",
        "LTAI.Core", "LTAI.Agent", "LTAI.Planning", "LTAI.Foundation"
    };

    private static readonly (string Pattern, double Risk)[] DangerPatterns =
    {
        ("rm -rf", 1.0),
        ("git clean", 0.95),
        ("DROP TABLE", 0.9),
        ("FORMAT C:", 1.0),
        ("shutdown", 0.85),
        ("/dev/null", 0.7),
        ("> /dev/null", 0.7),
        ("--force", 0.6),
        ("--hard reset", 0.8),
        ("curl.*|.*sh", 0.7),
        ("eval(", 0.8),
        ("os.system", 0.8),
        ("exec(", 0.8),
        ("Process.Start", 0.6),
        ("File.Delete", 0.66),
        ("Directory.Delete", 0.66),
        (".csproj", 0.4),
        ("secrets", 0.8),
        ("api_key", 0.75),
        ("connectionstring", 0.66)
    };

    private readonly double _riskTolerance;

    public SemanticDiffAgent(double riskTolerance = 0.5)
    {
        _riskTolerance = riskTolerance;
    }

    public DiffSafetyResult EvaluateGene(Gene gene)
    {
        var triggered = new List<string>();
        double maxRisk = 0;

        var combinedAction = $"{gene.Condition} {gene.Action} {gene.RouteLabel}".ToLowerInvariant();
        var target = gene.TargetModule?.ToLowerInvariant() ?? "";

        foreach (var (pattern, risk) in DangerPatterns)
        {
            if (combinedAction.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                triggered.Add($"danger_pattern:{pattern}");
                maxRisk = Math.Max(maxRisk, risk);
            }
        }

        if (gene.TargetModule != null && ProtectedPaths.Any(p =>
            target.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var verb in DestructiveVerbs)
            {
                if (gene.Action.Contains(verb, StringComparison.OrdinalIgnoreCase))
                {
                    triggered.Add($"protected_path_destruction:{gene.TargetModule}:{verb}");
                    maxRisk = Math.Max(maxRisk, 0.7);
                    break;
                }
            }
        }

        foreach (var (paramKey, paramValue) in gene.Parameters)
        {
            if (paramValue is string strVal)
            {
                var lower = strVal.ToLowerInvariant();
                if (lower.Contains("../") || lower.Contains(@"..\"))
                {
                    triggered.Add($"parameter_path_traversal:{paramKey}={strVal}");
                    maxRisk = Math.Max(maxRisk, 0.75);
                }

                foreach (var p in ProtectedPaths)
                {
                    if (strVal.StartsWith(p, StringComparison.OrdinalIgnoreCase) ||
                        lower.Contains(p.ToLowerInvariant()))
                    {
                        triggered.Add($"parameter_protected_path:{paramKey}={strVal}");
                        maxRisk = Math.Max(maxRisk, 0.65);
                    }
                }

                foreach (var (pattern, risk) in DangerPatterns)
                {
                    if (lower.Contains(pattern.ToLowerInvariant()))
                    {
                        triggered.Add($"parameter_danger_pattern:{paramKey}=[{pattern}]");
                        maxRisk = Math.Max(maxRisk, risk);
                    }
                }
            }
        }

        var safe = maxRisk <= _riskTolerance;

        return new DiffSafetyResult
        {
            Safe = safe,
            Reason = safe
                ? $"Safe: max risk {maxRisk:F2} <= {_riskTolerance:F2}"
                : $"BLOCKED: max risk {maxRisk:F2} > {_riskTolerance:F2}, patterns: [{string.Join(", ", triggered)}]",
            RiskScore = maxRisk,
            TriggeredPatterns = triggered
        };
    }

    public DiffSafetyResult EvaluateProposal(ArchitectureProposal proposal)
    {
        var triggered = new List<string>();
        double maxRisk = 0;

        var desc = (proposal.Description ?? "").ToLowerInvariant();
        var actionName = proposal.Action.ToString().ToLowerInvariant();

        if (actionName.Contains("remove") || actionName.Contains("delete") || actionName.Contains("drop"))
        {
            var pathRisk = proposal.Parameters.TryGetValue("path", out var path) ||
                           proposal.Parameters.TryGetValue("target", out path);

            if (pathRisk && path is string pathStr &&
                ProtectedPaths.Any(p => pathStr.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                var risk = 0.70 + (actionName.Contains("purge") ? 0.2 : 0);
                triggered.Add($"destructive_action_on_protected_path:{actionName}:{pathStr}");
                maxRisk = Math.Max(maxRisk, risk);
            }
        }

        foreach (var (paramKey, paramValue) in proposal.Parameters)
        {
            if (paramValue is string strVal)
            {
                var lower = strVal.ToLowerInvariant();
                if (lower.Contains("../") || lower.Contains(@"..\"))
                {
                    triggered.Add($"param_path_traversal:{paramKey}={strVal}");
                    maxRisk = Math.Max(maxRisk, 0.75);
                }
            }
        }

        foreach (var (pattern, risk) in DangerPatterns)
        {
            if (desc.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                triggered.Add($"danger_description:{pattern}");
                maxRisk = Math.Max(maxRisk, risk);
            }
        }

        var proposalSafe = maxRisk <= _riskTolerance;

        return new DiffSafetyResult
        {
            Safe = proposalSafe,
            Reason = proposalSafe
                ? $"Safe: max risk {maxRisk:F2} <= {_riskTolerance:F2}"
                : $"BLOCKED proposal: max risk {maxRisk:F2} > {_riskTolerance:F2}, patterns: [{string.Join(", ", triggered)}]",
            RiskScore = maxRisk,
            TriggeredPatterns = triggered
        };
    }
}
