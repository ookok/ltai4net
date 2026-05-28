using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.MAF;

// ============================================================================
// Tool-Call Repair Pipeline — adapted from DeepSeek-Reasonix Pillar 2.
// Repairs four classes of model output defects before dispatching tool calls.
// ============================================================================

/// <summary>
/// Result of running the tool-call repair pipeline on a model response.
/// </summary>
public sealed class RepairResult
{
    public string RepairedText { get; init; } = "";
    public string Action { get; init; } = "think";
    public string Detail { get; init; } = "";
    public List<string> AppliedFixes { get; init; } = new();
    public bool WasTruncated { get; init; }
    public bool WasScavenged { get; init; }
    public bool WasStormSuppressed { get; init; }
}

/// <summary>
/// Pipeline that runs model responses through four repair steps:
/// 1. SCAVENGE — extract ACTION/DETAIL from raw text if missing
/// 2. TRUNCATION — detect and repair truncated responses
/// 3. STORM — suppress repeated identical tool calls
/// 4. FLATTEN — (reserved for JSON tool-call mode, no-op in text mode)
/// </summary>
public sealed class ToolCallRepairPipeline
{
    private readonly ILogger<ToolCallRepairPipeline> _logger;
    private readonly int _stormWindowSize;
    private readonly Queue<(string Action, string Detail, DateTime Time)> _recentCalls = new();

