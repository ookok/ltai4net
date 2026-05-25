using System.Diagnostics;
using System.Text;
using LTAI.AI.Interfaces;
using LTAI.Core.System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.MAF;

/// <summary>
/// Agentic Shell: Read → Think → Edit → Run → Observe cycle.
/// Claude Code design philosophy: the agent owns the terminal loop,
/// file system + git are its UI, each iteration produces an audit trail.
/// </summary>
public sealed class AgenticLoop
{
    private readonly ILivingTreeSystem _lts;
    private readonly ILogger<AgenticLoop> _logger;
    private readonly AgentHookPipeline _hooks;
    private readonly string _workspaceRoot;
    private int _iterationCount;
    private const int MaxIterations = 20;

    public int IterationCount => _iterationCount;
    public List<LoopStep> History { get; } = new();

    public AgenticLoop(ILivingTreeSystem lts, AgentHookPipeline hooks,
        ILogger<AgenticLoop>? logger = null)
    {
        _lts = lts;
        _hooks = hooks;
        _logger = logger ?? NullLogger<AgenticLoop>.Instance;
        _workspaceRoot = Environment.GetEnvironmentVariable("LTAI_WORKSPACE")
            ?? Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Run the full Read→Think→Edit→Run→Observe loop until convergence or max iterations.
    /// </summary>
    public async Task<AgenticLoopResult> RunAsync(string task, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _iterationCount = 0;
        History.Clear();

        await _hooks.RunSessionStartHooksAsync("agentic_loop", ct).ConfigureAwait(false);

        var context = new LoopContext
        {
            Task = task,
            WorkspaceRoot = _workspaceRoot,
            State = new Dictionary<string, string>()
        };

        while (_iterationCount < MaxIterations)
        {
            ct.ThrowIfCancellationRequested();
            _iterationCount++;

            var step = await ExecuteOneIteration(context, ct).ConfigureAwait(false);
            History.Add(step);

            if (step.Phase == LoopPhase.Done || step.Phase == LoopPhase.Failed)
                break;
        }

        await _hooks.RunSessionEndHooksAsync("agentic_loop", ct).ConfigureAwait(false);
        sw.Stop();

        return new AgenticLoopResult
        {
            Completed = History.Count > 0 &&
                History[^1].Phase is LoopPhase.Done or LoopPhase.Success,
            Iterations = _iterationCount,
            TotalMs = sw.ElapsedMilliseconds,
            Steps = History.ToList(),
            FinalOutput = History.LastOrDefault(s => s.Phase == LoopPhase.Done)?.Observation ?? ""
        };
    }

    private async Task<LoopStep> ExecuteOneIteration(LoopContext context, CancellationToken ct)
    {
        var step = new LoopStep { Iteration = _iterationCount };
        var sb = new StringBuilder();

        // 1. READ: gather environment state
        step.Phase = LoopPhase.Read;
        try
        {
            context.State["build_ok"] = await CheckBuildAsync(ct).ConfigureAwait(false) ? "true" : "false";
            context.State["git_clean"] = await CheckGitCleanAsync(ct).ConfigureAwait(false) ? "true" : "false";
            context.State["last_output"] = step.Observation ?? "(first run)";
            step.Phase = LoopPhase.Think;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AgenticLoop: Read phase failed");
            step.Observation = $"Error reading environment: {ex.Message}";
            step.Phase = LoopPhase.Failed;
            return step;
        }

        // 2. THINK: reason about next action
        var thinking = await _lts.ChatAsync(
            $"You are in an agentic loop. Task: {context.Task}\n" +
            $"Environment: build_ok={context.State["build_ok"]}, git_clean={context.State["git_clean"]}\n" +
            $"Last observation: {context.State["last_output"]}\n\n" +
            "Based on the above, what should be the NEXT action? Respond with:\n" +
            "ACTION: <read|edit|run|observe|done>\n" +
            "DETAIL: <what to do>\n\n" +
            "If the task appears complete, respond with ACTION: done\n" +
            "If a previous edit caused errors, respond with ACTION: edit to fix them",
            ct).ConfigureAwait(false);

        var (action, detail) = ParseAction(thinking);
        step.Thinking = thinking;
        step.Action = action;
        step.Detail = detail;

        // 3. EDIT: make changes (with hook check)
        if (action == "edit")
        {
            step.Phase = LoopPhase.Edit;

            var hookCtx = new ToolUseContext
            {
                ToolName = "edit",
                SessionId = "agentic_loop",
                Args = detail,
                Reason = context.Task
            };

            var preResult = await _hooks.RunPreToolHooksAsync(hookCtx, ct).ConfigureAwait(false);
            if (preResult == ToolUseResult.Blocked)
            {
                step.Observation = "Edit blocked by hook";
                step.Phase = LoopPhase.Failed;
                return step;
            }

            await _lts.ChatAsync(detail, ct).ConfigureAwait(false);
            await _hooks.RunPostToolHooksAsync(hookCtx, null, ct).ConfigureAwait(false);
        }

        // 4. RUN: execute tests/build to validate
        if (action == "run" || (action == "edit" && _iterationCount % 3 == 0))
        {
            step.Phase = LoopPhase.Run;

            var hookCtx = new ToolUseContext
            {
                ToolName = "dotnet build",
                SessionId = "agentic_loop"
            };

            var preResult = await _hooks.RunPreToolHooksAsync(hookCtx, ct).ConfigureAwait(false);
            if (preResult == ToolUseResult.Blocked)
            {
                step.Observation = "Build blocked by hook";
                step.Phase = LoopPhase.Failed;
                return step;
            }

            context.State["build_ok"] = await CheckBuildAsync(ct).ConfigureAwait(false) ? "true" : "false";
            if (context.State["build_ok"] == "true")
                context.State["git_clean"] = await CheckGitCleanAsync(ct).ConfigureAwait(false) ? "true" : "false";

            step.Observation = $"Build: {context.State["build_ok"]}, Git: {context.State["git_clean"]}";
            await _hooks.RunPostToolHooksAsync(hookCtx, null, ct).ConfigureAwait(false);
        }

        // 5. OBSERVE: read results and decide
        if (action == "observe" || action == "run" || action == "edit")
        {
            step.Phase = LoopPhase.Observe;
            step.Observation ??= $"Iteration {_iterationCount}: {action} completed. Build={context.State["build_ok"]}";

            if (context.State["build_ok"] == "true" && action == "done")
            {
                step.Phase = LoopPhase.Done;
                _logger.LogInformation("AgenticLoop: Task completed in {Iterations} iterations", _iterationCount);
            }
        }

        if (action == "done")
        {
            step.Phase = LoopPhase.Done;
            step.Observation = "Task marked as complete by the agent.";
        }

        if (_iterationCount >= MaxIterations)
        {
            step.Phase = LoopPhase.Done;
            step.Observation = "Max iterations reached. Stopping.";
        }

        return step;
    }

    private static (string Action, string Detail) ParseAction(string response)
    {
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var action = "think";
        var detail = "";

        foreach (var line in lines)
        {
            if (line.StartsWith("ACTION:", StringComparison.OrdinalIgnoreCase))
                action = line["ACTION:".Length..].Trim().ToLowerInvariant();
            else if (line.StartsWith("DETAIL:", StringComparison.OrdinalIgnoreCase))
                detail = line["DETAIL:".Length..].Trim();
        }

        return (action, detail);
    }

    private async Task<bool> CheckBuildAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "build --no-restore")
            {
                WorkingDirectory = _workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task<bool> CheckGitCleanAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "diff --quiet")
            {
                WorkingDirectory = _workspaceRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}

public enum LoopPhase { Read, Think, Edit, Run, Observe, Done, Success, Failed }

public sealed class LoopStep
{
    public int Iteration { get; init; }
    public LoopPhase Phase { get; set; }
    public string Thinking { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Observation { get; set; }
}

public sealed class LoopContext
{
    public string Task { get; init; } = "";
    public string WorkspaceRoot { get; init; } = "";
    public Dictionary<string, string> State { get; init; } = new();
}

public sealed class AgenticLoopResult
{
    public bool Completed { get; init; }
    public int Iterations { get; init; }
    public long TotalMs { get; init; }
    public List<LoopStep> Steps { get; init; } = new();
    public string FinalOutput { get; init; } = "";
}
