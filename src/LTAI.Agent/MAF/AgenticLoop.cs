using System.Diagnostics;
using System.Text;
using LTAI.Agent.Tools;
using LTAI.AI.Interfaces;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using LTAI.Models;
using LTAI.Tools.CodeEngine;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.MAF;

public sealed class AgenticLoop
{
    private readonly ILivingTreeSystem _lts;
    private readonly ILogger<AgenticLoop> _logger;
    private readonly AgentHookPipeline _hooks;
    private readonly SystemPromptAssembler? _promptAssembler;
    private readonly MemoryFilesService? _memoryFiles;
    private readonly CSharpCompilationService? _roslynDiagnostics;
    private readonly PartStreamStore? _partStore;
    private readonly PartAssembler _assembler;
    private readonly string _workspaceRoot;
    private readonly string _projectLanguage;
    private readonly string _sessionId;
    private int _iterationCount;
    private const int MaxIterations = 20;

    public int IterationCount => _iterationCount;
    public List<LoopStep> History { get; } = new();
    public PartAssembler PartAssembler => _assembler;
    public string SessionId => _sessionId;

    public AgenticLoop(ILivingTreeSystem lts, AgentHookPipeline hooks,
        SystemPromptAssembler? promptAssembler = null,
        MemoryFilesService? memoryFiles = null,
        CSharpCompilationService? roslynDiagnostics = null,
        PartStreamStore? partStore = null,
        ILogger<AgenticLoop>? logger = null)
    {
        _lts = lts;
        _hooks = hooks;
        _promptAssembler = promptAssembler;
        _memoryFiles = memoryFiles;
        _roslynDiagnostics = roslynDiagnostics;
        _partStore = partStore;
        _logger = logger ?? NullLogger<AgenticLoop>.Instance;
        _workspaceRoot = OptionService.Get("LTAI_WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        _projectLanguage = DiagnosticParser.DetectLanguage(_workspaceRoot);
        _sessionId = Guid.NewGuid().ToString("N")[..8];
        _assembler = new PartAssembler();

        _assembler.OnPartAppended += async p =>
        {
            _logger.LogDebug("PartAppended: {Id} {Type}", p.Id, p.GetType().Name);
            if (_partStore != null)
                await _partStore.AppendAsync(_sessionId, p, CancellationToken.None).ConfigureAwait(false);
        };
        _assembler.OnPartUpdated += async p =>
        {
            _logger.LogDebug("PartUpdated: {Id} {Type}", p.Id, p.GetType().Name);
            if (_partStore != null)
                await _partStore.AppendAsync(_sessionId, p, CancellationToken.None).ConfigureAwait(false);
        };
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
            var (buildOk, buildOutput) = await CheckBuildWithOutputAsync(ct).ConfigureAwait(false);
            context.State["build_ok"] = buildOk ? "true" : "false";
            context.State["git_clean"] = await CheckGitCleanAsync(ct).ConfigureAwait(false) ? "true" : "false";
            context.State["last_output"] = step.Observation ?? "(first run)";

            var diagLines = new List<string>();
            if (!buildOk)
            {
                diagLines.Add(DiagnosticParser.BuildDiagnosticContext(
                    DiagnosticParser.ParseBuildOutput(buildOutput, _projectLanguage)));
            }
            if (_roslynDiagnostics != null && _projectLanguage == "dotnet")
            {
                var roslynResult = await _roslynDiagnostics.AnalyzeWorkspaceAsync(_workspaceRoot, ct)
                    .ConfigureAwait(false);
                if (roslynResult.Diagnostics.Count > 0)
                    diagLines.Add(roslynResult.ToPromptContext());
            }
            context.State["build_diagnostics"] = string.Join("\n", diagLines);

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
        var memoryContext = "";
        if (_memoryFiles != null && _iterationCount == 1)
        {
            memoryContext = BuildMemoryContext(context.Task);
        }

        var diagCtx = context.State.GetValueOrDefault("build_diagnostics", "");

        var gitCleanBool = context.State.TryGetValue("git_clean", out var gc) && gc == "true";
        var buildOkBool = context.State.TryGetValue("build_ok", out var bo) && bo == "true";

        var systemPrompt = _promptAssembler?.Assemble(new PromptLayerContext
        {
            WorkspaceRoot = _workspaceRoot,
            CurrentDir = _workspaceRoot,
            Platform = Environment.OSVersion.Platform.ToString().ToLowerInvariant(),
            Date = DateTime.Now.ToString("yyyy-MM-dd"),
            Shell = "pwsh",
            GitClean = gitCleanBool,
            BuildOk = buildOkBool,
            BuildDiagnostics = diagCtx,
            MemoryContext = memoryContext,
            TaskInstructions = $"Agentic loop iteration {_iterationCount}/{MaxIterations}",
        }) ?? "";

        var taskPrompt = $"Task: {context.Task}\n" +
            $"Environment: build_ok={context.State["build_ok"]}, git_clean={context.State["git_clean"]}\n" +
            $"Last observation: {context.State["last_output"]}\n" +
            (string.IsNullOrEmpty(diagCtx) ? "" : $"{diagCtx}\n") +
            "\nBased on the above, what should be the NEXT action? Respond with:\n" +
            "ACTION: <read|edit|run|observe|done>\n" +
            "DETAIL: <what to do>\n\n" +
            "If the task appears complete, respond with ACTION: done\n" +
            "If a previous edit caused errors, respond with ACTION: edit to fix them";

        var thinking = await _lts.ChatAsync(
            systemPrompt + "\n\n" + taskPrompt,
            ct).ConfigureAwait(false);

        _assembler.FeedText(thinking);

        var (action, detail) = ParseAction(thinking);
        step.Thinking = thinking;
        step.Action = action;
        step.Detail = detail;
        step.Parts = _assembler.Snapshot();

        if (action == "read")
        {
            step.Phase = LoopPhase.Read;
            var readPart = new ToolInvocationPart(
                Guid.NewGuid().ToString("N")[..8],
                "read", detail, ToolState.Executing);
            _assembler.StartToolInvocation(readPart);

            var readPaths = await ReadFilesAsync(detail, ct).ConfigureAwait(false);

            _assembler.UpdateToolState(readPart.Id,
                readPaths.Count > 0 ? ToolState.Completed : ToolState.Error,
                readPaths.Count > 0 ? readPaths.Count : null,
                readPaths.Count == 0 ? "No files found or readable" : null);

            step.Observation = readPaths.Count > 0
                ? $"Read {readPaths.Count} file(s)"
                : $"No readable files found for: {detail}";
        }

        // 3. EDIT: make changes (with hook check)
        else if (action == "edit")
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

            var editPart = new ToolInvocationPart(
                Guid.NewGuid().ToString("N")[..8],
                "edit", detail, ToolState.Executing);
            _assembler.StartToolInvocation(editPart);

            await _lts.ChatAsync(detail, ct).ConfigureAwait(false);
            _assembler.UpdateToolState(editPart.Id, ToolState.Completed, null);

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

            var buildPart = new ToolInvocationPart(
                Guid.NewGuid().ToString("N")[..8],
                "dotnet build", "--no-restore", ToolState.Executing);
            _assembler.StartToolInvocation(buildPart);

            context.State["build_ok"] = await CheckBuildAsync(ct).ConfigureAwait(false) ? "true" : "false";
            if (context.State["build_ok"] == "false")
            {
                var (_, runBuildOutput) = await CheckBuildWithOutputAsync(ct).ConfigureAwait(false);
                context.State["build_diagnostics"] = DiagnosticParser.BuildDiagnosticContext(
                    DiagnosticParser.ParseBuildOutput(runBuildOutput, _projectLanguage));
            }
            if (context.State["build_ok"] == "true")
                context.State["git_clean"] = await CheckGitCleanAsync(ct).ConfigureAwait(false) ? "true" : "false";

            _assembler.UpdateToolState(buildPart.Id,
                context.State["build_ok"] == "true" ? ToolState.Completed : ToolState.Error,
                context.State["build_ok"] == "true" ? "Build succeeded" : "Build failed");

            step.Observation = $"Build: {context.State["build_ok"]}, Git: {context.State["git_clean"]}";
            await _hooks.RunPostToolHooksAsync(hookCtx, null, ct).ConfigureAwait(false);

            if (context.State["build_ok"] == "true")
            {
                var testFailures = new List<TestFailure>();
                try
                {
                    var testPart = new ToolInvocationPart(
                        Guid.NewGuid().ToString("N")[..8],
                        "dotnet test", "--no-build", ToolState.Executing);
                    _assembler.StartToolInvocation(testPart);

                    var testOutput = await CaptureTestOutputAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(testOutput))
                    {
                        testFailures = TestResultParser.Parse(testOutput);
                        if (testFailures.Count > 0)
                        {
                            var failureContext = TestResultParser.BuildFailureContext(testFailures);
                            step.Observation += $"\n\n{failureContext}";
                            _logger.LogInformation("AgenticLoop: Found {Count} test failures", testFailures.Count);
                        }
                    }

                    _assembler.UpdateToolState(testPart.Id,
                        testFailures.Count == 0 ? ToolState.Completed : ToolState.Error,
                        testFailures.Count == 0 ? "All tests passed" : $"{testFailures.Count} failures");
                }
                catch { /* intentional: cleanup may fail */ }
            }
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

    private string BuildMemoryContext(string task)
    {
        try
        {
            var relevant = _memoryFiles!.RetrieveRelevant(task, topK: 3);
            if (relevant.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("Relevant knowledge from memory files:");
            foreach (var mf in relevant)
            {
                if (!string.IsNullOrEmpty(mf.Summary))
                    sb.AppendLine($"- [{mf.Domain}] {mf.Summary}");
                foreach (var fact in mf.Facts.Take(5))
                    sb.AppendLine($"  • {fact.Statement}");
            }
            sb.AppendLine();
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BuildMemoryContext failed, returning empty context");
            return "";
        }
    }

    private async Task<bool> CheckBuildAsync(CancellationToken ct)
    {
        var (success, _) = await CheckBuildWithOutputAsync(ct).ConfigureAwait(false);
        return success;
    }

    private async Task<(bool success, string output)> CheckBuildWithOutputAsync(CancellationToken ct)
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
            if (p == null) return (false, "");
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode == 0, error + "\n" + output);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "CheckBuildWithOutputAsync failed"); return (false, ""); }
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
        catch (Exception ex) { _logger.LogWarning(ex, "CheckGitCleanAsync failed"); return false; }
    }

    private async Task<string> CaptureTestOutputAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "test --no-build --verbosity normal")
            {
                WorkingDirectory = _workspaceRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return output + "\n" + error;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "CaptureTestOutputAsync failed"); return ""; }
    }

    private async Task<List<string>> ReadFilesAsync(string detail, CancellationToken ct)
    {
        var paths = new List<string>();
        var matches = System.Text.RegularExpressions.Regex.Matches(detail, @"""([^""]+)""|'([^']+)'|([^\s,;]+)");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var p = m.Groups[1].Success ? m.Groups[1].Value :
                    m.Groups[2].Success ? m.Groups[2].Value :
                    m.Groups[3].Value;
            if (!string.IsNullOrWhiteSpace(p))
                paths.Add(p);
        }
        if (paths.Count == 0 && !string.IsNullOrWhiteSpace(detail))
            paths.Add(detail.Trim());

        var readPaths = new List<string>();
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path, _workspaceRoot);
            if (!fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(fullPath))
                continue;

            try
            {
                var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
                DiagnosticInfo[]? diags = null;
                if (_roslynDiagnostics != null && fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var analysis = await _roslynDiagnostics.AnalyzeFilesAsync(
                        new[] { fullPath }, ct).ConfigureAwait(false);
                    var diagList = new List<DiagnosticInfo>();
                    foreach (var d in analysis.Diagnostics)
                    {
                        diagList.Add(new DiagnosticInfo
                        {
                            FilePath = d.FilePath,
                            LineNumber = d.Line,
                            ColumnNumber = d.Column,
                            Severity = d.Severity,
                            Code = d.Code,
                            Message = d.Message
                        });
                    }
                    diags = diagList.ToArray();
                }
                _assembler.AddFilePart(fullPath, content, diags);
                readPaths.Add(fullPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReadFiles: Failed to read {Path}", path);
            }
        }

        return readPaths;
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
    public Part[] Parts { get; set; } = Array.Empty<Part>();
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
