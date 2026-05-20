using System.Collections.Concurrent;
using System.Diagnostics;
using LTAI.Core.System;

namespace LTAI.TreeLLM.Session;

public sealed record MultiToolDispatchResult
{
    public List<ToolCall> DispatchedTools { get; init; } = new();
    public List<ToolCallResult> Results { get; init; } = new();
    public double TotalLatencyMs { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
}

public sealed record ToolCallResult
{
    public string ToolName { get; init; } = "";
    public Dictionary<string, object> Parameters { get; init; } = new();
    public string? Output { get; init; }
    public double LatencyMs { get; init; }
    public bool Success { get; init; }
}

public sealed class MultiToolDispatch
{
    private readonly UnifiedAgentLoop _agentLoop;
    private readonly int _maxParallelTools;

    public MultiToolDispatch(UnifiedAgentLoop agentLoop, int maxParallelTools = 5)
    {
        _agentLoop = agentLoop;
        _maxParallelTools = maxParallelTools;
    }

    public async Task<(string thought, List<ToolCall> actions)> DecideParallelActionsAsync(
        string rawResponse, int stepIdx)
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

        return (thought, actions.Take(_maxParallelTools).ToList());
    }

    public async Task<MultiToolDispatchResult> ExecuteParallelAsync(List<ToolCall> actions)
    {
        var sw = Stopwatch.StartNew();
        var tasks = new List<Task<ToolCallResult>>();

        foreach (var action in actions)
        {
            tasks.Add(ExecuteSingleToolAsync(action));
        }

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        return new MultiToolDispatchResult
        {
            DispatchedTools = actions,
            Results = results.ToList(),
            TotalLatencyMs = results.Max(r => r.LatencyMs),
            SuccessCount = results.Count(r => r.Success),
            FailureCount = results.Count(r => !r.Success)
        };
    }

    private async Task<ToolCallResult> ExecuteSingleToolAsync(ToolCall action)
    {
        var toolSw = Stopwatch.StartNew();
        try
        {
            var output = await _agentLoop.ExecuteActionAsyncInternal(action);
            toolSw.Stop();
            return new ToolCallResult
            {
                ToolName = action.ToolName,
                Parameters = action.Parameters,
                Output = output,
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
                if (actions.Count == 0)
                    actions.Add(new ToolCall("search", new() { ["query"] = rawResponse[..Math.Min(200, rawResponse.Length)] }));
            }

            if (rawResponse.Contains("complete", StringComparison.OrdinalIgnoreCase))
                actions.Add(new ToolCall("complete", new() { ["step"] = stepIdx.ToString() }));
        }

        return actions;
    }

    public string BuildObservationText(MultiToolDispatchResult dispatchResult)
    {
        if (dispatchResult.Results.Count == 0) return "No actions taken.";

        var parts = new List<string>
        {
            $"Executed {dispatchResult.Results.Count} parallel tools in {dispatchResult.TotalLatencyMs}ms:"
        };

        for (int i = 0; i < dispatchResult.Results.Count; i++)
        {
            var r = dispatchResult.Results[i];
            var status = r.Success ? "OK" : "FAIL";
            var summary = r.Output?.Length > 200 ? r.Output[..200] + "..." : r.Output;
            parts.Add($"  [{status}] {r.ToolName}: {summary}");
        }

        return string.Join("\n", parts);
    }
}
