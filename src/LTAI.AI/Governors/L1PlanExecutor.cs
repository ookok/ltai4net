using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record PlanStep
{
    public string Tool { get; init; } = "";
    public Dictionary<string, object?> Args { get; init; } = new();
}

public sealed record PlanResult
{
    public bool Success { get; init; }
    public List<PlanStep> Steps { get; init; } = new();
    public string? Error { get; init; }
    public string? ContextMessage { get; init; }
    public int ToolsExecuted { get; init; }

    public static PlanResult NoMatch => new() { Success = false };
    public static PlanResult ParseFailure(string error) => new()
    {
        Success = false,
        Error = error
    };
}

public sealed class L1PlanExecutor
{
    private readonly ILogger<L1PlanExecutor>? _logger;
    private const int MaxPlanSteps = 5;

    public L1PlanExecutor(ILogger<L1PlanExecutor>? logger = null)
    {
        _logger = logger;
    }

    public async Task<PlanResult> PlanAndExecuteAsync(
        string query,
        IChatClient llm,
        AIToolRegistry toolRegistry,
        string flashModel,
        CancellationToken cancellationToken = default)
    {
        var toolNames = string.Join(", ", toolRegistry.ListTools().Take(20));
        var planningPrompt = BuildPlanningPrompt(query, toolNames);

        string? planJson;
        try
        {
            var planMessages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You are a task planner. Output ONLY a JSON plan. Do NOT answer the query, do NOT add explanations, do NOT use markdown code fences."),
                new(ChatRole.User, planningPrompt)
            };
            var planOptions = new ChatOptions
            {
                ModelId = flashModel,
                Temperature = 0.1f,
                MaxOutputTokens = 1024,
                Tools = new List<AITool>() // Disable tool calling — planner outputs JSON only
            };

            var planResponse = await llm.GetResponseAsync(planMessages, planOptions, cancellationToken);
            planJson = planResponse.Text?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "L1 planning call failed: {Error}", ex.Message);
            return PlanResult.ParseFailure($"L1 planning call failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(planJson))
            return PlanResult.ParseFailure("L1 returned empty plan");

        _logger?.LogDebug("L2 raw plan: {Plan}", planJson[..Math.Min(planJson.Length, 500)]);
        var steps = ParsePlan(planJson);
        _logger?.LogDebug("L2 parsed {Count} steps", steps.Count);
        if (steps.Count == 0)
            return PlanResult.ParseFailure($"Failed to parse plan from: {planJson[..Math.Min(planJson.Length, 100)]}");

        var contextSb = new StringBuilder();
        contextSb.AppendLine("【Layer2 自动规划执行】以下是按计划执行的工具结果：");

        var executedSteps = new List<PlanStep>();
        var toolsExecuted = 0;

        foreach (var step in steps)
        {
            if (!toolRegistry.HasTool(step.Tool))
            {
                _logger?.LogDebug("L2 plan: unknown tool {Tool}, skipping", step.Tool);
                continue;
            }

            FillDefaultArgs(step);

            try
            {
                var result = await toolRegistry.InvokeAsync(step.Tool, step.Args, cancellationToken);
                var resultText = result?.ToString() ?? "";
                var truncated = resultText.Length > 2000 ? resultText[..2000] + "..." : resultText;

                contextSb.AppendLine();
                contextSb.AppendLine($"### 工具: {step.Tool}");
                contextSb.AppendLine(truncated);

                executedSteps.Add(step);
                toolsExecuted++;
                _logger?.LogInformation("L2 plan executed: {Tool} (result {Len} chars)", step.Tool, resultText.Length);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "L2 plan tool {Tool} failed: {Error}", step.Tool, ex.Message);
                contextSb.AppendLine();
                contextSb.AppendLine($"### 工具: {step.Tool} (失败)");
                contextSb.AppendLine($"错误: {ex.Message}");
                executedSteps.Add(step);
            }
        }

        if (toolsExecuted == 0)
            return new PlanResult
            {
                Success = false,
                Error = "No tools were successfully executed from the plan",
                ContextMessage = contextSb.ToString()
            };

        contextSb.AppendLine();
        contextSb.AppendLine("---");
        contextSb.AppendLine("以上是自动规划执行的所有工具结果。请严格基于这些数据回答用户问题，不得编造任何不在工具结果中的信息。");

