using System.Diagnostics;
using System.Text.RegularExpressions;
using LTAI.Core.Interfaces;
using LTAI.Execution.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Execution.Models
{
    public enum ReactAction
    {
        Think,
        ToolCall,
        Observe,
        FinalAnswer,
        AskClarify
    }

    public enum ExecutionMode
    {
        DAG,
        React,
        Hybrid
    }

    public sealed class ReactStep
    {
        public int Iteration { get; set; }
        public string Thought { get; set; } = "";
        public string Action { get; set; } = "";
        public string ActionInput { get; set; } = "";
        public string Observation { get; set; } = "";
        public float Confidence { get; set; }
        public double LatencyMs { get; set; }
        public int TokensUsed { get; set; }
        public string Error { get; set; } = "";
    }

    public sealed class ReactTrajectory
    {
        public string Task { get; set; } = "";
        public List<ReactStep> Steps { get; set; } = new();
        public string FinalAnswer { get; set; } = "";
        public int TotalIterations { get; set; }
        public int TotalTokens { get; set; }
        public double TotalLatencyMs { get; set; }
        public bool Success { get; set; }
        public string StoppedReason { get; set; } = "";
    }

    public sealed class ReactConfig
    {
        public int MaxIterations { get; set; } = 10;
        public int MaxTokensPerIteration { get; set; } = 4096;
        public double TimeoutSeconds { get; set; } = 120.0;
        public float ConfidenceThreshold { get; set; } = 0.3f;
        public bool EnableReflexion { get; set; } = true;
        public float Temperature { get; set; } = 0.5f;
    }
}

namespace LTAI.Execution.Modes
{

public sealed class ReactExecutor
{
    private readonly ILogger<ReactExecutor> _logger;
    private readonly List<ReactTrajectory> _trajectories = new();
    private static ReactExecutor? _instance;

    public ReactConfig Config { get; } = new();
    public object? Consciousness { get; }

    public ReactExecutor(object? consciousness = null)
    {
        Consciousness = consciousness;
        _logger = NullLogger.Instance;
    }

    internal ReactExecutor(object? consciousness, ILogger<ReactExecutor> logger)
    {
        Consciousness = consciousness;
        _logger = logger;
    }

    public async Task<ReactTrajectory> Run(
        string task,
        Dictionary<string, Func<string, CancellationToken, Task<string>>>? tools = null,
        Dictionary<string, object?>? context = null,
        CancellationToken cancellationToken = default)
    {
        tools ??= new Dictionary<string, Func<string, CancellationToken, Task<string>>>();
        context ??= new Dictionary<string, object?>();

        var trajectory = new ReactTrajectory { Task = task };
        var history = new List<string>();
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Config.TimeoutSeconds);

        var knowledge = context.TryGetValue("knowledge", out var k) ? k?.ToString() : null;
        var kbBlock = !string.IsNullOrEmpty(knowledge)
            ? $"\nRelevant knowledge:\n{knowledge![..Math.Min(knowledge.Length, 2000)]}"
            : "";

        var toolNames = string.Join(", ", tools.Keys);
        var currentPrompt = BuildSystemPrompt(task, toolNames) + kbBlock;

        for (var iteration = 1; iteration <= Config.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sw.Elapsed > timeout)
            {
                trajectory.StoppedReason = "timeout";
                _logger.LogWarning("ReAct timeout after {Elapsed}ms for task: {Task}", sw.ElapsedMilliseconds, task);
                break;
            }

            var (thought, actionName, actionInput) = await ThinkActAsync(currentPrompt, history, iteration, cancellationToken);

            if (string.IsNullOrEmpty(thought))
            {
                trajectory.StoppedReason = "error";
                _logger.LogError("ReAct empty thought at iteration {Iteration} for task: {Task}", iteration, task);
                break;
            }

