using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.AI.Providers;

public static class ToolCallRepairer
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> _sessionToolCalls = new();
    private static readonly object _stormLock = new();
    private static readonly int StormWindowMax = int.TryParse(Knowledge.Core.OptionService.Get("repairer_storm_window"), out var sw) ? sw : 20;
    private static readonly int LoopWindowSize = int.TryParse(Knowledge.Core.OptionService.Get("repairer_loop_window"), out var lw) ? lw : 5;
    private static readonly int LoopThreshold = int.TryParse(Knowledge.Core.OptionService.Get("repairer_loop_threshold"), out var lt) ? lt : 3;

    private static readonly Regex TruncationTrailingCommaRegex = new(
        @",\s*(?=\})", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static JsonElement? RepairToolCall(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        var json = FlattenDeepSchema(rawJson);
        return RescueParser.TryParseToolCall(json)
            ?? ScavengeFromThinking(rawJson);
    }

    public static JsonElement? RepairAll(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        var json = FlattenDeepSchema(rawJson);
        var result = RescueParser.TryParseToolCall(json);
        if (result.HasValue) return result;
        result = ScavengeFromThinking(json);
        if (result.HasValue) return result;
        result = TruncationRepair(json);
        if (result.HasValue) return result;
        return null;
    }

    public static string FlattenDeepSchema(string json)
    {
        if (!json.Contains("parameters", StringComparison.OrdinalIgnoreCase))
            return json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("parameters", out var paramsElement) &&
                paramsElement.ValueKind == JsonValueKind.Object)
            {
                var paramCount = paramsElement.EnumerateObject().Count();
                if (paramCount <= 10) return json;

                var flattened = new Dictionary<string, object?>();
                FlattenObject(paramsElement, flattened, "");

                var rootDict = new Dictionary<string, object?>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "parameters")
                        rootDict["parameters"] = flattened;
                    else
                        rootDict[prop.Name] = ExtractJsonValue(prop.Value);
                }

                return JsonSerializer.Serialize(rootDict);
            }
        }
        catch { }

        return json;
    }

    public static JsonElement? ScavengeFromThinking(string text)
    {
        if (!text.Contains("<thinking", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("thinking_content", StringComparison.OrdinalIgnoreCase))
            return null;

        var toolPattern = new Regex(
            @"(?:function|tool)_call[:\s]*(\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(500));

        Match match;
        try { match = toolPattern.Match(text); }
        catch (RegexMatchTimeoutException) { return null; }

        if (match.Success)
            return RescueParser.TryParseToolCall(match.Groups[1].Value);

        var braceJson = ExtractBraceBalancedJson(text, "arguments")
            ?? ExtractBraceBalancedJson(text, "parameters")
            ?? ExtractBraceBalancedJson(text, "input");
        if (braceJson != null)
            return RescueParser.TryParseToolCall(braceJson);

        return null;
    }

    private static string? ExtractBraceBalancedJson(string text, string keyName)
    {
        var keyIdx = text.IndexOf($"\"{keyName}\"", StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0) return null;

        var colonIdx = text.IndexOf(':', keyIdx);
        if (colonIdx < 0) return null;

        var startIdx = colonIdx + 1;
        while (startIdx < text.Length && char.IsWhiteSpace(text[startIdx]))
            startIdx++;
        if (startIdx >= text.Length || text[startIdx] != '{') return null;

        var depth = 0;
        var inString = false;
        for (var i = startIdx; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"' && (i == 0 || text[i - 1] != '\\'))
                inString = !inString;
            if (inString) continue;
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return text[startIdx..(i + 1)];
            }
        }

        return null;
    }

    public static JsonElement? TruncationRepair(string json)
    {
        if (!json.EndsWith('{') && !json.EndsWith(',') && !json.Contains("..."))
            return null;

        var braceCount = 0;
        var inString = false;
        var sb = new System.Text.StringBuilder();

        foreach (var c in json)
        {
            if (c == '"' && (sb.Length == 0 || sb[^1] != '\\'))
                inString = !inString;

            if (!inString)
            {
                if (c == '{') braceCount++;
                else if (c == '}') braceCount--;
            }

            sb.Append(c);
        }

        while (braceCount > 0)
        {
            sb.Append('}');
            braceCount--;
        }

        var repaired = sb.ToString();
        if (repaired.EndsWith(", ..."))
            repaired = repaired[..^4] + "}";

        repaired = TruncationTrailingCommaRegex.Replace(repaired, "");

        return RescueParser.TryParseToolCall(repaired);
    }

    public static bool IsDuplicateToolCall(string sessionId, string toolName, string args)
    {
        var key = $"{toolName}|{args}";
        lock (_stormLock)
        {
            var recentCalls = _sessionToolCalls.GetOrAdd(sessionId, _ => new HashSet<string>());
            if (recentCalls.Contains(key)) return true;
            recentCalls.Add(key);
            if (recentCalls.Count > StormWindowMax)
            {
                var toRemove = recentCalls.Take(recentCalls.Count - StormWindowMax).ToList();
                foreach (var r in toRemove) recentCalls.Remove(r);
            }
            return false;
        }
    }

    public static string CapToolResult(string result, int maxTokens = 3000)
    {
        if (string.IsNullOrWhiteSpace(result)) return result;
        var approxTokens = result.Length / 4;
        if (approxTokens <= maxTokens) return result;
        var targetChars = maxTokens * 4;
        return result[..targetChars] + $"\n\n... [truncated: {approxTokens} tokens → {maxTokens} tokens cap]";
    }

    public static void ClearStormHistory(string sessionId)
    {
        lock (_stormLock) _sessionToolCalls.TryRemove(sessionId, out _);
        lock (_loopLock) _loopHistory.TryRemove(sessionId, out _);
    }

    private static readonly ConcurrentDictionary<string, Queue<(string Hash, DateTime Time)>> _loopHistory = new();
    private static readonly object _loopLock = new();

    /// <summary>
    /// SHA256-based tool call loop detection with circuit breaker.
    /// From OpenFang's Loop Guard pattern: if the same tool+args hash appears
    /// >= LoopThreshold times within the recent window, trigger a circuit break.
    /// Returns true if this call should be blocked as a detected loop.
    /// </summary>
    public static bool DetectLoop(string sessionId, string toolName, string args)
    {
        var hash = ComputeToolCallHash(toolName, args);
        var now = DateTime.UtcNow;

        lock (_loopLock)
        {
            var history = _loopHistory.GetOrAdd(sessionId,
                _ => new Queue<(string Hash, DateTime Time)>());

            while (history.Count > LoopWindowSize)
                history.Dequeue();

            var loopCount = history.Count(h => h.Hash == hash);
            history.Enqueue((hash, now));

            if (loopCount >= LoopThreshold)
            {
                ClearStormHistory(sessionId);
                return true;
            }

            return false;
        }
    }

    private static string ComputeToolCallHash(string toolName, string args)
    {
        var input = $"{toolName}|{args}";
        var hashBytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hashBytes)[..16];
    }

    private static object? ExtractJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ExtractJsonValue).ToList(),
            JsonValueKind.Object => FlattenObjectToDict(element),
            _ => element.GetRawText()
        };
    }

    private static Dictionary<string, object?> FlattenObjectToDict(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
            dict[prop.Name] = ExtractJsonValue(prop.Value);
        return dict;
    }

    private static void FlattenObject(JsonElement element, Dictionary<string, object?> result, string prefix)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            if (prop.Value.ValueKind == JsonValueKind.Object)
                FlattenObject(prop.Value, result, key);
            else if (prop.Value.ValueKind == JsonValueKind.String)
                result[key] = prop.Value.GetString();
            else if (prop.Value.ValueKind == JsonValueKind.Number)
                result[key] = prop.Value.GetRawText();
            else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                result[key] = prop.Value.GetBoolean();
        }
    }
}
