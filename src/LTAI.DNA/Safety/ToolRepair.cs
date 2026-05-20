using System.Text.Json;
using System.Text.RegularExpressions;

namespace LTAI.DNA.Safety;

public sealed class ToolRepair
{
    private readonly int _maxDepth;
    private readonly int _maxParams;

    public ToolRepair(int maxDepth = 4, int maxParams = 50)
    {
        _maxDepth = maxDepth;
        _maxParams = maxParams;
    }

    public Dictionary<string, object>? Fix(string raw)
    {
        try
        {
            var parsed = ParseJson(raw);
            if (parsed == null) return null;

            var healed = HealMalformed(parsed);
            var flattened = FlattenNested(healed);
            var truncated = TruncateStorm(flattened);
            return Validate(truncated);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object>? ParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim();

        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            text = Regex.Replace(text, @"```json\s*", "", RegexOptions.IgnoreCase);
        if (text.StartsWith("```"))
            text = Regex.Replace(text, @"```\s*", "");
        if (text.EndsWith("```"))
            text = text[..^3].Trim();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(
                text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            var match = Regex.Match(text, @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}");
            if (match.Success)
            {
                try
                {
                    return JsonSerializer.Deserialize<Dictionary<string, object>>(
                        match.Value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch { /* non-fatal */ }
            }
        }

        return null;
    }

    private static Dictionary<string, object> HealMalformed(Dictionary<string, object> parsed)
    {
        var result = new Dictionary<string, object>(parsed);
        var keys = result.Keys.ToList();

        foreach (var key in keys)
        {
            var value = result[key];

            if (value is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.String)
                {
                    var str = je.GetString();
                    if (!string.IsNullOrEmpty(str))
                    {
                        if (str == "false" || str == "true")
                            result[key] = str == "true";
                        else if (int.TryParse(str, out var iv))
                            result[key] = iv;
                        else if (double.TryParse(str, out var dv))
                            result[key] = dv;
                    }
                }
                else if (je.ValueKind == JsonValueKind.True)
                    result[key] = true;
                else if (je.ValueKind == JsonValueKind.False)
                    result[key] = false;
                else if (je.ValueKind == JsonValueKind.Number)
                {
                    if (je.TryGetInt32(out var iv)) result[key] = iv;
                    else if (je.TryGetDouble(out var dv)) result[key] = dv;
                }
            }
        }

        return result;
    }

    private Dictionary<string, object> FlattenNested(Dictionary<string, object> data, string prefix = "")
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, value) in data)
        {
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";

            if (value is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    var nested = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        je.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (nested != null)
                    {
                        var depth = fullKey.Count(c => c == '.');
                        if (depth < _maxDepth)
                        {
                            foreach (var (nk, nv) in FlattenNested(nested, fullKey))
                                result[nk] = nv;
                            continue;
                        }
                    }
                }
                catch { /* non-fatal */ }
            }

            result[fullKey] = value switch
            {
                JsonElement e => e.ToString(),
                _ => value
            };
        }

        return result;
    }

    private Dictionary<string, object> TruncateStorm(Dictionary<string, object> data)
    {
        if (data.Count <= _maxParams) return data;

        var result = new Dictionary<string, object>();
        int count = 0;
        foreach (var (key, value) in data)
        {
            if (count >= _maxParams - 1) break;
            result[key] = value;
            count++;
        }

        result["_TRUNCATED"] = $"Truncated from {data.Count} to {_maxParams} params";
        return result;
    }

    private static Dictionary<string, object>? Validate(Dictionary<string, object> data)
    {
        return data.Count > 0 ? data : null;
    }

    public static string NormalizeCommand(string cmd)
    {
        cmd = Regex.Replace(cmd, @"<<\w+\s*\n.*?\n\w+", "<<REDACTED", RegexOptions.Singleline);
        return cmd.Trim();
    }
}

public static class ToolRepairExtensions
{
    public static Dictionary<string, object>? RepairToolCall(string raw)
    {
        var repairer = new ToolRepair();
        return repairer.Fix(raw);
    }

    public static string NormalizeShellCommand(string cmd)
    {
        return ToolRepair.NormalizeCommand(cmd);
    }
}
