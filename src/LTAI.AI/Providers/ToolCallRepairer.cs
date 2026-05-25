using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.AI.Providers;

public static class ToolCallRepairer
{
    private static readonly HashSet<string> _recentToolCalls = new();
    private static readonly object _stormLock = new();
    private const int StormWindowMax = 20;

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
                    else if (prop.Value.ValueKind != JsonValueKind.Object)
                        rootDict[prop.Name] = prop.Value.ToString();
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
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var match = toolPattern.Match(text);
        if (match.Success)
            return RescueParser.TryParseToolCall(match.Groups[1].Value);

        var bracePattern = new Regex(
            @"""(?:name|function_name|tool_name)""\s*:\s*""(\w+)""[^}]*""(?:arguments|parameters|input)""\s*:\s*(\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\})",
            RegexOptions.Singleline);

        var braceMatch = bracePattern.Match(text);
        if (braceMatch.Success)
            return RescueParser.TryParseToolCall(braceMatch.Groups[2].Value);

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

        repaired = Regex.Replace(repaired, @",\s*(?=\})", "");

        return RescueParser.TryParseToolCall(repaired);
    }

    public static bool IsDuplicateToolCall(string toolName, string args)
    {
        var key = $"{toolName}|{args}";
        lock (_stormLock)
        {
            if (_recentToolCalls.Contains(key))
                return true;

            _recentToolCalls.Add(key);
            if (_recentToolCalls.Count > StormWindowMax)
            {
                var toRemove = _recentToolCalls.Take(_recentToolCalls.Count - StormWindowMax).ToList();
                foreach (var r in toRemove)
                    _recentToolCalls.Remove(r);
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

    public static void ClearStormHistory()
    {
        lock (_stormLock) _recentToolCalls.Clear();
    }

    private static void FlattenObject(JsonElement element, Dictionary<string, object?> result, string prefix)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenObject(prop.Value, result, key);
            }
            else if (prop.Value.ValueKind == JsonValueKind.String)
            {
                result[key] = prop.Value.GetString();
            }
            else if (prop.Value.ValueKind == JsonValueKind.Number)
            {
                result[key] = prop.Value.GetRawText();
            }
            else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
            {
                result[key] = prop.Value.GetBoolean();
            }
        }
    }
}