            if (string.Equals(actionName, "final_answer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(actionName, "finalanswer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(actionName, "answer", StringComparison.OrdinalIgnoreCase))
            {
                trajectory.FinalAnswer = actionInput;
                trajectory.Success = true;
                trajectory.StoppedReason = "final_answer";
                trajectory.Steps.Add(new ReactStep
                {
                    Iteration = iteration,
                    Thought = thought,
                    Action = "final_answer",
                    ActionInput = actionInput,
                    Observation = "Task complete.",
                    Confidence = 1.0f
                });
                _logger.LogInformation("ReAct final answer at iteration {Iteration} for task: {Task}", iteration, task);
                break;
            }

            if (string.Equals(actionName, "ask_clarify", StringComparison.OrdinalIgnoreCase)
                || string.Equals(actionName, "askclarify", StringComparison.OrdinalIgnoreCase)
                || string.Equals(actionName, "clarify", StringComparison.OrdinalIgnoreCase)
                || string.Equals(actionName, "question", StringComparison.OrdinalIgnoreCase))
            {
                trajectory.Steps.Add(new ReactStep
                {
                    Iteration = iteration,
                    Thought = thought,
                    Action = "ask_clarify",
                    ActionInput = actionInput,
                    Observation = $"Clarification needed: {actionInput}",
                    Confidence = 0.5f
                });
                trajectory.StoppedReason = "ask_clarify";
                _logger.LogInformation("ReAct asking clarification at iteration {Iteration}: {Question}", iteration, actionInput);
                break;
            }

            var stepStart = Stopwatch.GetTimestamp();
            var (observation, error) = await ExecuteActionAsync(actionName, actionInput, tools, cancellationToken);
            var stepLatency = (Stopwatch.GetTimestamp() - stepStart) / (double)Stopwatch.Frequency * 1000.0;

            var confidence = EstimateConfidence(observation, error);

            var step = new ReactStep
            {
                Iteration = iteration,
                Thought = thought,
                Action = $"tool_call({actionName})",
                ActionInput = actionInput,
                Observation = observation ?? error ?? "",
                Confidence = confidence,
                LatencyMs = stepLatency,
                Error = error ?? ""
            };
            trajectory.Steps.Add(step);

            _logger.LogDebug("ReAct step {Iteration}: {Action}({Input}) -> confidence={Confidence:F2}, latency={Latency:F0}ms",
                iteration, actionName, actionInput[..Math.Min(actionInput.Length, 50)], confidence, stepLatency);

            if (!string.IsNullOrEmpty(error) && iteration >= 3)
            {
                var recentErrors = trajectory.Steps.TakeLast(3).Count(s => !string.IsNullOrEmpty(s.Error));
                if (recentErrors >= 2)
                {
                    trajectory.StoppedReason = "error";
                    _logger.LogWarning("ReAct aborting after {Count} recent errors at iteration {Iteration}", recentErrors, iteration);
                    break;
                }
            }

            history.Add($"Step {iteration}: {thought}");
            history.Add($"Action: {actionName}({actionInput[..Math.Min(actionInput.Length, 100)]})");

            var truncatedObs = observation is { Length: > 500 }
                ? observation[..500] + "..."
                : observation;
            history.Add($"Observation: {truncatedObs}");

            currentPrompt = $"Observation: {truncatedObs}\n\nBased on this observation, what is your next thought and action?\nContinue with the exact format: Thought: ... Action: ...";
        }

        trajectory.TotalIterations = trajectory.Steps.Count;
        trajectory.TotalTokens = trajectory.Steps.Sum(s => s.TokensUsed);
        trajectory.TotalLatencyMs = trajectory.Steps.Sum(s => s.LatencyMs);

        if (string.IsNullOrEmpty(trajectory.StoppedReason))
            trajectory.StoppedReason = "max_iterations";

        _trajectories.Add(trajectory);

        if (Config.EnableReflexion)
            LogReflexion(trajectory);

        return trajectory;
    }

    public Dictionary<string, object?> GetStats()
    {
        if (_trajectories.Count == 0)
            return new Dictionary<string, object?> { ["trajectories"] = 0 };

        var trajs = _trajectories;
        return new Dictionary<string, object?>
        {
            ["trajectories"] = trajs.Count,
            ["success_rate"] = Math.Round((double)trajs.Count(t => t.Success) / trajs.Count, 3),
            ["avg_iterations"] = Math.Round(trajs.Average(t => t.TotalIterations), 1),
            ["avg_tokens"] = (int)Math.Round(trajs.Average(t => t.TotalTokens)),
            ["avg_latency_ms"] = (int)Math.Round(trajs.Average(t => t.TotalLatencyMs)),
            ["common_actions"] = GetCommonActions(trajs)
        };
    }

