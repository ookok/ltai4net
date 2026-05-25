using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public sealed record PlanStep
{
    public string Id { get; init; } = "";
    public string Tool { get; init; } = "";
    public Dictionary<string, object?> Args { get; init; } = new();
    public List<string> Deps { get; init; } = new();
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
    private readonly PromptTemplateStore? _prompts;
    private const int MaxPlanSteps = 5;

    public L1PlanExecutor(ILogger<L1PlanExecutor>? logger = null, PromptTemplateStore? prompts = null)
    {
        _logger = logger;
        _prompts = prompts;
    }

    public async Task<PlanResult> PlanAndExecuteAsync(
        string query,
        IChatClient llm,
        AIToolRegistry toolRegistry,
        string flashModel,
        CancellationToken cancellationToken = default)
    {
        var toolSignatures = BuildToolSignatures(toolRegistry);
        var planningPrompt = _prompts != null
            ? _prompts.Render("plan_user", new Dictionary<string, string>
            {
                ["tools"] = toolSignatures,
                ["query"] = query
            })
            : $"Available tools:\n{toolSignatures}\n\nUser query: \"{query}\"\n\nOutput ONLY a JSON plan.";
        var systemPrompt = _prompts?.Render("plan_system") ?? "You are a task planner. Output ONLY a JSON plan.";

        string? planJson;
        try
        {
            var planMessages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, planningPrompt)
            };
            var planOptions = new ChatOptions
            {
                ModelId = flashModel,
                Temperature = 0.1f,
                MaxOutputTokens = 1024,
                Tools = new List<AITool>() // Disable tool calling — planner outputs JSON only
            };

            var planResponse = await llm.GetResponseAsync(planMessages, planOptions, cancellationToken).ConfigureAwait(false);
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
        var results = new Dictionary<string, string>();
        var toolsExecuted = 0;

        // Assign IDs to steps without them
        for (int i = 0; i < steps.Count; i++)
            if (string.IsNullOrEmpty(steps[i].Id))
                steps[i] = steps[i] with { Id = $"s{i}" };

        var remaining = steps.ToHashSet();
        while (remaining.Count > 0)
        {
            // Find steps whose dependencies are all resolved
            var ready = remaining.Where(s => s.Deps.All(d => results.ContainsKey(d))).ToList();
            if (ready.Count == 0)
            {
                // Circular dependency or missing deps — execute remaining sequentially
                _logger?.LogWarning("L2 plan: possible circular dependency, executing remaining {Count} steps sequentially", remaining.Count);
                ready = remaining.ToList();
            }

            // Execute ready steps in parallel
            var tasks = ready.Select(async step =>
            {
                if (!toolRegistry.HasTool(step.Tool))
                {
                    _logger?.LogDebug("L2 plan: unknown tool {Tool}, skipping", step.Tool);
                    return (step, (string?)null, (string?)null);
                }

                FillDefaultArgsFromMetadata(step, toolRegistry);

                // Resolve dependencies: replace {sX} references in args with results
                foreach (var key in step.Args.Keys.ToList())
                {
                    var val = step.Args[key]?.ToString();
                    if (val != null)
                    {
                        foreach (var (depId, depResult) in results)
                        {
                            if (val.Contains($"{{{depId}}}"))
                                step.Args[key] = val.Replace($"{{{depId}}}", depResult);
                        }
                    }
                }

                try
                {
                    var result = await toolRegistry.InvokeAsync(step.Tool, step.Args, cancellationToken).ConfigureAwait(false);
                    var resultText = result?.ToString() ?? "";
                    _logger?.LogInformation("L2 plan executed: {Tool} (id={Id}, result {Len} chars)", step.Tool, step.Id, resultText.Length);
                    return (step, resultText, (string?)null);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "L2 plan tool {Tool} failed: {Error}", step.Tool, ex.Message);
                    return (step, (string?)null, ex.Message);
                }
            });

            var batchResults = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var (step, resultText, error) in batchResults)
            {
                remaining.Remove(step);
                executedSteps.Add(step);

                contextSb.AppendLine();
                if (error != null)
                {
                    contextSb.AppendLine($"### 工具: {step.Tool} (失败)");
                    contextSb.AppendLine($"错误: {error}");
                }
                else if (resultText != null)
                {
                    var truncated = resultText.Length > 2000 ? resultText[..2000] + "..." : resultText;
                    contextSb.AppendLine($"### 工具: {step.Tool}");
                    contextSb.AppendLine(truncated);
                    results[step.Id] = resultText;
                    toolsExecuted++;
                }
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
        // No longer hardcoded — FillDefaultArgsFromMetadata handles this
    }

    private string BuildToolSignatures(AIToolRegistry toolRegistry)
    {
        var sb = new StringBuilder();
        foreach (var tool in toolRegistry.GetTools().Take(25))
        {
            var name = tool.Name;
            var desc = tool.Description ?? "";
            if (desc.Length > 80) desc = desc[..80] + "...";

            sb.AppendLine($"- {name}: {desc}");

            var schema = (tool as AIFunction)?.JsonSchema;
            if (schema != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(schema.Value.ToString());
                    var root = doc.RootElement;
                    if (root.TryGetProperty("properties", out var props))
                    {
                        sb.Append("  args: {");
                        var paramParts = new List<string>();
                        foreach (var p in props.EnumerateObject())
                        {
                            var pType = "string";
                            if (p.Value.TryGetProperty("type", out var t))
                            {
                                if (t.ValueKind == JsonValueKind.String)
                                    pType = t.GetString() ?? "string";
                                else if (t.ValueKind == JsonValueKind.Array)
                                    pType = string.Join("|", t.EnumerateArray().Select(e => e.GetString() ?? ""));
                            }
                            var isRequired = root.TryGetProperty("required", out var req)
                                && req.ValueKind == JsonValueKind.Array
                                && req.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == p.Name);
                            var reqMark = isRequired ? "" : "?";
                            paramParts.Add($"\"{p.Name}\": {pType}{reqMark}");
                        }
                        sb.AppendLine(string.Join(", ", paramParts) + "}");
                    }
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "Tool schema parameter parsing failed"); }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private void FillDefaultArgsFromMetadata(PlanStep step, AIToolRegistry toolRegistry)
    {
        var tool = toolRegistry.GetTool(step.Tool) as AIFunction;
        var schema = tool?.JsonSchema;
        if (schema == null) return;

        try
        {
            using var doc = JsonDocument.Parse(schema.Value.ToString());
            var root = doc.RootElement;
            if (!root.TryGetProperty("properties", out var props)) return;

            bool isRequired(string name) =>
                root.TryGetProperty("required", out var req)
                && req.EnumerateArray().Any(r => r.GetString() == name);

            foreach (var p in props.EnumerateObject())
            {
                if (step.Args.ContainsKey(p.Name)) continue;

                var pType = p.Value.TryGetProperty("type", out var t) ? t.GetString() : "string";
                if (isRequired(p.Name))
                {
                    step.Args[p.Name] = pType switch
                    {
                        "string" => "",
                        "integer" or "number" => 0,
                        "boolean" => false,
                        "null" => null!,
                        _ => ""
                    };
                }
                else
                {
                    step.Args[p.Name] = null!;
                }
            }
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Default argument metadata population failed"); }
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

                var stepId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                var deps = new List<string>();
                if (item.TryGetProperty("deps", out var depsProp) && depsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in depsProp.EnumerateArray())
                        deps.Add(d.GetString() ?? "");
                }

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

                steps.Add(new PlanStep { Id = stepId, Tool = tool, Args = args, Deps = deps });
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
