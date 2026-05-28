using System.Diagnostics;
using System.Text;
using LTAI.Agent.Resilience;
using LTAI.Agent.Tools;
using LTAI.Agent.Workflows;
using ModelsDiagnosticInfo = LTAI.Models.DiagnosticInfo;
using ModelsDiagnosticParser = LTAI.Models.DiagnosticParser;
using LTAI.AI.Interfaces;
using LTAI.Core.Configuration;
using LTAI.Core.Governors;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using LTAI.Models;
using LTAI.Tools.CodeEngine;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.MAF;

public sealed class AgenticLoop : IAsyncDisposable
{
    private readonly ILivingTreeSystem _lts;
    private readonly ILogger<AgenticLoop> _logger;
    private readonly AgentHookPipeline _hooks;
    private readonly SystemPromptAssembler? _promptAssembler;
    private readonly MemoryFilesService? _memoryFiles;
    private readonly CSharpCompilationService? _roslynDiagnostics;
    private readonly PartStreamStore? _partStore;
    private readonly IMicroKernel? _kernel;
    private readonly IProjectSpecProvider? _projectSpec;
    private readonly ToolCallRepairPipeline? _repairPipeline;
    private readonly CacheFirstContextBuilder? _cacheCtx;
    private readonly BackpressurePipeline? _backpressure;
    private readonly DebugLoop? _debugLoop;
    private readonly Core.System.AuditLogService? _auditLog;
    private readonly PartAssembler _assembler;
    private readonly Action<Part> _onPartAppendedHandler;
    private readonly Action<Part> _onPartUpdatedHandler;
    private readonly string _workspaceRoot;
    private readonly string _projectLanguage;
    private readonly string _sessionId;
    private readonly AgenticLoopConfig _config;
    private int _iterationCount;
    private int _consecutiveBuildFailures;

    public int IterationCount => _iterationCount;
    public List<LoopStep> History { get; } = new();
    public PartAssembler PartAssembler => _assembler;
    public string SessionId => _sessionId;