    // Regex for extracting ACTION/DETAIL from raw thinking text
    private static readonly Regex ActionRegex = new(
        @"ACTION\s*[:：]\s*(read|edit|run|observe|done|think)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DetailRegex = new(
        @"DETAIL\s*[:：]\s*(.+?)(?=\n\s*(?:ACTION|OBSERVATION|$)|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Detect truncated JSON (for future JSON tool-call mode)
    private static readonly Regex JsonStartRegex = new(
        @"\{\s*""(?:name|function|tool)", RegexOptions.Compiled);

    // Detect think-block extractable actions
    private static readonly Regex ThinkBlockRegex = new(
        @"<think[^>]*>(.*?)</think>", RegexOptions.Singleline | RegexOptions.Compiled);

    public ToolCallRepairPipeline(
        ILogger<ToolCallRepairPipeline>? logger = null,
        int stormWindowSize = 5)
    {
        _logger = logger ?? NullLogger<ToolCallRepairPipeline>.Instance;
        _stormWindowSize = stormWindowSize;
    }

    /// <summary>
    /// Run the full repair pipeline on a model response.
    /// </summary>
    public RepairResult Repair(string response, string? previousAction = null)
    {
        var fixes = new List<string>();
        var text = response ?? "";
        var wasTruncated = false;
        var wasScavenged = false;
        var wasStormSuppressed = false;

        // Step 1: SCAVENGE — extract ACTION/DETAIL from raw text
        (text, wasScavenged) = Scavenge(text, fixes);

        // Step 2: TRUNCATION — detect incomplete responses
        (text, wasTruncated) = Truncation(text, fixes);

        // Step 3: STORM — suppress repeated calls
        var (action, detail) = ParseAction(text);
        (action, detail, wasStormSuppressed) = Storm(action, detail, previousAction, fixes);

        // Step 4: FLATTEN — no-op in text mode (reserved for JSON tool-calls)

        if (fixes.Count > 0)
        {
            _logger.LogInformation(
                "ToolCallRepair: Applied {Count} fixes: {Fixes}",
                fixes.Count, string.Join(", ", fixes));
        }

        return new RepairResult
        {
            RepairedText = text,
            Action = action,
            Detail = detail,
            AppliedFixes = fixes,
            WasTruncated = wasTruncated,
            WasScavenged = wasScavenged,
            WasStormSuppressed = wasStormSuppressed
        };
    }

    // ========================================================================
    // Step 1: SCAVENGE
    // DeepSeek models sometimes generate ACTION/DETAIL inside <think> blocks
    // but forget to output them in the final message. Extract from raw text.
    // ========================================================================
    private (string Text, bool Fixed) Scavenge(string text, List<string> fixes)
    {
        // If ACTION already present at top level, nothing to scavenge
        if (ActionRegex.IsMatch(text))
            return (text, false);

        // Try to extract ACTION/DETAIL from <think> blocks
        var thinkMatch = ThinkBlockRegex.Match(text);
        if (thinkMatch.Success)
        {
            var thinkContent = thinkMatch.Value;
            var actionMatch = ActionRegex.Match(thinkContent);
            if (actionMatch.Success)
            {
                var action = actionMatch.Groups[1].Value.ToLowerInvariant();
                var detailMatch = DetailRegex.Match(thinkContent);
                var detail = detailMatch.Success
                    ? detailMatch.Groups[1].Value.Trim()
                    : "extracted from think block";

                var extracted = $"\nACTION: {action}\nDETAIL: {detail}";
                fixes.Add($"scavenge: extracted ACTION={action} from think block");
                _logger.LogDebug("ToolCallRepair: Scavenged ACTION={Action} from think block", action);
                return (text + extracted, true);
            }
        }

        // Try to extract ACTION from anywhere in the text
        var globalAction = ActionRegex.Match(text);
        if (globalAction.Success)
        {
            var action = globalAction.Groups[1].Value.ToLowerInvariant();
            var detailMatch = DetailRegex.Match(text);
            var detail = detailMatch.Success
                ? detailMatch.Groups[1].Value.Trim()
                : "extracted from response";

            // Add explicit ACTION/DETAIL lines at the end
            var appended = $"\nACTION: {action}\nDETAIL: {detail}";
            fixes.Add($"scavenge: normalized scattered ACTION={action}");
            return (text + appended, true);
        }

        // Last resort: look for imperative patterns
        if (text.Contains("read", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("file", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("open", StringComparison.OrdinalIgnoreCase)))
        {
            fixes.Add("scavenge: inferred ACTION=read from context");
            return (text + "\nACTION: read\nDETAIL: inspect relevant files", true);
        }

        return (text, false);
    }

    // ========================================================================
    // Step 2: TRUNCATION
    // Detect when the model response was cut off (max_tokens exhausted).
    // Mark truncated and try to salvage a partial action.
    // ========================================================================
    private (string Text, bool Truncated) Truncation(string text, List<string> fixes)
    {
        text = text.TrimEnd();

        // Check if text ends mid-stream (no period, no newline, no closing marker)
        var lastChar = text.Length > 0 ? text[^1] : '.';
        var likelyTruncated = lastChar != '.' && lastChar != '\n' &&
                              lastChar != '>' && lastChar != '}' &&
                              !text.EndsWith("done", StringComparison.OrdinalIgnoreCase);

        // Check if JSON was being output and got cut off
        var jsonStarts = JsonStartRegex.Matches(text).Count;
        var jsonEnds = text.Count(c => c == '}');
        var jsonTruncated = jsonStarts > 0 && jsonStarts * 2 > jsonEnds; // rough estimate

        if (!likelyTruncated && !jsonTruncated)
            return (text, false);

        var truncationType = jsonTruncated ? "JSON" : "text";
        fixes.Add($"truncation: detected {truncationType} truncation");

        // Try to salvage: keep the last complete ACTION if any
        var actionMatches = ActionRegex.Matches(text);
        if (actionMatches.Count > 0)
        {
            var lastAction = actionMatches[^1];
            // Truncate text to just before the last (possibly incomplete) ACTION
            var truncatePos = lastAction.Index;
            var salvaged = text[..truncatePos].TrimEnd();
            fixes.Add("truncation: salvaged text before incomplete action");
            return (salvaged, true);
        }

        // If we have at least some meaningful content, return it as-is with a note
        if (text.Length > 50)
        {
            var note = "\n\n[Note: Response was truncated. Last action may be incomplete.]";
            return (text + note, true);
        }

        return (text, true);
    }

    // ========================================================================
    // Step 3: STORM
    // Detect when the model is calling the same tool with the same args
    // in a tight loop. Inject a reflection prompt to break the cycle.
    // ========================================================================
    private (string Action, string Detail, bool Suppressed) Storm(
        string action, string detail, string? previousAction, List<string> fixes)
    {
        var now = DateTime.UtcNow;
        var callKey = $"{action}|{detail}";

        // Check recent calls for duplicates
        var duplicateCount = _recentCalls.Count(c =>
            $"{c.Action}|{c.Detail}" == callKey &&
            (now - c.Time).TotalSeconds < 30);

        if (duplicateCount >= 2) // same call 3+ times in 30s window
        {
            _logger.LogWarning(
                "ToolCallRepair: Storm detected — {Action} called {Count}x in 30s. Suppressing.",
                action, duplicateCount + 1);

            // Replace with a reflection action
            _recentCalls.Clear(); // reset window
            fixes.Add($"storm: suppressed duplicate {action} call (called {duplicateCount + 1}x)");
            return ("think",
                $"Repeated '{action}' calls detected. Reflect on what's not working and try a different approach.",
                true);
        }

        // Record this call
        _recentCalls.Enqueue((action, detail, now));
        while (_recentCalls.Count > _stormWindowSize)
            _recentCalls.Dequeue();

        return (action, detail, false);
    }

    /// <summary>
    /// Parse ACTION/DETAIL from a response string.
    /// </summary>
    public static (string Action, string Detail) ParseAction(string response)
    {
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var action = "think";
        var detail = "";

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ACTION:", StringComparison.OrdinalIgnoreCase))
                action = trimmed["ACTION:".Length..].Trim().ToLowerInvariant();
            else if (trimmed.StartsWith("DETAIL:", StringComparison.OrdinalIgnoreCase))
                detail = trimmed["DETAIL:".Length..].Trim();
        }

        return (action, detail);
    }

    /// <summary>
    /// Reset the storm detection window (call at start of new task).
    /// </summary>
    public void ResetStormWindow()
    {
        _recentCalls.Clear();
    }
}
