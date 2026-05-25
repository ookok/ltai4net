using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LTAI.Core.Messaging;
using LTAI.Core.System;

namespace LTAI.Agent.Session;

public sealed record MultiToolDispatchResult
{
    public List<ToolCall> DispatchedTools { get; init; } = new();
    public List<ToolCallResult> Results { get; init; } = new();
    public double TotalLatencyMs { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public int RetryCount { get; init; }
}

public sealed record ToolCallResult
{
    public string ToolName { get; init; } = "";
    public Dictionary<string, object> Parameters { get; init; } = new();
    public string? Output { get; init; }
    public double LatencyMs { get; init; }
    public bool Success { get; init; }
    public int Attempts { get; init; } = 1;
}

public sealed class MultiToolDispatch
{
    private readonly AIToolRegistry _toolRegistry;
    private readonly int _maxParallelTools;
    private const int MaxRetries = 2;

    public MultiToolDispatch(AIToolRegistry toolRegistry, int maxParallelTools = 5)
    {
        _toolRegistry = toolRegistry;
        _maxParallelTools = maxParallelTools;
    }

    public static (string thought, List<ToolCall> actions) DecideParallelActions(
        string rawResponse, int stepIdx, int maxParallelTools = 5)
    {
        var lines = rawResponse.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var thought = lines.FirstOrDefault(l => l.StartsWith("THOUGHT:", StringComparison.OrdinalIgnoreCase))
                      ?? lines.FirstOrDefault(l => l.StartsWith("思考:", StringComparison.OrdinalIgnoreCase))
                      ?? (lines.Length > 0 ? lines[0] : "");

        thought = thought.Replace("THOUGHT:", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("思考:", "", StringComparison.OrdinalIgnoreCase)
                         .Trim();

        if (thought.Length > 500) thought = thought[..500];

        var actions = ParseMultiActions(lines, rawResponse, stepIdx);

        return (thought, actions.Take(maxParallelTools).ToList());
    }

    public async Task<MultiToolDispatchResult> ExecuteParallelAsync(List<ToolCall> actions)
    {
        var sw = Stopwatch.StartNew();
        var tasks = new List<Task<ToolCallResult>>();
        var totalRetries = 0;

        foreach (var action in actions)
        {
            tasks.Add(ExecuteWithRetryAsync(action));
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        totalRetries = results.Sum(r => r.Attempts - 1);

        return new MultiToolDispatchResult
        {
            DispatchedTools = actions,
            Results = results.ToList(),
            TotalLatencyMs = sw.ElapsedMilliseconds,
            SuccessCount = results.Count(r => r.Success),
            FailureCount = results.Count(r => !r.Success),
            RetryCount = totalRetries
        };
    }

    private async Task<ToolCallResult> ExecuteWithRetryAsync(ToolCall action)
    {
        for (var attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            var result = await ExecuteSingleToolAsync(action).ConfigureAwait(false);
            result = result with { Attempts = attempt };

            if (result.Success || attempt > MaxRetries)
                return result;
        }

        return new ToolCallResult
        {
            ToolName = action.ToolName,
            Parameters = action.Parameters,
            Output = "Max retries exceeded",
            Success = false,
            Attempts = MaxRetries + 1
        };
    }

    private async Task<ToolCallResult> ExecuteSingleToolAsync(ToolCall action)
    {
        var toolSw = Stopwatch.StartNew();
        try
        {
            var output = await _toolRegistry.InvokeAsync(action.ToolName,
                action.Parameters.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)).ConfigureAwait(false);
            toolSw.Stop();
            return new ToolCallResult
            {
                ToolName = action.ToolName,
                Parameters = action.Parameters,
                Output = output?.ToString() ?? "",
                LatencyMs = toolSw.ElapsedMilliseconds,
                Success = true
            };
        }
        catch (Exception ex)
        {
            toolSw.Stop();
            return new ToolCallResult
            {
                ToolName = action.ToolName,
                Parameters = action.Parameters,
                Output = $"Error: {ex.Message}",
                LatencyMs = toolSw.ElapsedMilliseconds,
                Success = false
            };
        }
    }

    private static List<ToolCall> ParseMultiActions(string[] lines, string rawResponse, int stepIdx)
    {
        var actions = new List<ToolCall>();

        // Try JSON tool calls first
        try
        {
            var jsonStart = rawResponse.IndexOf("{\"tool\"", StringComparison.OrdinalIgnoreCase);
            if (jsonStart < 0) jsonStart = rawResponse.IndexOf("{\"name\":", StringComparison.OrdinalIgnoreCase);
            if (jsonStart >= 0)
            {
                while (jsonStart >= 0 && jsonStart < rawResponse.Length)
                {
                    var braceCount = 0;
                    var inString = false;
                    var jsonEnd = -1;
                    for (var i = jsonStart; i < rawResponse.Length; i++)
                    {
                        var c = rawResponse[i];
                        if (c == '"' && (i == 0 || rawResponse[i - 1] != '\\')) inString = !inString;
                        if (inString) continue;
                        if (c == '{') braceCount++;
                        else if (c == '}') { braceCount--; if (braceCount == 0) { jsonEnd = i + 1; break; } }
                    }
                    if (jsonEnd < 0) break;

                    var jsonStr = rawResponse[jsonStart..jsonEnd];
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;

                    var toolName = root.TryGetProperty("tool", out var tn) ? tn.GetString() ?? ""
                        : root.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                    var args = root.TryGetProperty("args", out var a) ? a
                        : root.TryGetProperty("arguments", out var arg) ? arg
                        : root.TryGetProperty("parameters", out var p) ? p : default;

                    if (!string.IsNullOrEmpty(toolName))
                    {
                        var paramsDict = new Dictionary<string, object>();
                        if (args.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in args.EnumerateObject())
                                paramsDict[prop.Name] = prop.Value.ToString();
                        }
                        else if (args.ValueKind == JsonValueKind.String)
                        {
                            paramsDict["value"] = args.GetString() ?? "";
                        }
                        actions.Add(new ToolCall(toolName, paramsDict));
                    }

                    jsonStart = rawResponse.IndexOf('{', jsonEnd);
                }

                if (actions.Count > 0) return actions;
            }
        }
        catch { /* fall through to text parsing */ }

        // Fallback: text-based ACTION: parsing
        var actionLines = lines.Where(l =>
            l.StartsWith("ACTION:", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("行动:", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var line in actionLines)
        {
            var actionStr = line.Replace("ACTION:", "", StringComparison.OrdinalIgnoreCase)
                                .Replace("行动:", "", StringComparison.OrdinalIgnoreCase)
                                .Trim();

            var colonIdx = actionStr.IndexOf(':');
            if (colonIdx > 0)
            {
                var toolName = actionStr[..colonIdx].Trim();
                var param = actionStr[(colonIdx + 1)..].Trim();

                var paramDict = new Dictionary<string, object>();
                var semicolonIdx = param.IndexOf(';');
                if (semicolonIdx > 0)
                {
                    paramDict["value"] = param[..semicolonIdx].Trim();
                    paramDict["extra"] = param[(semicolonIdx + 1)..].Trim();
                }
                else
                {
                    paramDict["value"] = param;
                }

                actions.Add(new ToolCall(toolName, paramDict));
            }
            else
            {
                actions.Add(new ToolCall("unknown", new() { ["raw"] = actionStr }));
            }
        }

        if (actions.Count == 0)
        {
            if (rawResponse.Contains("search(", StringComparison.OrdinalIgnoreCase))
            {
                var searchMatches = System.Text.RegularExpressions.Regex.Matches(
                    rawResponse, @"search\(\s*""([^""]+)""\s*\)");
                foreach (System.Text.RegularExpressions.Match m in searchMatches)
                {
                    actions.Add(new ToolCall("search", new() { ["query"] = m.Groups[1].Value }));
                }
            }
        }

        return actions;
    }

    public static string BuildObservationText(MultiToolDispatchResult dispatchResult)
    {
        if (dispatchResult.Results.Count == 0) return "No actions taken.";

        var parts = new List<string>
        {
            $"Executed {dispatchResult.Results.Count} tools in {dispatchResult.TotalLatencyMs}ms (retries={dispatchResult.RetryCount}):"
        };

        for (int i = 0; i < dispatchResult.Results.Count; i++)
        {
            var r = dispatchResult.Results[i];
            var status = r.Success ? "OK" : "FAIL";
            var attemptStr = r.Attempts > 1 ? $" [{r.Attempts} attempts]" : "";
            var summary = r.Output?.Length > 200 ? r.Output[..200] + "..." : r.Output;
            parts.Add($"  [{status}]{attemptStr} {r.ToolName}: {summary}");
        }

        return string.Join("\n", parts);
    }
}
