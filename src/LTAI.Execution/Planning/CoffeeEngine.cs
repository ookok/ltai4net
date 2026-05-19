using System.Text.RegularExpressions;
using LTAI.Execution.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Execution.Planning;

public sealed class CoFEECognitiveEngine
{
    private readonly ILogger<CoFEECognitiveEngine> _logger;
    private readonly List<BacktrackRecord> _backtrackHistory = new();
    private readonly Lock _backtrackLock = new();
    private readonly Random _rng = new();

    public static readonly HashSet<string> LeakageKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "therefore we should", "so obviously", "clearly", "as expected", "naturally we can",
        "据此可知", "显然", "自然而然", "因此应该"
    };

    private static readonly Lazy<CoFEECognitiveEngine> _instance = new(
        () => new CoFEECognitiveEngine(NullLogger<CoFEECognitiveEngine>.Instance));

    public static CoFEECognitiveEngine Instance => _instance.Value;

    private const string BackwardChainPrompt =
        "从目标逆向分析: {goal}\n当前步骤: {step}";

    private const string SubgoalPrompt =
        "将'{step}'分解为可验证的子目标";

    private const string VerificationPrompt =
        @"Verify the step against 4 cognitive constraints:
1. Backward Chain — does this step logically connect to the goal?
2. Subgoal Decomposition — can this step be broken into verifiable subgoals?
3. Output Observable — does this step produce a concrete observable output?
4. No Post-Hoc Leakage — does this step avoid reasoning that assumes the conclusion?";

    private static readonly HashSet<string> ActionableVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "compute", "find", "extract", "generate", "verify", "analyze", "search"
    };

    private static readonly HashSet<string> ObservableOutputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "result", "output", "file", "report", "data", "list", "table"
    };

    private CoFEECognitiveEngine(ILogger<CoFEECognitiveEngine> logger)
    {
        _logger = logger;
    }

    internal CoFEECognitiveEngine()
        : this((ILogger<CoFEECognitiveEngine>)NullLogger<CoFEECognitiveEngine>.Instance)
    {
    }

    public VerificationResult VerifyStep(string stepId, string stepDescription, string goal)
    {
        var bw = CheckBackwardChain(stepDescription, goal) ? 1.0 : 0.0;
        var sg = CheckSubgoalVerifiable(stepDescription) ? 1.0 : 0.0;
        var ob = CheckOutputObservable(stepDescription) ? 1.0 : 0.0;
        var nl = CheckNoPostHocLeakage(stepDescription) ? 1.0 : 0.0;

        var score = (bw + sg + ob + nl) / 4.0;
        var passed = score >= 0.75;

        var reasons = new List<string>();
        if (!CheckBackwardChain(stepDescription, goal))
            reasons.Add("Step does not sufficiently relate to the target goal.");
        if (!CheckSubgoalVerifiable(stepDescription))
            reasons.Add("Step lacks actionable verbs and cannot be independently verified.");
        if (!CheckOutputObservable(stepDescription))
            reasons.Add("Step does not produce a concrete observable output.");
        if (!CheckNoPostHocLeakage(stepDescription))
            reasons.Add("Step contains post-hoc reasoning language that presumes the conclusion.");

        var fixSuggestions = new List<string>();
        if (!CheckBackwardChain(stepDescription, goal))
            fixSuggestions.Add("Reframe the step to explicitly reference elements in the goal: " + goal);
        if (!CheckSubgoalVerifiable(stepDescription))
            fixSuggestions.Add("Include an actionable verb (compute, find, extract, generate, verify, analyze, search).");
        if (!CheckOutputObservable(stepDescription))
            fixSuggestions.Add("Specify a concrete deliverable (result, output, file, report, data, list, table).");
        if (!CheckNoPostHocLeakage(stepDescription))
            fixSuggestions.Add("Remove language that assumes the conclusion (e.g. 'therefore we should', 'clearly').");

        return new VerificationResult
        {
            StepId = stepId,
            BackwardChainOk = CheckBackwardChain(stepDescription, goal),
            SubgoalVerifiable = CheckSubgoalVerifiable(stepDescription),
            OutputObservable = CheckOutputObservable(stepDescription),
            NoPostHocLeakage = CheckNoPostHocLeakage(stepDescription),
            Reasons = reasons,
            CausalHypothesis = goal,
            Passed = passed,
            Score = score,
            FixSuggestions = fixSuggestions
        };
    }

    public CognitiveAudit AuditPlan(string planId, List<(string id, string desc)> steps, string goal)
    {
        var deduped = DeduplicateByFirstWords(steps, 5);
        var results = new List<VerificationResult>();

        foreach (var (id, desc) in deduped)
        {
            results.Add(VerifyStep(id, desc, goal));
        }

        var passCount = results.Count(r => r.Passed);
        var passRate = results.Count > 0 ? (double)passCount / results.Count : 0.0;
        var failCount = results.Count(r => !r.Passed);

        string recommendation = passRate switch
        {
            > 0.8 => "Plan passes cognitive audit",
            > 0.5 => $"Plan needs refinement — {failCount} steps flagged",
            _ => "Plan requires significant revision"
        };

        return new CognitiveAudit
        {
            PlanId = planId,
            StepsVerified = results.Count,
            BacktrackRecords = new List<BacktrackRecord>(),
            PassRate = passRate,
            CompressionRatio = steps.Count > 0 ? (double)deduped.Count / steps.Count : 0.0,
            Recommendation = recommendation
        };
    }

    public void RecordBacktrack(string alternative, string whyRejected, CognitiveBehavior behavior, string betterAlternative)
    {
        var record = new BacktrackRecord
        {
            Alternative = alternative,
            WhyRejected = whyRejected,
            Behavior = behavior,
            BetterAlternative = betterAlternative
        };

        lock (_backtrackLock)
        {
            _backtrackHistory.Add(record);
            while (_backtrackHistory.Count > 200)
            {
                _backtrackHistory.RemoveAt(0);
            }
        }
    }

    public string BuildConstrainedPrompt(string task, string goal, List<CognitiveBehavior> behaviors)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(task);

        foreach (var behavior in behaviors)
        {
            switch (behavior)
            {
                case CognitiveBehavior.BackwardChain:
                    sb.AppendLine(BackwardChainPrompt
                        .Replace("{goal}", goal)
                        .Replace("{step}", task));
                    break;
                case CognitiveBehavior.SubgoalDecompose:
                    sb.AppendLine(SubgoalPrompt
                        .Replace("{step}", task));
                    break;
                case CognitiveBehavior.Verify:
                    sb.AppendLine(VerificationPrompt);
                    break;
                case CognitiveBehavior.Backtrack:
                    sb.AppendLine("If this approach fails, backtrack and explore an alternative path.");
                    break;
            }
        }

        return sb.ToString();
    }

    public Dictionary<string, object?> GetStats()
    {
        lock (_backtrackLock)
        {
            return new Dictionary<string, object?>
            {
                ["backtrackCount"] = _backtrackHistory.Count,
                ["cognitiveBehaviors"] = Enum.GetNames<CognitiveBehavior>()
            };
        }
    }

    private bool CheckBackwardChain(string step, string goal)
    {
        var stepWords = Tokenize(step);
        var goalWords = Tokenize(goal);

        if (stepWords.Count == 0 || goalWords.Count == 0)
            return false;

        var intersection = stepWords.Intersect(goalWords, StringComparer.OrdinalIgnoreCase).Count();
        var union = stepWords.Union(goalWords, StringComparer.OrdinalIgnoreCase).Count();

        var jaccard = union > 0 ? (double)intersection / union : 0.0;
        return jaccard > 0.1;
    }

    private bool CheckSubgoalVerifiable(string step)
    {
        var words = Tokenize(step);
        return words.Any(w => ActionableVerbs.Contains(w.ToLowerInvariant()));
    }

    private bool CheckOutputObservable(string step)
    {
        var words = Tokenize(step);
        return words.Any(w => ObservableOutputs.Contains(w.ToLowerInvariant()));
    }

    private bool CheckNoPostHocLeakage(string step)
    {
        return !LeakageKeywords.Any(kw =>
            step.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        var matches = Regex.Matches(text, @"[\p{L}\p{N}]+");
        foreach (Match m in matches)
        {
            tokens.Add(m.Value);
        }

        return tokens;
    }

    private static List<(string id, string desc)> DeduplicateByFirstWords(
        List<(string id, string desc)> steps,
        int wordCount)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string id, string desc)>();

        foreach (var (id, desc) in steps)
        {
            var words = Tokenize(desc);
            var prefix = string.Join(" ", words.Take(wordCount));

            if (seen.Add(prefix))
            {
                result.Add((id, desc));
            }
        }

        return result;
    }
}
