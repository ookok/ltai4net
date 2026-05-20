using System.Collections.Concurrent;
using System.Diagnostics;
using LTAI.Core.System;
using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Session;

public sealed class UnifiedAgentLoop : IInteractionLoop
{
    private readonly IChatClient _chatClient;
    private readonly AgenticRAG _agenticRAG;
    private readonly Prompting.PromptBuilder _promptBuilder;
    private readonly Prompting.ContinuousLearningLoop? _learningLoop;
    private readonly ILogger<UnifiedAgentLoop>? _logger;
    private readonly ConcurrentDictionary<string, InteractionTrajectory> _checkpoints = new();
    private readonly string _checkpointDir;

    public UnifiedAgentLoop(
        IChatClient chatClient,
        AgenticRAG agenticRAG,
        Prompting.PromptBuilder promptBuilder,
        Prompting.ContinuousLearningLoop? learningLoop = null,
        ILogger<UnifiedAgentLoop>? logger = null)
    {
        _chatClient = chatClient;
        _agenticRAG = agenticRAG;
        _promptBuilder = promptBuilder;
        _learningLoop = learningLoop;
        _logger = logger;
        _checkpointDir = global::System.IO.Path.Combine(".livingtree", "agent_trajectories");
        global::System.IO.Directory.CreateDirectory(_checkpointDir);
    }

    public async Task<InteractionTrajectory> RunAsync(
        string taskDescription,
        RolloutConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? new RolloutConfig();
        var sw = Stopwatch.StartNew();
        var trajectoryId = Guid.NewGuid().ToString("N")[..12];
        var steps = new List<AgentStep>();

        var accumulatedContext = taskDescription;
        double totalReward = 0;
        bool completed = false;

        for (int stepIdx = 0; stepIdx < cfg.MaxSteps && !completed; stepIdx++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stepSw = Stopwatch.StartNew();

            var (thought, action) = await DecideActionAsync(accumulatedContext, stepIdx, cfg);

            if (string.IsNullOrEmpty(thought))
            {
                completed = true;
                break;
            }

            var observation = await ExecuteActionAsync(action);

            var reward = ComputeStepReward(thought, observation, stepIdx);

            var step = new AgentStep(
                stepIdx,
                thought,
                action,
                observation,
                reward,
                stepSw.ElapsedMilliseconds);

            steps.Add(step);
            totalReward += reward;

            accumulatedContext = AppendToContext(accumulatedContext, thought, observation);

            if (IsTerminal(thought, observation) || stepIdx >= cfg.MaxSteps - 1)
                completed = true;

            if (cfg.EnablePartialRollout &&
                steps.Count % cfg.PartialRolloutSteps == 0 &&
                steps.Count < cfg.MaxSteps)
            {
                SaveCheckpointTemporary(trajectoryId, steps, accumulatedContext);
            }
        }

        sw.Stop();

        var trajectory = new InteractionTrajectory(
            trajectoryId,
            taskDescription,
            steps,
            totalReward / Math.Max(1, steps.Count),
            completed,
            sw.ElapsedMilliseconds);

        if (cfg.SaveCheckpoints)
            SaveCheckpoint(trajectory);

        if (_learningLoop != null && steps.Count > 0)
        {
            var answer = steps.Last().Observation ?? steps.Last().Thought;
            _learningLoop.Process(taskDescription, answer);
        }

        _logger?.LogInformation(
            "UnifiedAgentLoop: trajectory={Id} steps={Steps} reward={Reward:F2} completed={Done} {Ms}ms",
            trajectoryId, steps.Count, trajectory.TotalReward, completed, sw.ElapsedMilliseconds);

        return trajectory;
    }