        return new PlanResult
        {
            Success = true,
            Steps = executedSteps,
            ContextMessage = contextSb.ToString(),
            ToolsExecuted = toolsExecuted
        };
    }

    private static void FillDefaultArgs(PlanStep step)
    {
        // Some tools have required params that the planner might omit.
        // Fill in sensible defaults.
        switch (step.Tool)
        {
            case "shell_exec":
                if (!step.Args.ContainsKey("workingDirectory"))
                    step.Args["workingDirectory"] = null!;
                break;
            case "filesystem_list":
                if (!step.Args.ContainsKey("path"))
                    step.Args["path"] = ".";
                if (!step.Args.ContainsKey("pattern"))
                    step.Args["pattern"] = null!;
                break;
            case "filesystem_read":
                if (!step.Args.ContainsKey("path"))
                    step.Args["path"] = "README.md";
                break;
            case "git_log":
                if (!step.Args.ContainsKey("repoPath"))
                    step.Args["repoPath"] = null!;
                if (!step.Args.ContainsKey("maxCount"))
                    step.Args["maxCount"] = 10;
                if (!step.Args.ContainsKey("format"))
                    step.Args["format"] = "oneline";
                break;
            case "git_diff":
                if (!step.Args.ContainsKey("repoPath"))
                    step.Args["repoPath"] = null!;
                if (!step.Args.ContainsKey("files"))
                    step.Args["files"] = null!;
                if (!step.Args.ContainsKey("staged"))
                    step.Args["staged"] = false;
                break;
            case "web_search":
                if (!step.Args.ContainsKey("query"))
                    step.Args["query"] = step.Tool;
                if (!step.Args.ContainsKey("maxResults"))
                    step.Args["maxResults"] = 5;
                break;
            case "env_processes":
                if (!step.Args.ContainsKey("filter"))
                    step.Args["filter"] = null!;
                if (!step.Args.ContainsKey("top"))
                    step.Args["top"] = 20;
                break;
            case "env_network":
                if (!step.Args.ContainsKey("pingHost"))
                    step.Args["pingHost"] = null!;
                break;
            case "datetime_now":
                if (!step.Args.ContainsKey("timezoneOffset"))
                    step.Args["timezoneOffset"] = null!;
                break;
        }
    }

    private static string BuildPlanningPrompt(string query, string toolNames)
    {
        return $"""
                Available tools: {toolNames}

                User query: "{query}"

                Output ONLY a JSON plan with tools needed. Format: {"{\"plan\":[{\"tool\":\"name\",\"args\":{\"p\":\"v\"}}]}"}

                Key tools and their required args:
                - web_search: args {"{\"query\":\"...\",\"maxResults\":5}"}
                - shell_exec: args {"{\"command\":\"...\",\"workingDirectory\":null}"}
                - filesystem_list: args {"{\"path\":\"dir\",\"pattern\":null}"}
                - filesystem_read: args {"{\"path\":\"file\"}"}
                - git_log: args {"{\"repoPath\":null,\"maxCount\":10,\"format\":\"oneline\"}"}
                - git_diff: args {"{\"repoPath\":null,\"files\":null,\"staged\":false}"}
                - env_sysinfo: args {"{}"}

                Output ONLY the JSON. No explanations.
                """;
    }

    private static List<PlanStep> ParsePlan(string text)
    {
        text = text.Trim();

        // Strip markdown code fences if present
        if (text.StartsWith("```"))
        {
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (end > 3)
                text = text[3..end].Trim();
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                text = text[4..].Trim();
        }

        // Try extracting JSON from the text if it's not pure JSON
        var jsonMatch = Regex.Match(text, @"\{[\s\S]*""plan""[\s\S]*\}", RegexOptions.IgnoreCase);
        if (!jsonMatch.Success)
        {
            jsonMatch = Regex.Match(text, @"\{[\s\S]*\}", RegexOptions.IgnoreCase);
        }

        var jsonText = jsonMatch.Success ? jsonMatch.Value : text;

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (!root.TryGetProperty("plan", out var planArr))
                return new List<PlanStep>();

            var steps = new List<PlanStep>();
            foreach (var item in planArr.EnumerateArray())
            {
                if (!item.TryGetProperty("tool", out var toolProp))
                    continue;

                var tool = toolProp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(tool))
                    continue;

                var args = new Dictionary<string, object?>();
                if (item.TryGetProperty("args", out var argsProp))
                {
                    foreach (var kv in argsProp.EnumerateObject())
                    {
                        args[kv.Name] = kv.Value.ValueKind switch
                        {
                            JsonValueKind.String => kv.Value.GetString(),
                            JsonValueKind.Number => kv.Value.TryGetInt64(out var l) ? l : kv.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            _ => kv.Value.ToString()
                        };
                    }
                }

                steps.Add(new PlanStep { Tool = tool, Args = args });
            }

            return steps;
        }
        catch (JsonException)
        {
            // Last resort: try to find individual tool calls in text format
            return ParseTextFormatPlan(text);
        }
    }

    private static List<PlanStep> ParseTextFormatPlan(string text)
    {
        var steps = new List<PlanStep>();
        var lines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Try "tool_name(arg1=val1, arg2=val2)" pattern
            var match = Regex.Match(line, @"(\w[\w_]+)\s*\((.+?)\)");
            if (match.Success)
            {
                var tool = match.Groups[1].Value;
                var argsStr = match.Groups[2].Value;
                var args = new Dictionary<string, object?>();

                var argPairs = argsStr.Split(',');
                foreach (var pair in argPairs)
                {
                    var eqIdx = pair.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var key = pair[..eqIdx].Trim();
                        var value = pair[(eqIdx + 1)..].Trim().Trim('"', '\'');
                        args[key] = value;
                    }
                }

                steps.Add(new PlanStep { Tool = tool, Args = args });
            }
        }

        return steps;
    }
}