    public AgenticLoop(ILivingTreeSystem lts, AgentHookPipeline hooks,
        SystemPromptAssembler? promptAssembler = null,
        MemoryFilesService? memoryFiles = null,
        CSharpCompilationService? roslynDiagnostics = null,
        PartStreamStore? partStore = null,
        IMicroKernel? kernel = null,
        IProjectSpecProvider? projectSpecProvider = null,
        ToolCallRepairPipeline? repairPipeline = null,
        CacheFirstContextBuilder? cacheContext = null,
        BackpressurePipeline? backpressure = null,
        DebugLoop? debugLoop = null,
        AgenticLoopConfig? config = null,
        Core.System.AuditLogService? auditLog = null,
        ILogger<AgenticLoop>? logger = null)
    {
        _lts = lts;
        _hooks = hooks;
        _promptAssembler = promptAssembler;
        _memoryFiles = memoryFiles;
        _roslynDiagnostics = roslynDiagnostics;
        _partStore = partStore;
        _kernel = kernel;
        _projectSpec = projectSpecProvider;
        _repairPipeline = repairPipeline;
        _cacheCtx = cacheContext;
        _backpressure = backpressure;
        _debugLoop = debugLoop;
        _auditLog = auditLog;
        _config = config ?? new AgenticLoopConfig();
        _logger = logger ?? NullLogger<AgenticLoop>.Instance;
        _workspaceRoot = OptionService.Get("LTAI_WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        _projectLanguage = ModelsDiagnosticParser.DetectLanguage(_workspaceRoot);
        _sessionId = Guid.NewGuid().ToString("N")[..8];
        _assembler = new PartAssembler();

        _onPartAppendedHandler = async p =>
        {
            _logger.LogDebug("PartAppended: {Id} {Type}", p.Id, p.GetType().Name);
            if (_partStore != null)
                await _partStore.AppendAsync(_sessionId, p, CancellationToken.None).ConfigureAwait(false);
        };
        _onPartUpdatedHandler = async p =>
        {
            _logger.LogDebug("PartUpdated: {Id} {Type}", p.Id, p.GetType().Name);
            if (_partStore != null)
                await _partStore.AppendAsync(_sessionId, p, CancellationToken.None).ConfigureAwait(false);
        };

        _assembler.OnPartAppended += _onPartAppendedHandler;
        _assembler.OnPartUpdated += _onPartUpdatedHandler;
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

        _repairPipeline?.ResetStormWindow();

        // Cache-First: lock immutable prefix at session start (Reasonix Pillar 1)
        if (_cacheCtx != null && !_cacheCtx.PrefixLocked)
        {
            var systemPrompt = _promptAssembler?.Assemble(new PromptLayerContext
            {
                WorkspaceRoot = _workspaceRoot,
                Platform = Environment.OSVersion.Platform.ToString(),
                Date = DateTime.Now.ToString("yyyy-MM-dd"),
                Shell = "pwsh"
            }) ?? "";
            _cacheCtx.LockPrefix(systemPrompt, "");
        }

        var context = new LoopContext
        {
            Task = task,
            WorkspaceRoot = _workspaceRoot,
            State = new Dictionary<string, string>()
        };

        while (_iterationCount < _config.MaxIterations)
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
                diagLines.Add(ModelsDiagnosticParser.BuildDiagnosticContext(
                    ModelsDiagnosticParser.ParseBuildOutput(buildOutput, _projectLanguage)));
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
            TaskInstructions = $"Agentic loop iteration {_iterationCount}/{_config.MaxIterations}",
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

        var promptForModel = _cacheCtx != null
            ? systemPrompt + "\n\n" + taskPrompt  // prefix already cached via CacheFirstContextBuilder
            : systemPrompt + "\n\n" + taskPrompt;

        var thinking = await _lts.ChatAsync(promptForModel, ct).ConfigureAwait(false);

        // NEEDS_PRO: model self-reports that task exceeds current capability (Reasonix Pillar 3)
        if (DetectNeedsPro(thinking))
        {
            _logger.LogInformation("AgenticLoop: NEEDS_PRO detected — retrying with Pro tier");
            _assembler.FeedText("[NEEDS_PRO — upgrading to Pro tier for this turn]");

            // Retry with an explicit instruction that Pro is available
            var proPrompt = systemPrompt + "\n\n" +
                "[SYSTEM: You are now running on the Pro tier. Full reasoning capability available.]\n\n" +
                taskPrompt;
            thinking = await _lts.ChatAsync(proPrompt, ct).ConfigureAwait(false);

            // Strip the NEEDS_PRO marker from the retried response if present
            if (DetectNeedsPro(thinking))
            {
                thinking = thinking.Split('\n', 2, StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1).FirstOrDefault() ?? thinking;
            }
        }

        // Cache-First: append assistant response to log
        _cacheCtx?.AppendToLog("assistant", thinking);
        _cacheCtx?.AdvanceTurn();

        _assembler.FeedText(thinking);

        // Tool-Call Repair Pipeline (adapted from DeepSeek-Reasonix Pillar 2)
        RepairResult repair;
        string action, detail;
        if (_repairPipeline != null)
        {
            repair = _repairPipeline.Repair(thinking, step.Action);
            action = repair.Action;
            detail = repair.Detail;
            if (repair.AppliedFixes.Count > 0)
            {
                _logger.LogDebug("AgenticLoop: Repair fixes applied: {Fixes}",
                    string.Join(", ", repair.AppliedFixes));
            }
        }
        else
        {
            (action, detail) = ParseAction(thinking);
        }

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

        // 4. RUN: execute tests/build to validate (with backpressure gate)
        if (action == "run" || (action == "edit" && _iterationCount % 3 == 0))
        {
            step.Phase = LoopPhase.Run;

            // BackpressurePipeline: lint→typecheck→test→review before full build
            if (_backpressure != null && action == "edit")
            {
                var bpResult = await _backpressure.CheckAsync(
                    _workspaceRoot, _sessionId, context.Task, ct).ConfigureAwait(false);
                if (!bpResult.AllPassed)
                {
                    var failedGates = bpResult.GateResults.Where(g => !g.Passed).ToList();
                    _logger.LogWarning("AgenticLoop: Backpressure blocked edit — {Count} gate(s) failed",
                        failedGates.Count);

                    // Escalate to DebugLoop for auto-fix when approaching threshold
                    if (_debugLoop != null && _consecutiveBuildFailures >= _config.DebugLoopTriggerThreshold - 1)
                    {
                        try
                        {
                            var bpDiagnostics = string.Join("\n", failedGates.Select(g =>
                                $"[{g.GateName}] {g.Reason} (errors: {g.ErrorCount}, warnings: {g.WarningCount})"));
                            var debugSession = await _debugLoop.DebugAsync(
                                _workspaceRoot, bpDiagnostics,
                                LTAI.Agent.Models.DebugLevel.SemiAuto, 3, 120000, ct).ConfigureAwait(false);

                            if (debugSession.Fixed)
                            {
                                _logger.LogInformation("DebugLoop fixed Backpressure failure");
                                _consecutiveBuildFailures = 0;
                                // Don't return — let the main loop re-execute with the fix applied
                            }
                            else
                            {
                                _logger.LogWarning("DebugLoop could not fix Backpressure (attempts={Count})",
                                    debugSession.Attempts.Count);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "DebugLoop escalation from Backpressure failed");
                        }
                    }

                    step.Observation = $"Backpressure blocked: {string.Join("; ", failedGates.Select(g => g.GateName))}";
                    step.Phase = LoopPhase.Failed;
                    return step;
                }
            }

            var buildCmd = _projectSpec?.GetBuildCommand() ?? "dotnet build --no-restore";
            var (buildName, buildArgs) = SplitCommand(buildCmd);

            var hookCtx = new ToolUseContext
            {
                ToolName = buildCmd,
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
                buildName, buildArgs, ToolState.Executing);
            _assembler.StartToolInvocation(buildPart);

            context.State["build_ok"] = await CheckBuildAsync(ct).ConfigureAwait(false) ? "true" : "false";
            if (context.State["build_ok"] == "false")
            {
                _consecutiveBuildFailures++;
                var (_, runBuildOutput) = await CheckBuildWithOutputAsync(ct).ConfigureAwait(false);
                context.State["build_diagnostics"] = ModelsDiagnosticParser.BuildDiagnosticContext(
                    ModelsDiagnosticParser.ParseBuildOutput(runBuildOutput, _projectLanguage));

                if (_consecutiveBuildFailures >= _config.DebugLoopTriggerThreshold)
                {
                    _logger.LogWarning("AgenticLoop: {Count} consecutive build failures — escalating to DebugLoop",
                        _consecutiveBuildFailures);
                    context.State["build_diagnostics"] +=
                        "\n[CRITICAL: 3+ consecutive failures. Re-analyze ALL recent edits. Consider reverting the last change and trying a different approach.]";

                    // Auto-escalate to DebugLoop for root-cause analysis
                    if (_debugLoop != null)
                    {
                        try
                        {
                            var buildOutput = context.State.GetValueOrDefault("build_diagnostics", "")?.ToString() ?? "";
                            var debugSession = await _debugLoop.DebugAsync(
                                _workspaceRoot, buildOutput,
                                LTAI.Agent.Models.DebugLevel.SemiAuto, 3, 120000, ct).ConfigureAwait(false);

                            if (debugSession.Fixed)
                            {
                                _logger.LogInformation("DebugLoop applied fix — retrying build");
                                _consecutiveBuildFailures = 0; // reset after fix
                            }
                            else if (debugSession.Escalated)
                            {
                                _logger.LogWarning("DebugLoop escalated — marking problem as unfixable");
                                step.Phase = LoopPhase.Failed;
                                step.Observation = "Unfixable: DebugLoop escalated after max attempts";
                                return step;
                            }
                            else
                            {
                                _logger.LogWarning("DebugLoop could not fix automatically (attempts={Count})",
                                    debugSession.Attempts.Count);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "DebugLoop escalation failed");
                        }
                    }
                }
            }
            else
            {
                _consecutiveBuildFailures = 0;
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
                    var testCmd = _projectSpec?.GetTestCommand() ?? "dotnet test --no-build";
                    var (testName, testArgs) = SplitCommand(testCmd);
                    var testPart = new ToolInvocationPart(
                        Guid.NewGuid().ToString("N")[..8],
                        testName, testArgs, ToolState.Executing);
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
            var obs = $"Iteration {_iterationCount}: {action} completed. Build={context.State["build_ok"]}";
            step.Observation ??= obs;

            // Cache-First: compress large tool results before logging (Reasonix Pillar 3)
            if (_cacheCtx != null && step.Observation != null && step.Observation.Length > 3000)
            {
                var compressed = CompressToolResult(step.Observation);
                _cacheCtx.AppendToLog("tool", compressed, action);
            }
            else
            {
                _cacheCtx?.AppendToLog("tool", step.Observation ?? obs, action);
            }

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

        if (_iterationCount >= _config.MaxIterations)
        {
            step.Phase = LoopPhase.Done;
            step.Observation = "Max iterations reached. Stopping.";
        }

        // Audit: record iteration result
        _auditLog?.Record("AgenticLoop", "iteration",
            $"iter={_iterationCount}, phase={step.Phase}, failures={_consecutiveBuildFailures}, action={action}",
            riskScore: _consecutiveBuildFailures > 0
                ? Math.Min(1.0, _consecutiveBuildFailures / 5.0) : 0.0,
            result: step.Phase == LoopPhase.Done ? "completed" : step.Phase.ToString());

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
        if (_kernel != null)
        {
            var buildCmd = _projectSpec?.GetBuildCommand() ?? "dotnet build --no-restore";
            var (buildExe, buildArgs) = SplitCommand(buildCmd);
            var result = await _kernel.ExecuteAsync(new KernelOp
            {
                Command = buildExe,
                Arguments = buildArgs,
                WorkingDirectory = _workspaceRoot,
                Timeout = TimeSpan.FromMinutes(3)
            }, ct).ConfigureAwait(false);
            return (result.Success, result.Data ?? result.Error ?? "");
        }

        try
        {
            var buildCmd = _projectSpec?.GetBuildCommand() ?? "dotnet build --no-restore";
            var (buildExe, buildArgs) = SplitCommand(buildCmd);
            var psi = new ProcessStartInfo(buildExe, buildArgs)
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
        if (_kernel != null)
        {
            var result = await _kernel.GitOpAsync("diff", "--quiet", ct).ConfigureAwait(false);
            return result.Success;
        }

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
        if (_kernel != null)
        {
            var testCmd = _projectSpec?.GetTestCommand() ?? "dotnet test --no-build --verbosity normal";
            var (testExe, testArgs) = SplitCommand(testCmd);
            var result = await _kernel.ExecuteAsync(new KernelOp
            {
                Command = testExe,
                Arguments = testArgs,
                WorkingDirectory = _workspaceRoot,
                Timeout = TimeSpan.FromMinutes(5)
            }, ct).ConfigureAwait(false);
            return result.Data ?? result.Error ?? "";
        }

        try
        {
            var testCmd = _projectSpec?.GetTestCommand() ?? "dotnet test --no-build --verbosity normal";
            var (testExe, testArgs) = SplitCommand(testCmd);
            var psi = new ProcessStartInfo(testExe, testArgs)
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
                ModelsDiagnosticInfo[]? diags = null;
                if (_roslynDiagnostics != null && fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var analysis = await _roslynDiagnostics.AnalyzeFilesAsync(
                        new[] { fullPath }, ct).ConfigureAwait(false);
                    var diagList = new List<ModelsDiagnosticInfo>();
                    foreach (var d in analysis.Diagnostics)
                    {
                        diagList.Add(new ModelsDiagnosticInfo
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

    private static (string Command, string Args) SplitCommand(string combined)
    {
        var idx = combined.IndexOf(' ');
        return idx < 0 ? (combined, "") : (combined[..idx], combined[(idx + 1)..]);
    }

    // ========================================================================
    // Tool result compression (Reasonix Pillar 3)
    // Results >3000 chars are compressed to a summary for the append-only log.
    // The full result is still available via read_file when needed.
    // ========================================================================
    private static string CompressToolResult(string result)
    {
        if (result.Length <= 3000) return result;

        const int headLines = 15;
        const int tailLines = 10;

        var lines = result.Split('\n');
        if (lines.Length <= headLines + tailLines + 5)
            return result; // not enough lines to meaningfully compress

        var head = string.Join('\n', lines.Take(headLines));
        var tail = string.Join('\n', lines.TakeLast(tailLines));

        return $"{head}\n\n... [{lines.Length - headLines - tailLines} lines compressed] ...\n\n{tail}\n\n" +
               $"[Tool result compressed: {result.Length} chars → ~{head.Length + tail.Length + 100} chars. " +
               $"Use read_file to retrieve full content if needed.]";
    }

    // ========================================================================
    // NEEDS_PRO detection (Reasonix Pillar 3)
    // If the model outputs <<<NEEDS_PRO>>> in the first line of its response,
    // the system should retry with a higher-capability model.
    // ========================================================================
    public static bool DetectNeedsPro(string response)
    {
        if (string.IsNullOrEmpty(response)) return false;
        var firstLine = response.Split('\n', 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Contains("<<<NEEDS_PRO>>>", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _assembler.OnPartAppended -= _onPartAppendedHandler;
        _assembler.OnPartUpdated -= _onPartUpdatedHandler;
        History.Clear();
        await Task.CompletedTask;
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
