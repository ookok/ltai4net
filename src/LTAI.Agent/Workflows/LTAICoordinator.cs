using System.Collections.Concurrent;
using System.Diagnostics;
using LTAI.AI.Interfaces;
using LTAI.Agent.MAF;
using LTAI.Agent.Skills.Runtime;
using LTAI.Knowledge.Core;
using LTAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed record SubSession
{
    public string SessionId { get; init; } = $"sub_{Guid.NewGuid():N}"[..20];
    public string AgentName { get; init; } = "";
    public string Role { get; init; } = "";
    public string Goal { get; init; } = "";
    public List<string> AllowedTools { get; init; } = new();
    public string? Result { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed class LTAICoordinator : IAsyncDisposable
{
    private readonly ILivingTreeSystem _lts;
    private readonly SkillAwareDecomposer _decomposer;
    private readonly PlannerIntegration _planner;
    private readonly UnifiedPlanningPipeline? _pipeline;
    private readonly SystemPromptAssembler? _promptAssembler;
    private readonly PromptService? _promptService;
    private readonly AgentProfile? _profile;
    private readonly string _workspaceRoot;
    private readonly ILogger<LTAICoordinator> _logger;
    private AbTestResult? _lastAbResult;

    public ConcurrentDictionary<string, SubSession> ActiveSessions { get; } = new();

    public LTAICoordinator(
        ILivingTreeSystem lts,
        SkillAwareDecomposer decomposer,
        PlannerIntegration planner,
        UnifiedPlanningPipeline? pipeline = null,
        SystemPromptAssembler? promptAssembler = null,
        PromptService? promptService = null,
        AgentProfile? profile = null,
        ILogger<LTAICoordinator>? logger = null)
    {
        _lts = lts;
        _decomposer = decomposer;
        _planner = planner;
        _pipeline = pipeline;
        _promptAssembler = promptAssembler;
        _promptService = promptService;
        _profile = profile;
        _workspaceRoot = OptionService.Get("LTAI_WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        _logger = logger ?? global::Microsoft.Extensions.Logging.Abstractions.NullLogger<LTAICoordinator>.Instance;
    }

    public async Task<TeamResult> RunAgentAsync(
        string agentName,
        string role,
        string prompt,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var events = new List<CoordinatorEvent>();

        var session = new SubSession
        {
            AgentName = agentName,
            Role = role,
            Goal = prompt
        };
        ActiveSessions[session.SessionId] = session;

        try
        {
            var systemPrompt = BuildSystemPrompt(agentName, role);

            var member = new TeamMember
            {
                Name = agentName,
                Role = role,
                SystemPrompt = systemPrompt
            };

            var pool = new AgentPool(_lts, _promptService);
            pool.Register(member);

            events.Add(new(CoordinatorEventType.TaskStarted, agentName, agentName));
            var output = await pool.RunAgentAsync(agentName, prompt, ct).ConfigureAwait(false);
            events.Add(new(CoordinatorEventType.Completed, agentName, agentName, output));

            session.Result = output;
            session.CompletedAt = DateTime.UtcNow;

            sw.Stop();
            RecordAbFeedback(output != null);
            return new TeamResult
            {
                Success = true,
                FinalOutput = output,
                Events = events,
                TaskGraph = Array.Empty<CoordinatorTask>(),
                CompletedTasks = 1,
                TotalTasks = 1,
                TotalMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            session.Result = $"Error: {ex.Message}";
            session.CompletedAt = DateTime.UtcNow;
            throw;
        }
    }

    public async Task<TeamResult> RunTeamAsync(
        AgentTeam team,
        string goal,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var events = new List<CoordinatorEvent>();
        var pool = new AgentPool(_lts, _promptService);

        pool.Register(team.Members);

        events.Add(new(CoordinatorEventType.Decomposing));

        var rounds = await DecomposeGoalAsync(goal, ct).ConfigureAwait(false);
        var tasks = BuildTaskGraph(team, goal, rounds);

        _logger.LogInformation(
            "LTAICoordinator: Decomposed goal into {Count} tasks for team '{Team}'",
            tasks.Count, team.Name);

        var results = await ExecuteTaskGraphAsync(pool, tasks, events, team.MaxConcurrency, ct)
            .ConfigureAwait(false);

        events.Add(new(CoordinatorEventType.Synthesizing));
        var finalOutput = await pool.SynthesizeResultsAsync(goal, results, ct).ConfigureAwait(false);
        events.Add(new(CoordinatorEventType.Completed, Data: finalOutput));

        sw.Stop();

        var completed = tasks.Count(t => t.Status == CoordinatorTaskStatus.Completed);
        var failed = tasks.Count(t => t.Status == CoordinatorTaskStatus.Failed);

        RecordAbFeedback(failed == 0);

        return new TeamResult
        {
            Success = failed == 0,
            FinalOutput = finalOutput,
            Error = failed > 0 ? $"{failed} task(s) failed" : null,
            Events = events,
            TaskGraph = tasks,
            CompletedTasks = completed,
            FailedTasks = failed,
            TotalTasks = tasks.Count,
            TotalMs = sw.ElapsedMilliseconds
        };
    }

    public async Task<TeamResult> RunTasksAsync(
        AgentTeam team,
        IReadOnlyList<CoordinatorTask> taskGraph,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var events = new List<CoordinatorEvent>();
        var pool = new AgentPool(_lts, _promptService);

        pool.Register(team.Members);

        _logger.LogInformation(
            "LTAICoordinator: Executing explicit DAG with {Count} tasks for team '{Team}'",
            taskGraph.Count, team.Name);

        var results = await ExecuteTaskGraphAsync(pool, taskGraph, events, team.MaxConcurrency, ct)
            .ConfigureAwait(false);

        events.Add(new(CoordinatorEventType.Synthesizing));
        var finalOutput = await pool.SynthesizeResultsAsync(team.Goal, results, ct).ConfigureAwait(false);
        events.Add(new(CoordinatorEventType.Completed, Data: finalOutput));

        sw.Stop();

        var completed = taskGraph.Count(t => t.Status == CoordinatorTaskStatus.Completed);
        var failed = taskGraph.Count(t => t.Status == CoordinatorTaskStatus.Failed);

        RecordAbFeedback(failed == 0);

        return new TeamResult
        {
            Success = failed == 0,
            FinalOutput = finalOutput,
            Error = failed > 0 ? $"{failed} task(s) failed" : null,
            Events = events,
            TaskGraph = taskGraph.ToList(),
            CompletedTasks = completed,
            FailedTasks = failed,
            TotalTasks = taskGraph.Count,
            TotalMs = sw.ElapsedMilliseconds
        };
    }

    private async Task<List<RoundPlan>> DecomposeGoalAsync(string goal, CancellationToken ct)
    {
        if (_decomposer.NeedsDecomposition(goal))
        {
            try
            {
                return await _decomposer.DecomposeAsync(goal, "general", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SkillAwareDecomposer failed, falling back to sentence-level decomposition");
            }
        }

        var sentences = goal.Split(new[] { '.', '。', '!', '！', '?', '？', '\n', ';', '；' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return sentences
            .Where(s => s.Length > 5)
            .Select((s, i) => new RoundPlan
            {
                Index = i,
                Goal = s
            })
            .ToList();
    }

    private static List<CoordinatorTask> BuildTaskGraph(
        AgentTeam team,
        string goal,
        IReadOnlyList<RoundPlan> rounds)
    {
        if (rounds.Count == 0)
        {
            return new List<CoordinatorTask>
            {
                new()
                {
                    Id = "task-0",
                    Goal = goal,
                    Assignee = team.Members[0].Name
                }
            };
        }

        if (rounds.Count == 1)
        {
            return new List<CoordinatorTask>
            {
                new()
                {
                    Id = "task-0",
                    Goal = rounds[0].Goal,
                    Assignee = team.Members[0].Name
                }
            };
        }

        var tasks = new List<CoordinatorTask>();
        var members = team.Members;

        for (int i = 0; i < rounds.Count; i++)
        {
            var member = members[i % members.Count];
            var deps = new List<string>();

            // Sequential dataflow: round N depends on round N-1 result for context
            if (i > 0)
                deps.Add($"task-{i - 1}");

            tasks.Add(new CoordinatorTask
            {
                Id = $"task-{i}",
                Goal = rounds[i].Goal,
                Assignee = member.Name,
                DependsOn = deps
            });
        }

        return tasks;
    }

    private async Task<IReadOnlyDictionary<string, string>> ExecuteTaskGraphAsync(
        AgentPool pool,
        IReadOnlyList<CoordinatorTask> tasks,
        List<CoordinatorEvent> events,
        int maxConcurrency,
        CancellationToken ct)
    {
        var queue = new TaskQueue();
        queue.Enqueue(tasks);

        var results = new ConcurrentDictionary<string, string>();
        var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var runningTasks = new List<Task>();

        while (!queue.AllDone && !ct.IsCancellationRequested)
        {
            while (queue.TryDequeue(out var taskId, out var task))
            {
                var capturedTaskId = taskId;
                var capturedTask = task;
                var capturedPool = pool;
                var capturedSemaphore = semaphore;
                var capturedQueue = queue;
                var capturedEvents = events;
                var capturedResults = results;
                var capturedMembers = pool.Members;

                var t = Task.Run(async () =>
                {
                    await capturedSemaphore.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        capturedEvents.Add(new(CoordinatorEventType.TaskStarted, capturedTaskId, capturedTask.Assignee));
                        var output = await capturedPool.RunAgentAsync(
                            capturedTask.Assignee, capturedTask.Goal, ct).ConfigureAwait(false);

                        capturedResults[capturedTaskId] = output;
                        capturedQueue.Complete(capturedTaskId, output);
                        capturedEvents.Add(new(CoordinatorEventType.TaskCompleted, capturedTaskId, capturedTask.Assignee));
                    }
                    catch (Exception ex)
                    {
                        var errMsg = $"{ex.GetType().Name}: {ex.Message}";
                        capturedQueue.Fail(capturedTaskId, errMsg);

                        var updatedTask = capturedQueue.Get(capturedTaskId);
                        if (updatedTask?.Status == CoordinatorTaskStatus.Ready)
                            capturedEvents.Add(new(CoordinatorEventType.TaskRetrying, capturedTaskId, capturedTask.Assignee, errMsg));
                        else
                            capturedEvents.Add(new(CoordinatorEventType.TaskFailed, capturedTaskId, capturedTask.Assignee, errMsg));
                    }
                    finally
                    {
                        capturedSemaphore.Release();
                    }
                }, ct);

                runningTasks.Add(t);
            }

            if (!queue.AllDone && queue.ReadyCount == 0)
                await Task.Delay(50, ct).ConfigureAwait(false);
        }

        await Task.WhenAll(runningTasks).ConfigureAwait(false);
        return results;
    }

    private string BuildSystemPrompt(string agentName, string role)
    {
        if (_promptAssembler != null)
        {
            return _promptAssembler.Assemble(new PromptLayerContext
            {
                WorkspaceRoot = _workspaceRoot,
                Platform = Environment.OSVersion.Platform.ToString().ToLowerInvariant(),
                Date = DateTime.Now.ToString("yyyy-MM-dd"),
                ModeHint = GetModeHint(),
                TaskInstructions = $"You are a {role} agent named {agentName}.",
            });
        }

        if (_promptService == null)
            return $"You are a {role} agent named {agentName}.";

        var abResult = _promptService.GetBestWithAbTest(role, "coordinator");
        if (abResult != null)
        {
            _lastAbResult = abResult;
            return abResult.AllScores.FirstOrDefault(s => s.VariantId == abResult.SelectedVariantId)?.Rendered
                ?? $"You are a {role} agent named {agentName}.";
        }

        var promptId = _promptService.GetBestForTask(role, "coordinator");
        if (promptId != null)
        {
            var rendered = _promptService.Render(promptId.Id, new()
            {
                ["agent_name"] = agentName,
                ["role"] = role
            });
            if (rendered.Success)
                return rendered.Rendered;
        }

        return $"You are a {role} agent named {agentName}.";
    }

    private string? GetModeHint()
    {
        return _profile?.Name switch
        {
            "plan" => "Planning mode: read-only analysis and proposal",
            "build" => "Build mode: full access for editing and testing",
            "chat" => "Chat mode: conversational, no file modifications",
            _ => null
        };
    }

    private void RecordAbFeedback(bool success)
    {
        if (_lastAbResult == null || _promptService == null) return;

        _promptService.RecordFeedback(_lastAbResult.SelectedVariantId, success);
        _lastAbResult = null;
    }

    public async Task<SubSession> SpawnSubagentAsync(
        string agentName,
        string goal,
        string role = "subagent",
        List<string>? allowedTools = null,
        CancellationToken ct = default)
    {
        var session = new SubSession
        {
            AgentName = agentName,
            Role = role,
            Goal = goal,
            AllowedTools = allowedTools ?? new List<string> { "read", "list", "search", "git_status", "git_diff", "git_log" }
        };

        ActiveSessions[session.SessionId] = session;

        _logger.LogInformation("LTAICoordinator: Spawning subagent {Agent} session {Session} for: {Goal}",
            agentName, session.SessionId, goal[..Math.Min(goal.Length, 100)]);

        try
        {
            var systemPrompt = BuildSubagentPrompt(agentName, role, goal, session.AllowedTools);

            var member = new TeamMember
            {
                Name = agentName,
                Role = role,
                SystemPrompt = systemPrompt
            };

            var team = new AgentTeam
            {
                Goal = goal,
                Members = new List<TeamMember> { member }
            };

            var tasks = new List<CoordinatorTask>
            {
                new()
                {
                    Id = session.SessionId,
                    Goal = goal,
                    Assignee = agentName,
                    DependsOn = new List<string>()
                }
            };

            var result = await RunTasksAsync(team, tasks, ct).ConfigureAwait(false);

            session.Result = result.Success ? result.FinalOutput : $"Subagent failed: {result.Error ?? "unknown error"}";
            session.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation("LTAICoordinator: Subagent {Agent} session {Session} completed in {Elapsed}",
                agentName, session.SessionId, session.CompletedAt - session.CreatedAt);
        }
        catch (Exception ex)
        {
            session.Result = $"Subagent error: {ex.Message}";
            session.CompletedAt = DateTime.UtcNow;
            _logger.LogWarning(ex, "LTAICoordinator: Subagent {Agent} session {Session} failed", agentName, session.SessionId);
        }

        return session;
    }

    public async Task<string> SpawnSubagentsParallelAsync(
        Dictionary<string, string> agentRoles,
        string sharedGoal,
        CancellationToken ct = default)
    {
        var tasks = agentRoles.Select(kv =>
            SpawnSubagentAsync(kv.Key, sharedGoal, kv.Value, ct: ct));

        var sessions = await Task.WhenAll(tasks).ConfigureAwait(false);

        var lines = new List<string>();
        foreach (var s in sessions)
        {
            lines.Add($"### {s.AgentName} ({s.Role})");
            lines.Add(s.Result ?? "(no result)");
            lines.Add("");
        }

        var synthesisPrompt = BuildSubagentPrompt("synthesizer", "synthesizer",
            $"Synthesize the following subagent results for goal: {sharedGoal}\n\n{string.Join("\n", lines)}",
            new List<string>());

        var synthesizer = new TeamMember
        {
            Name = "synthesizer",
            Role = "synthesizer",
            SystemPrompt = synthesisPrompt
        };

        var team = new AgentTeam
        {
            Goal = $"Synthesize parallel results for: {sharedGoal}",
            Members = new List<TeamMember> { synthesizer }
        };

        var synthTasks = new List<CoordinatorTask>
        {
            new()
            {
                Id = $"synth_{Guid.NewGuid():N}"[..20],
                Goal = $"Synthesize {sessions.Length} subagent results into coherent final output",
                Assignee = "synthesizer",
                DependsOn = new List<string>()
            }
        };

        var result = await RunTasksAsync(team, synthTasks, ct).ConfigureAwait(false);
        return result.FinalOutput ?? "Synthesis produced no output.";
    }

    private string BuildSubagentPrompt(string agentName, string role, string goal, List<string> allowedTools)
    {
        var toolList = string.Join(", ", allowedTools);
        return $"""
You are a subagent named '{agentName}' with role: {role}.
You have access to these tools: {toolList}.
Your goal: {goal}

Instructions:
- Focus ONLY on the goal above — do not expand scope
- Produce a concise, well-structured result
- If you cannot complete the goal, report exactly why
- Do not ask for clarifications — use your best judgment
""";
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
    }
}
