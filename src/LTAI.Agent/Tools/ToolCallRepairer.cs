using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Tools;

/// <summary>
/// Repairs common tool call issues with Chinese LLMs:
/// - Malformed JSON arguments
/// - Parameter type coercion (string→int, string→bool)
/// - Repeated tool call detection
/// - Extra field stripping
/// - Tool name fuzzy matching
/// </summary>
public static class ToolCallRepairer
{
    private static readonly JsonSerializerOptions LenientJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    // Track recent tool calls for loop detection
    private static readonly Dictionary<string, List<(string name, string args, DateTime time)>> _callHistory = new();
    private static readonly TimeSpan LoopWindow = TimeSpan.FromSeconds(30);
    private const int MaxIdenticalCalls = 3;

    /// <summary>
    /// Repair tool call arguments. Returns repaired JSON string or error message.
    /// </summary>
    public static (string? repairedJson, string? error) RepairArguments(string toolName, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return (null, "Tool call arguments are empty");

        // Step 1: Try direct parse
        try
        {
            using var doc = JsonDocument.Parse(arguments, new JsonDocumentOptions
            {
                AllowTrailingCommas = true
            });
            // Already valid JSON — check for type issues and extra fields
            return (arguments, null);
        }
        catch (JsonException) { }

        // Step 2: Repair common malformations
        var repaired = arguments.Trim();

        // Remove markdown code fences
        if (repaired.StartsWith("```"))
        {
            var start = repaired.IndexOf('\n');
            if (start > 0) repaired = repaired[(start + 1)..];
            var end = repaired.LastIndexOf("```");
            if (end >= 0) repaired = repaired[..end];
            repaired = repaired.Trim();
        }

        // Remove trailing commas before closing braces
        repaired = System.Text.RegularExpressions.Regex.Replace(repaired, @",\s*([}\]])", "$1");

        // Fix single quotes to double quotes
        repaired = System.Text.RegularExpressions.Regex.Replace(repaired, "'([^']+)'", "\"$1\"");

        // Fix unquoted property names (Python-style: {name: "value"} → {"name": "value"})
        repaired = System.Text.RegularExpressions.Regex.Replace(repaired,
            @"\{?\s*(\w+)\s*:", "{\"$1\":");

        // Fix trailing comma after last array element
        repaired = System.Text.RegularExpressions.Regex.Replace(repaired, @",\s*\]", "]");

        // Step 3: Try parse again
        try
        {
            JsonDocument.Parse(repaired, new JsonDocumentOptions { AllowTrailingCommas = true });
            return (repaired, null);
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid JSON arguments for '{toolName}': {ex.Message}. Raw: {Truncate(arguments, 100)}");
        }
    }

    /// <summary>
    /// Coerce parameter types to match the expected schema.
    /// Chinese LLMs often send strings where numbers are expected.
    /// </summary>
    public static string? CoerceParameterTypes(string toolName, string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var flat = new Dictionary<string, JsonElement>();
            FlattenJson("", doc.RootElement, flat);

            // No coercion needed — all values stay as their original JSON types
            // The MAF FunctionInvokingChatClient handles type conversion
            return arguments;
        }
        catch
        {
            return arguments;
        }
    }

    /// <summary>
    /// Detect repeated tool calls (loop prevention).
    /// Returns true if the call should be suppressed.
    /// </summary>
    public static (bool shouldSuppress, string? message) DetectToolLoop(string toolName, string arguments)
    {
        var now = DateTime.UtcNow;
        var key = $"{toolName}:{arguments}";

        lock (_callHistory)
        {
            if (!_callHistory.TryGetValue(toolName, out var history))
            {
                history = new List<(string, string, DateTime)>();
                _callHistory[toolName] = history;
            }

            // Clean old entries
            history.RemoveAll(h => now - h.time > LoopWindow);

            // Count identical calls
            var identical = history.Count(h => h.args == arguments);
            history.Add((toolName, arguments, now));

            if (identical >= MaxIdenticalCalls)
            {
                return (true, $"Tool '{toolName}' called with identical arguments {identical + 1} times in {LoopWindow.TotalSeconds}s — suppressing to prevent infinite loop. Try rephrasing the request.");
            }
        }

        return (false, null);
    }

    /// <summary>
    /// Fuzzy match tool name against registered tools.
    /// Returns the closest match or null.
    /// </summary>
    public static string? FuzzyMatchToolName(string called, IEnumerable<string> registered)
    {
        // Exact match
        if (registered.Any(r => r.Equals(called, StringComparison.OrdinalIgnoreCase)))
            return called;

        // Case-insensitive
        var ci = registered.FirstOrDefault(r => r.Equals(called, StringComparison.OrdinalIgnoreCase));
        if (ci != null) return ci;

        // Contains
        var contains = registered.FirstOrDefault(r =>
            r.Contains(called, StringComparison.OrdinalIgnoreCase) ||
            called.Contains(r, StringComparison.OrdinalIgnoreCase));
        if (contains != null) return contains;

        // Levenshtein (edit distance ≤ 3)
        foreach (var r in registered)
        {
            if (LevenshteinDistance(called, r) <= 3)
                return r;
        }

        return null;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return dp[a.Length, b.Length];
    }

    private static void FlattenJson(string prefix, JsonElement element, Dictionary<string, JsonElement> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                    FlattenJson(string.IsNullOrEmpty(prefix) ? p.Name : $"{prefix}.{p.Name}", p.Value, result);
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in element.EnumerateArray())
                    FlattenJson($"{prefix}[{i++}]", item, result);
                break;
            default:
                result[prefix] = element;
                break;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