    public async IAsyncEnumerable<InteractionTrajectory> RunBatchAsync(
        IReadOnlyList<string> tasks,
        RolloutConfig? config = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cfg = config ?? new RolloutConfig();
        var semaphore = new SemaphoreSlim(cfg.MaxConcurrent);

        var runningTasks = new List<Task<InteractionTrajectory>>();

        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(cancellationToken);

            var t = Task.Run(async () =>
            {
                try
                {
                    return await RunAsync(task, cfg, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken);

            runningTasks.Add(t);
        }

        while (runningTasks.Count > 0)
        {
            var completed = await Task.WhenAny(runningTasks);
            runningTasks.Remove(completed);
            yield return await completed;
        }
    }

    public InteractionTrajectory? RestoreFromCheckpoint(string trajectoryId)
    {
        if (_checkpoints.TryGetValue(trajectoryId, out var trajectory))
            return trajectory;

        var path = CheckpointPath(trajectoryId);
        if (!global::System.IO.File.Exists(path)) return null;

        try
        {
            var json = global::System.IO.File.ReadAllText(path);
            var trajectory_ = System.Text.Json.JsonSerializer.Deserialize<InteractionTrajectory>(json);
            if (trajectory_ != null)
                _checkpoints[trajectoryId] = trajectory_;
            return trajectory_;
        }
        catch
        {
            return null;
        }
    }

    public bool SaveCheckpoint(InteractionTrajectory trajectory)
    {
        _checkpoints[trajectory.TrajectoryId] = trajectory;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(trajectory);
            global::System.IO.File.WriteAllText(CheckpointPath(trajectory.TrajectoryId), json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(string thought, ToolCall? action)> DecideActionAsync(
        string context, int stepIdx, RolloutConfig cfg)
    {
        var docs = _agenticRAG.Search(context, RAGMode.Iterative, maxRounds: 2);

        var opts = new Prompting.PromptBuildOptions
        {
            Domain = "agent_action",
            MaxContextTokens = 4000,
            IncludeStrategyHint = false
        };

        var prompt = _promptBuilder.BuildSinglePrompt(
            $"Step {stepIdx + 1}: Based on the following context, decide the next action.\n\nContext:\n{context}",
            docs, opts);

        try
        {
            var response = await _chatClient.GetResponseAsync(prompt);
            var (thought, action) = ParseActionResponse(response.Text ?? "", stepIdx);
            return (thought, action);
        }
        catch
        {
            return ("Task completed.", null);
        }
    }

    private static (string thought, ToolCall? action) ParseActionResponse(string response, int stepIdx)
    {
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var thought = lines.FirstOrDefault(l => l.StartsWith("THOUGHT:", StringComparison.OrdinalIgnoreCase))
                      ?? lines.FirstOrDefault(l => l.StartsWith("思考:", StringComparison.OrdinalIgnoreCase))
                      ?? (lines.Length > 0 ? lines[0] : "");

        thought = thought.Replace("THOUGHT:", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("思考:", "", StringComparison.OrdinalIgnoreCase)
                         .Trim();

        if (thought.Length > 500)
            thought = thought[..500];

        var actionLine = lines.FirstOrDefault(l =>
            l.StartsWith("ACTION:", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("行动:", StringComparison.OrdinalIgnoreCase));

        if (actionLine == null)
        {
            if (response.Contains("search(", StringComparison.OrdinalIgnoreCase))
                return (thought, new ToolCall("search", new() { ["query"] = response[..Math.Min(200, response.Length)] }));
            if (response.Contains("complete", StringComparison.OrdinalIgnoreCase))
                return (thought, new ToolCall("complete", new() { ["step"] = stepIdx.ToString() }));
            return (thought, null);
        }

        actionLine = actionLine.Replace("ACTION:", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("行动:", "", StringComparison.OrdinalIgnoreCase)
                               .Trim();

        var colonIdx = actionLine.IndexOf(':');
        if (colonIdx > 0)
        {
            var toolName = actionLine[..colonIdx].Trim();
            var param = actionLine[(colonIdx + 1)..].Trim();
            return (thought, new ToolCall(toolName, new() { ["value"] = param }));
        }

        return (thought, new ToolCall("unknown", new() { ["raw"] = actionLine }));
    }

    public async Task<string?> ExecuteActionAsyncInternal(ToolCall action)
    {
        return await ExecuteActionCore(action);
    }

    private async Task<string?> ExecuteActionAsync(ToolCall? action)
    {
        if (action == null) return "No action taken.";
        return await ExecuteActionCore(action);
    }

    private async Task<string?> ExecuteActionCore(ToolCall action)
    {

        return action.ToolName.ToLowerInvariant() switch
        {
            "search" => await ExecuteSearchAction(action),
            "complete" => "Task marked as complete.",
            "wait" => "Waiting for next step.",
            _ => $"Executed unknown tool: {action.ToolName}"
        };
    }

    private async Task<string> ExecuteSearchAction(ToolCall action)
    {
        var query = action.Parameters.TryGetValue("value", out var v) ? v?.ToString() ?? "" : "";
        if (string.IsNullOrEmpty(query))
            query = action.Parameters.TryGetValue("query", out var q) ? q?.ToString() ?? "" : "";

        if (string.IsNullOrEmpty(query))
            return "No search query provided.";

        var results = _agenticRAG.Search(query, RAGMode.Iterative, maxRounds: 2);
        if (results.Count == 0)
            return $"No results found for: {query}";

        var topResults = results.Take(3).Select(r =>
            $"- [{r.Title ?? "source"}] {r.Content[..Math.Min(200, r.Content.Length)]}");

        return $"Found {results.Count} results for '{query}':\n" + string.Join("\n", topResults);
    }

    private static double ComputeStepReward(string thought, string? observation, int stepIdx)
    {
        double reward = 0.1;

        if (!string.IsNullOrEmpty(thought))
        {
            if (thought.Length > 100) reward += 0.1;
            if (thought.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                thought.Contains("搜索", StringComparison.OrdinalIgnoreCase)) reward += 0.05;
            if (thought.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
                thought.Contains("完成", StringComparison.OrdinalIgnoreCase)) reward += 0.15;
        }

        if (!string.IsNullOrEmpty(observation))
        {
            if (observation.Contains("No results", StringComparison.OrdinalIgnoreCase)) reward -= 0.05;
            if (observation.Contains("Found", StringComparison.OrdinalIgnoreCase) ||
                observation.Contains("找到", StringComparison.OrdinalIgnoreCase)) reward += 0.1;
            if (observation.Contains("Task marked", StringComparison.OrdinalIgnoreCase)) reward += 0.2;
        }

        var positionDecay = Math.Exp(-0.05 * stepIdx);
        reward *= positionDecay;

        return Math.Clamp(reward, 0.0, 1.0);
    }

    private static bool IsTerminal(string thought, string? observation)
    {
        if (thought.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            thought.Contains("完成", StringComparison.OrdinalIgnoreCase))
            return true;

        if (observation?.Contains("Task marked as complete", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return false;
    }

    private static string AppendToContext(string current, string thought, string? observation)
    {
        var parts = new List<string> { current };

        if (thought.Length > 0)
            parts.Add($"Thought: {thought}");

        if (!string.IsNullOrEmpty(observation))
            parts.Add($"Observation: {observation}");

        return string.Join("\n", parts);
    }

    private string CheckpointPath(string trajectoryId)
        => global::System.IO.Path.Combine(_checkpointDir, $"traj_{trajectoryId}.json");

    private void SaveCheckpointTemporary(string trajectoryId, List<AgentStep> steps, string context)
    {
        var partialTrajectory = new InteractionTrajectory(
            trajectoryId,
            "",
            new List<AgentStep>(steps),
            steps.Count > 0 ? steps.Average(s => s.Reward) : 0,
            false,
            0);

        SaveCheckpoint(partialTrajectory);
    }
}