    public static (string thought, string actionName, string actionInput) ParseAction(string response)
    {
        var thoughtMatch = Regex.Match(
            response,
            @"Thought:\s*(.+?)(?=\n(?:Action|Observation):|\Z)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var thought = thoughtMatch.Success
            ? thoughtMatch.Groups[1].Value.Trim()
            : (response.Length > 200 ? response[..200] : response);

        var actionMatch = Regex.Match(
            response,
            @"Action:\s*(\w+)\((.+?)\)",
            RegexOptions.IgnoreCase);

        if (!actionMatch.Success)
        {
            var lower = response.ToLowerInvariant();
            if (lower.Contains("final answer") || lower.Contains("the answer is") || lower.Contains("in conclusion"))
                return (thought, "final_answer", response.Length > 1000 ? response[..1000] : response);

            return (thought, "think", "re-evaluating");
        }

        var actionName = actionMatch.Groups[1].Value.Trim();
        var actionInput = actionMatch.Groups[2].Value.Trim();

        return (thought, actionName, actionInput);
    }

    public static float EstimateConfidence(string observation, string error)
    {
        if (!string.IsNullOrEmpty(error))
            return 0.1f;

        var score = 0.5f;

        if (observation.Length > 100)
            score += 0.2f;

        if (observation.Contains("success", StringComparison.OrdinalIgnoreCase)
            || observation.Contains("completed", StringComparison.OrdinalIgnoreCase)
            || observation.Contains("found", StringComparison.OrdinalIgnoreCase)
            || observation.Contains("result", StringComparison.OrdinalIgnoreCase))
            score += 0.15f;

        if (observation.Contains("error", StringComparison.OrdinalIgnoreCase)
            || observation.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || observation.Contains("not found", StringComparison.OrdinalIgnoreCase))
            score -= 0.3f;

        if (observation.Length < 10)
            score -= 0.2f;

        return Math.Clamp(score, 0.0f, 1.0f);
    }

    public static string ParseParam(string input, string paramName, string defaultValue = "")
    {
        foreach (var part in input.Split(','))
        {
            var eqIndex = part.IndexOf('=');
            if (eqIndex < 0) continue;

            var key = part[..eqIndex].Trim();
            if (string.Equals(key, paramName, StringComparison.OrdinalIgnoreCase))
                return part[(eqIndex + 1)..].Trim();
        }

        return defaultValue;
    }

    public static ExecutionMode RouteExecution(
        string task,
        List<Dictionary<string, object?>> plan,
        object? consciousness = null,
        object? foresightGate = null)
    {
        if (foresightGate != null)
        {
            try
            {
                var gateType = foresightGate.GetType();
                var gateMethod = gateType.GetMethod("Gate")
                    ?? gateType.GetMethod("Assess");

                if (gateMethod != null)
                {
                    var parameters = gateMethod.GetParameters();
                    var args = new List<object?>();

                    foreach (var p in parameters)
                    {
                        if (string.Equals(p.Name, "task", StringComparison.OrdinalIgnoreCase))
                            args.Add(task);
                        else if (p.Name is "planLength" or "context")
                            args.Add(new Dictionary<string, object?> { ["plan_length"] = plan.Count, ["has_subtasks"] = plan.Count > 1 });
                        else if (string.Equals(p.Name, "history", StringComparison.OrdinalIgnoreCase))
                            args.Add(new List<string>());
                        else if (string.Equals(p.Name, "mode", StringComparison.OrdinalIgnoreCase))
                            args.Add("low");
                        else if (string.Equals(p.Name, "domain", StringComparison.OrdinalIgnoreCase))
                            args.Add("general");
                        else
                            args.Add(null);
                    }

                    var result = gateMethod.Invoke(foresightGate, args.ToArray());

                    if (result != null)
                    {
                        var resultType = result.GetType();
                        var confidenceProp = resultType.GetProperty("Confidence");
                        var stateProp = resultType.GetProperty("State");

                        var confidence = confidenceProp?.GetValue(result) is float f ? f : 0.5f;
                        var stateVal = stateProp?.GetValue(result)?.ToString()?.ToLowerInvariant() ?? "accept";

                        if (stateVal == "reject" || stateVal == "recalibrate")
                            return ExecutionMode.React;

                        if (confidence > 0.7f && plan.Count > 2)
                            return ExecutionMode.DAG;

                        if (confidence < 0.5f)
                            return ExecutionMode.React;

                        return ExecutionMode.Hybrid;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to heuristic
            }
        }

        if (plan.Count >= 5)
            return ExecutionMode.DAG;

        if (plan.Count <= 2)
            return ExecutionMode.React;

        return ExecutionMode.Hybrid;
    }

    public static ReactExecutor GetReactExecutor(object? consciousness = null)
    {
        if (_instance == null)
        {
            _instance = new ReactExecutor(consciousness);
        }
        else if (consciousness != null && _instance.Consciousness == null)
        {
            _instance = new ReactExecutor(consciousness, _instance._logger);
        }

        return _instance;
    }

    private static string BuildSystemPrompt(string task, string toolNames)
    {
        var toolsSection = string.IsNullOrEmpty(toolNames)
            ? "Available tools: none provided."
            : $"Available tools: {toolNames}";

        return $"""
You are a LivingTree ReAct agent. Solve tasks by interleaving thought, action, and observation.

{toolsSection}

Other actions:
- ask_clarify(question) — Ask the user for clarification before proceeding
- final_answer(response) — Task complete, return the answer

For each step, output exactly in this format:

Thought: <your reasoning about what to do next and why>
Action: <tool_name>(<tool_input>)  OR  Action: final_answer(<response>)  OR  Action: ask_clarify(<question>)

Rules:
1. Always think BEFORE acting — explain your reasoning in the "Thought" line
2. After each action, consider the observation and think again
3. If observation reveals new information, adjust your plan
4. If uncertain, say so — don't guess
5. Stop with final_answer when the task is complete

Task: {task}
""";
    }

    private async Task<(string thought, string actionName, string actionInput)> ThinkActAsync(
        string prompt,
        List<string> history,
        int iteration,
        CancellationToken cancellationToken)
    {
        if (Consciousness is not IProviderEngine llm)
        {
            _logger.LogError("No LLM consciousness available for ReAct");
            return ("", "final_answer", "No consciousness available");
        }

        string histBlock;
        if (history.Count > 0)
        {
            var recent = history.Count > 6 ? history.TakeLast(6) : history;
            histBlock = string.Join("\n", recent);
        }
        else
        {
            histBlock = "(start)";
        }

        var fullPrompt = $"{prompt}\n\nHistory:\n{histBlock}";

        try
        {
            var response = await llm.ChatAsync(
                fullPrompt,
                new LLMChatOptions
                {
                    Temperature = Config.Temperature,
                    MaxTokens = Config.MaxTokensPerIteration,
                    TimeoutMs = (int)(Config.TimeoutSeconds * 1000)
                },
                cancellationToken);

            return ParseAction(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ReAct think failed at iteration {Iteration}", iteration);
            return ("Error in reasoning", "final_answer", ex.Message);
        }
    }

    private static async Task<(string observation, string error)> ExecuteActionAsync(
        string actionName,
        string actionInput,
        Dictionary<string, Func<string, CancellationToken, Task<string>>> tools,
        CancellationToken cancellationToken)
    {
        if (!tools.TryGetValue(actionName, out var tool))
            return ("", $"Unknown tool: {actionName}");

        try
        {
            var result = await tool(actionInput, cancellationToken);
            var observation = result.Length > 2000 ? result[..2000] : result;
            return (observation, "");
        }
        catch (Exception ex)
        {
            var error = ex.Message;
            if (error.Length > 200) error = error[..200];
            return ("", $"Tool {actionName} failed: {error}");
        }
    }

    private void LogReflexion(ReactTrajectory trajectory)
    {
        var lessons = ExtractLessons(trajectory);
        if (lessons.Count == 0) return;

        _logger.LogInformation("ReAct Reflexion: {Count} lessons from '{Task}'", lessons.Count, trajectory.Task[..Math.Min(trajectory.Task.Length, 60)]);
        foreach (var lesson in lessons)
            _logger.LogDebug("  -> {Lesson}", lesson);
    }

    private static List<string> ExtractLessons(ReactTrajectory trajectory)
    {
        var lessons = new List<string>();

        foreach (var step in trajectory.Steps)
        {
            if (!string.IsNullOrEmpty(step.Error))
                lessons.Add($"Avoid {step.Action} on error: {step.Error[..Math.Min(step.Error.Length, 100)]}");

            if (step.Confidence < 0.3f && step.Iteration > 1)
                lessons.Add($"Low confidence at step {step.Iteration}: {step.Action}");
        }

        if (trajectory.Success && trajectory.Steps.Count == 1)
            lessons.Add($"Fast resolution: {trajectory.Steps[0].Action}");

        if (!trajectory.Success && trajectory.StoppedReason == "max_iterations")
            lessons.Add($"Task '{trajectory.Task[..Math.Min(trajectory.Task.Length, 60)]}' needs decomposition or human help");

        return lessons;
    }

    private static List<string> GetCommonActions(List<ReactTrajectory> trajectories)
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in trajectories)
        {
            foreach (var s in t.Steps)
            {
                var key = s.Action;
                counts.TryGetValue(key, out var current);
                counts[key] = current + 1;
            }
        }

        return counts
            .OrderByDescending(kvp => kvp.Value)
            .Take(5)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    private sealed class NullLogger : ILogger<ReactExecutor>
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
}
