using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using LTAI.AI.Governors;
using LTAI.Core.Governors;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using LTAI.Agent.Models;

namespace LTAI.Agent.Resilience;

public sealed class DebugLoop
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IChatClient _chatClient;
    private readonly CorrectionMemory? _correctionMemory;
    private readonly ILogger<DebugLoop>? _logger;
    private readonly string _persistPath;
    private readonly HarnessProfile? _harnessProfile;
    private readonly ConcurrentDictionary<string, DebugSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _backups = new();
    private readonly string _repoPath;
    private readonly IMicroKernel? _kernel;
    private readonly IProjectSpecProvider? _projectSpec;

    private const int MaxSourceLines = 400;
    private const int ContextPadding = 30;

    private const string TracePrompt = """
        You are an expert C# debugger performing single-step execution tracing. Simulate stepping through the code to find the ROOT CAUSE of the error — NOT just the crash site.

        ## Error Details
        - Exception Type: {0}
        - Exception Message: {1}
        - Crash File: {2}
        - Crash Line: {3}
        - Full Stack Trace:
        {4}

        ## Crash Site Source ({2})
        ```csharp
        {5}
        ```

        ## Caller Contexts (files in the stack trace)
        {6}

        ## Instructions: Trace-Then-Fix
        ### Phase 1 — Trace Data Flow to Root Cause
        1. Start at the CRASH SITE (file {2}, line {3}) and trace BACKWARD through each stack frame
        2. For each frame, identify: what value was passed? where did it originate? was it null/empty/invalid at the source?
        3. Determine the TRUE ROOT CAUSE — the earliest point in the call chain where the bad state originated
        4. State the root cause clearly: "ROOT_CAUSE: <explanation>" in your response

        ### Phase 2 — Generate Minimal Fix at Root Cause
        5. Fix the code at the ROOT CAUSE location (NOT necessarily the crash site)
        6. If the root cause is in {2}, output the complete fixed file in a ```csharp block
        7. If the root cause is in a different file (from stack trace), indicate which file and what change
        8. Produce a MINIMAL fix — change only what is necessary
        9. Pay attention to: null safety, validation at boundaries, defensive copies, edge cases
        """;

    private const string FixPrompt = """
        You are an expert C# debugger. Fix the code based on root cause analysis.

        ## Root Cause (previously traced)
        {0}

        ## Error Details
        - Exception Type: {1}
        - File: {2}
        ## Source Code ({2})
        ```csharp
        {3}
        ```

        ## Instructions
        1. Apply the fix at the ROOT CAUSE location identified above
        2. Output the COMPLETE fixed file content inside a ```csharp code block
        3. Do NOT add explanatory text outside the code block
        4. Preserve all existing code that does not need to change
        """;

    private const string PatchPrompt = """
        You are an expert C# debugger. Produce a MINIMAL search-and-replace patch — change only the broken lines.

        ## Error Details
        - Exception Type: {0}
        - Exception Message: {1}
        - File: {2}
        - Line: {3}

        ## Fix Context
        {4}

        ## Source Code ({2})
        ```csharp
        {5}
        ```

        ## Instructions
        1. Identify the MINIMAL set of lines that need to change
        2. Output in SEARCH/REPLACE patch format:
        SEARCH: <<<
        // exact original lines
        >>>
        REPLACE: <<<
        // corrected lines
        >>>
        3. Include only 1-10 surrounding lines in SEARCH for unambiguous matching
        4. Do NOT rewrite the entire file — change only what is broken
        5. Ensure the REPLACE block contains valid C# code
        """;

    private const string DecomposePrompt = """
        You are an expert C# debugger. A fix for this error has been attempted {0} times and keeps failing.

        ## Error
        - Type: {1}
        - Message: {2}
        - File: {3}:{4}

        ## Previous Attempts
        {5}

        ## Instructions
        1. Break this problem into smaller, independent sub-fixes
        2. List each sub-fix as:
           SUB_FIX: <description> | <file>:<line> | <change_type>
        3. Order by dependency (foundation fixes first)
        4. Keep each sub-fix to 1-5 lines of change
        """;

    private const string FixAnchorTemplate = """
        ## FIX PLAN (attempt {0} of {1})
        ### Previous Attempts
        {2}
        ### Current Strategy
        Try a different approach than before. If previous fixes were null checks, try initialization ordering. If previous fixes were type casts, try changing the type.
        """;

    private const string PreAnalysisPrompt = """
        You are an expert C# code reviewer performing proactive defect detection. Analyze this code WITHOUT running it — find bugs through static analysis.

        ## Source File
        ```csharp
        {0}
        ```

        ## Instructions
        1. Simulate execution flow through each method
        2. Identify potential issues WITHOUT needing log output:
           - NullReference risks: variables that could be null at usage points
           - Unhandled edge cases: missing validation, empty collections, boundary conditions
           - Thread safety issues: shared state without synchronization
           - Resource leaks: undisposed objects, unclosed streams
           - Logic errors: incorrect conditions, off-by-one, inverted booleans
           - Exception swallowing: empty catch blocks, catch(Exception) without logging
        3. For each issue found, output:
           ISSUE|<severity>|<line>|<category>|<description>
           FIX|<line>|<suggested code change>
        4. Be specific — reference exact line numbers and variable names
        5. If no issues found, output: "NO_ISSUES"
        """;


    public DebugLoop(IChatClient chatClient, CorrectionMemory? correctionMemory = null,
        ILogger<DebugLoop>? logger = null, string? persistPath = null,
        HarnessProfile? harnessProfile = null, string? repoPath = null,
        IMicroKernel? kernel = null, IProjectSpecProvider? projectSpec = null)
    {
        _chatClient = chatClient;
        _correctionMemory = correctionMemory;
        _logger = logger;
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "debug_loop.json");
        _harnessProfile = harnessProfile;
        _repoPath = repoPath ?? Repository.Discover(Directory.GetCurrentDirectory()) ?? "";
        _kernel = kernel;
        _projectSpec = projectSpec;
    }

    public DebugSession Debug(string target, string args, DebugLevel level = DebugLevel.SemiAuto,
        int maxAttempts = 3)
    {
        // Debug-only sync wrapper: blocking is acceptable in debug/diagnostic paths
        return Task.Run(() => DebugAsync(target, args, level, maxAttempts)).GetAwaiter().GetResult();
    }

    public async Task<DebugSession> DebugAsync(string target, string args, DebugLevel level = DebugLevel.SemiAuto,
        int maxAttempts = 3, int timeoutMs = 120000, CancellationToken ct = default)
    {
        var session = new DebugSession
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Target = target,
            Args = args,
            Level = level,
            MaxAttempts = maxAttempts
        };

        _sessions[session.Id] = session;
        var sw = Stopwatch.StartNew();

        try
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var error = RunTarget(target, args, timeoutMs);

                if (error == null)
                {
                    session.Fixed = true;
                    _logger?.LogInformation("DebugLoop [{Id}]: Target ran successfully", session.Id);
                    break;
                }

                _logger?.LogInformation("DebugLoop [{Id}] attempt {Attempt}: {ExceptionType} at {File}:{Line}",
                    session.Id, attempt + 1, error.ExceptionType, error.FilePath, error.LineNumber);

                if (level == DebugLevel.Analyze)
                {
                    break;
                }

                if (string.IsNullOrEmpty(error.FilePath) || !File.Exists(error.FilePath))
                {
                    _logger?.LogWarning("DebugLoop [{Id}]: Cannot fix — no source file available", session.Id);
                    session.Escalated = true;
                    break;
                }

                BackupFile(error.FilePath);

                var fix = await GenerateAndApplyAsync(error, attempt, session, ct).ConfigureAwait(false);
                session.Attempts.Add(fix);

                if (fix.Result == AttemptResult.Fixed)
                {
                    var verifyError = RunTarget(target, args, timeoutMs);
                    if (verifyError == null)
                    {
                        session.Fixed = true;
                        _logger?.LogInformation("DebugLoop [{Id}]: Fix verified — target runs successfully", session.Id);
                        RecordCorrection(error, fix);
                    }
                    else
                    {
                        fix.Result = AttemptResult.Partial;
                        fix.NewError = verifyError.ExceptionMessage;
                        _logger?.LogWarning("DebugLoop [{Id}]: Fix applied but target still fails: {Msg}",
                            session.Id, verifyError.ExceptionMessage);
                    }
                    break;
                }

                if (fix.Result == AttemptResult.Hitl || fix.Result == AttemptResult.Worse)
                {
                    session.Escalated = true;
                    Rollback(error.FilePath);
                    break;
                }

                if (fix.Result == AttemptResult.Unchanged && attempt >= 1)
                {
                    session.Escalated = true;
                    Rollback(error.FilePath);
                    break;
                }
            }
        }
        finally
        {
            sw.Stop();
            session.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
            CleanupBackups();
        }

        Save();
        return session;
    }

    private ErrorSnapshot? RunTarget(string target, string args, int timeoutMs = 120000)
    {
        try
        {
            var runCmd = _projectSpec?.GetRunCommand() ?? "dotnet run";
            var spaceIdx = runCmd.IndexOf(' ');
            var exe = spaceIdx > 0 ? runCmd[..spaceIdx] : runCmd;
            var action = spaceIdx > 0 ? runCmd[(spaceIdx + 1)..] + " " : "";

            if (_kernel != null)
            {
                var result = _kernel.ExecuteAsync(new KernelOp
                {
                    Command = exe,
                    Arguments = $"{action}--project {target} {args}",
                    Timeout = TimeSpan.FromMilliseconds(timeoutMs)
                }, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

                if (result.Success && string.IsNullOrWhiteSpace(result.Error)) return null;
                return ParseError(result.Error ?? "", result.Data ?? "", target);
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"{action}--project {target} {args}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { _logger?.LogWarning(ex, "DebugLoop: Failed to kill process"); }
                return null;
            }

            if (process.ExitCode == 0) return null;

            return ParseError(stderr, stdout, target);
        }
        catch (Exception ex)
        {
            return new ErrorSnapshot
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                ExceptionType = ex.GetType().Name,
                ExceptionMessage = ex.Message,
                TracebackText = ex.StackTrace ?? ""
            };
        }
    }

    private static ErrorSnapshot? ParseError(string stderr, string stdout, string target)
    {
        var combined = stderr + "\n" + stdout;
        if (string.IsNullOrWhiteSpace(combined)) return null;

        var match = Regex.Match(combined, @"^(\w+\.\w+Exception):\s*(.+)$", RegexOptions.Multiline);
        if (!match.Success) match = Regex.Match(combined, @"error CS\d+:\s*(.+)$", RegexOptions.Multiline);

        string exceptionType = "UnknownError";
        string message = combined.Length > 500 ? combined[..500] : combined;

        if (match.Success)
        {
            exceptionType = match.Groups[1].Success ? match.Groups[1].Value : "BuildError";
            message = match.Groups[2].Success ? match.Groups[2].Value : match.Value;
        }

        var fileMatch = Regex.Match(combined, @"([\w\\\/\.\-]+\.cs)\((\d+)");
        if (!fileMatch.Success)
            fileMatch = Regex.Match(combined, @"(?:in\s+|at\s+)?([\w\\\/\.]+\.cs):line\s+(\d+)");
        var testMatch = Regex.Match(combined, @"(?:\[xUnit|Failed\s+)([\w\.]+\.\w+)(?:\(|\[)");

        var filePath = fileMatch.Groups[1].Success ? fileMatch.Groups[1].Value : "";
        if (!string.IsNullOrEmpty(filePath) && !Path.IsPathRooted(filePath))
        {
            var resolved = ResolveFilePath(filePath);
            if (resolved != null) filePath = resolved;
        }

        return new ErrorSnapshot
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            ExceptionType = exceptionType,
            ExceptionMessage = message,
            TracebackText = combined.Length > 2000 ? combined[..2000] : combined,
            FilePath = filePath,
            LineNumber = fileMatch.Groups[2].Success ? int.Parse(fileMatch.Groups[2].Value) : 0,
            TestName = testMatch.Groups[1].Success ? testMatch.Groups[1].Value : null
        };
    }

    private static string? ResolveFilePath(string relativePath)
    {
        try
        {
            var candidate = Path.GetFullPath(relativePath);
            if (File.Exists(candidate)) return candidate;

            var files = Directory.GetFiles(Directory.GetCurrentDirectory(), Path.GetFileName(relativePath),
                SearchOption.AllDirectories);
            return files.FirstOrDefault(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex, "DebugLoop: Failed to resolve file path");
            return null;
        }
    }

    private static bool IsFixAllowed(HarnessMode mode, string safetyLevel) => safetyLevel switch
    {
        "safe" => true,
        "review" => mode != HarnessMode.Controlled,
        "dangerous" => mode == HarnessMode.Evolutionary,
        _ => true
    };

    private static List<string> GetRepairStrategies(string errorCode)
    {
        if (DiagnosticParser.RepairCodeMap.TryGetValue(errorCode, out var strategies))
            return strategies;
        return DiagnosticParser.RepairCodeMap.TryGetValue("default", out var defaultStrategies)
            ? defaultStrategies
            : new List<string> { "manual-fix" };
    }

    private async Task<FixAttempt> GenerateAndApplyAsync(ErrorSnapshot error, int attemptNum,
        DebugSession session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var fix = new FixAttempt
        {
            AttemptNumber = attemptNum + 1,
            Error = error,
            AppliedFile = error.FilePath
        };

        try
        {
            var diagnostics = DiagnosticParser.Parse(error.TracebackText);
            var primaryCode = diagnostics.Count > 0 ? diagnostics[0].DiagnosticCode : error.ExceptionType;
            FixInstinctStore.RecordAttempt(primaryCode);

            if (diagnostics.Count > 0)
            {
                var diag = diagnostics[0];
                var mode = _harnessProfile?.Mode ?? HarnessMode.Hybrid;
                if (!IsFixAllowed(mode, diag.SafetyLevel))
                {
                    _logger?.LogWarning("DebugLoop [{Id}]: Fix blocked by harness safety: {Code} ({Level})",
                        session.Id, diag.DiagnosticCode, diag.SafetyLevel);
                    fix.Result = AttemptResult.Unchanged;
                    fix.NewError = $"Fix blocked by harness safety: {diag.DiagnosticCode} ({diag.SafetyLevel})";
                    sw.Stop();
                    fix.DurationMs = sw.Elapsed.TotalMilliseconds;
                    return fix;
                }
            }

            var llmTokens = 0;
            fix.GeneratedPatch = await GeneratePatchAsync(error, attemptNum, session, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(fix.GeneratedPatch))
            {
                llmTokens = EstimateTokens(fix.GeneratedPatch);
                _logger?.LogInformation("DebugLoop [{Id}]: LLM generated patch ({Tokens} est. tokens)",
                    session.Id, llmTokens);

                var sourceLines = ReadSourceContext(error.FilePath, error.LineNumber);
                error.SourceContext = string.Join('\n', sourceLines);
            }
            else
            {
                _logger?.LogWarning("DebugLoop [{Id}]: LLM returned empty patch", session.Id);
                fix.Result = AttemptResult.Unchanged;
                sw.Stop();
                fix.DurationMs = sw.Elapsed.TotalMilliseconds;
                return fix;
            }

            fix.LlmTokens = llmTokens;
            fix.LlmProvider = "l2";

            var applied = ApplyFix(error.FilePath, fix.GeneratedPatch);
            fix.AppliedLine = error.LineNumber;

            if (applied)
            {
                fix.Result = AttemptResult.Fixed;
                fix.GitCommit = CaptureCurrentState(error.FilePath);
                var diags = DiagnosticParser.Parse(error.TracebackText);
                var code = diags.Count > 0 ? diags[0].DiagnosticCode : error.ExceptionType;
                var strat = diags.Count > 0 ? string.Join(", ", GetRepairStrategies(diags[0].DiagnosticCode)) : "auto-fix";
                FixInstinctStore.RecordSuccess(code, fix.GeneratedPatch, strat, error.FilePath);
            }
            else
            {
                fix.Result = AttemptResult.Unchanged;
                if (string.IsNullOrEmpty(fix.GeneratedPatch))
                    FixInstinctStore.RecordEmptyPatch();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DebugLoop [{Id}]: GenerateAndApply failed", session.Id);
            fix.Result = AttemptResult.Worse;
            fix.NewError = ex.Message;
        }

        sw.Stop();
        fix.DurationMs = sw.Elapsed.TotalMilliseconds;
        return fix;
    }

    private async Task<string> GeneratePatchAsync(ErrorSnapshot error, int attemptNum, DebugSession session, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(error.FilePath) || !File.Exists(error.FilePath))
            return "";

        var sourceText = ReadFileSnippet(error.FilePath, error.LineNumber);
        if (string.IsNullOrEmpty(sourceText))
            return "";

        var stackTrace = error.TracebackText.Length > 2000
            ? error.TracebackText[..2000]
            : error.TracebackText;

        var callerContext = BuildCallerContext(stackTrace, error.FilePath);
        var diagnostics = DiagnosticParser.Parse(error.TracebackText);
        var repairStrategies = string.Join(", ", GetRepairStrategies(error.ExceptionType));
        var diagnosticContext = diagnostics.Count > 0
            ? "\n## Structured Diagnostics\n" + DiagnosticParser.ToPromptContext(diagnostics)
                + "\n\nRepair strategies: " + repairStrategies
            : "";

        var primaryCode = diagnostics.Count > 0 ? diagnostics[0].DiagnosticCode : error.ExceptionType;
        var instinctContext = FixInstinctStore.GetContextForDiagnostic(primaryCode);
        if (instinctContext.Length > 0)
            instinctContext = "\n## Learned Instincts\n" + instinctContext;

        var subagentHint = GetSubagentHint(primaryCode);

        var fixAnchor = attemptNum > 0
            ? BuildFixAnchor(error, attemptNum, session)
            : "";

        try
        {
            var temperature = AdaptiveRetryTemperature(attemptNum);

            if (attemptNum == 0)
            {
                var tracePrompt = string.Format(TracePrompt,
                    error.ExceptionType,
                    error.ExceptionMessage,
                    error.FilePath,
                    error.LineNumber,
                    stackTrace,
                    sourceText,
                    callerContext) + subagentHint + "\n" + instinctContext + diagnosticContext;

                var traceOptions = new ChatOptions
                {
                    Temperature = 0.1f,
                    MaxOutputTokens = 4096
                };

                var traceResponse = await _chatClient.GetResponseAsync(tracePrompt, traceOptions, ct).ConfigureAwait(false);
                var traceText = traceResponse.Text ?? "";

                if (traceText.TrimStart().StartsWith("#error", StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("DebugLoop: LLM indicated unfixable error");
                    return "";
                }

                var rootCause = ExtractRootCause(traceText);
                _logger?.LogInformation("DebugLoop: Root cause traced — {RootCause}",
                    rootCause.Length > 120 ? rootCause[..120] + "..." : rootCause);

                if (traceText.Contains("```csharp") || traceText.Contains("```cs") || traceText.Contains("SEARCH:"))
                {
                    return ExtractCodeBlock(traceText);
                }

                var fixPrompt = string.Format(FixPrompt,
                    rootCause,
                    error.ExceptionType,
                    error.FilePath,
                    sourceText) + subagentHint + "\n" + instinctContext + diagnosticContext;

                var fixOptions = new ChatOptions
                {
                    Temperature = 0.05f,
                    MaxOutputTokens = 8192
                };

                var fixResponse = await _chatClient.GetResponseAsync(fixPrompt, fixOptions, ct).ConfigureAwait(false);
                var fixText = fixResponse.Text ?? "";

                if (fixText.TrimStart().StartsWith("#error", StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("DebugLoop: LLM indicated unfixable error after tracing");
                    return "";
                }

                return ExtractCodeBlock(fixText);
            }
            else
            {
                var patchPrompt = string.Format(PatchPrompt,
                    error.ExceptionType,
                    error.ExceptionMessage,
                    error.FilePath,
                    error.LineNumber,
                    fixAnchor,
                    sourceText) + subagentHint + "\n" + instinctContext + diagnosticContext;

                var patchOptions = new ChatOptions
                {
                    Temperature = temperature,
                    MaxOutputTokens = 4096
                };

                var patchResponse = await _chatClient.GetResponseAsync(patchPrompt, patchOptions, ct).ConfigureAwait(false);
                var patchText = patchResponse.Text ?? "";

                if (patchText.Contains("SEARCH:") && patchText.Contains("REPLACE:"))
                    return patchText;

                if (patchText.TrimStart().StartsWith("#error", StringComparison.OrdinalIgnoreCase))
                {
                    if (attemptNum >= 2)
                        return await DecomposeTaskAsync(error, attemptNum, sourceText, ct).ConfigureAwait(false);
                    return "";
                }

                return ExtractCodeBlock(patchText);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("DebugLoop: LLM trace/fix failed: {Message}", ex.Message);
            return HeuristicFallback(error, attemptNum) ?? "";
        }
    }

    private string BuildFixAnchor(ErrorSnapshot error, int attemptNum, DebugSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Format(FixAnchorTemplate,
            attemptNum + 1,
            session.MaxAttempts,
            string.Join("\n", session.Attempts.Select(a => $"- Attempt {a.AttemptNumber}: {a.Result} — {a.Error?.ExceptionType ?? "unknown"}"))));

        sb.AppendLine("## Device Parameters");
        sb.AppendLine($"- Harness Mode: {_harnessProfile?.Mode ?? HarnessMode.Hybrid}");
        sb.AppendLine($"- Error: {error.ExceptionType} at {error.FilePath}:{error.LineNumber}");
        sb.AppendLine($"- Message: {error.ExceptionMessage[..Math.Min(error.ExceptionMessage.Length, 150)]}");
        sb.AppendLine();
        sb.AppendLine("The previous approach did not work. Try a fundamentally different strategy:");
        sb.AppendLine("- If you added null checks before, try changing initialization order instead");
        sb.AppendLine("- If you changed types, try adding validation at the data boundary instead");
        sb.AppendLine("- Consider whether the root cause is in a DIFFERENT file from the crash site");
        return sb.ToString();
    }

    private static float AdaptiveRetryTemperature(int attemptNum) => attemptNum switch
    {
        0 => 0.1f,
        1 => 0.3f,
        2 => 0.5f,
        _ => 0.7f
    };

    private static string GetSubagentHint(string diagnosticCode) => diagnosticCode switch
    {
        "CS8602" or "CS8600" or "NullReference" => "[Subagent: NullSafety] Focus on null input propagation — check callee signatures, evaluate whether the null came from a service/dictionary/indexer.",
        "CS1061" or "CS0117" => "[Subagent: TypeCheck] Check actual runtime type vs declared type — verify namespaces, extension methods, and using directives.",
        "CS0246" or "CS0103" => "[Subagent: Namespace] Check assembly references, project references, and using directives. Consider nuget package version mismatch.",
        "CS0165" => "[Subagent: FlowAnalysis] Verify all code paths assign a value — check branching, try/catch, and early returns.",
        "CS1503" => "[Subagent: TypeConversion] Check implicit/explicit conversion operators and generic type constraints.",
        "FileNotFound" => "[Subagent: PathResolution] Check working directory, relative path resolution, and project file copy settings.",
        _ => "[Subagent: General] Check data flow from origin to crash site — trace where the bad state first entered the system."
    };

    private async Task<string> DecomposeTaskAsync(ErrorSnapshot error, int attemptNum, string sourceText, CancellationToken ct)
    {
        try
        {
            var previousAttempts = string.Join("\n",
                Enumerable.Range(1, attemptNum).Select(i => $"- Attempt {i}: failed"));

            var prompt = string.Format(DecomposePrompt,
                attemptNum,
                error.ExceptionType,
                error.ExceptionMessage,
                error.FilePath,
                error.LineNumber,
                previousAttempts);

            var options = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 2048 };
            var response = await _chatClient.GetResponseAsync(prompt, options, ct).ConfigureAwait(false);
            var text = response.Text ?? "";

            var subFixes = new List<(string desc, string changeType)>();
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("SUB_FIX:"))
                {
                    var parts = line["SUB_FIX:".Length..].Trim().Split('|', 3);
                    if (parts.Length >= 2)
                        subFixes.Add((parts[0].Trim(), parts[1].Trim()));
                }
            }

            if (subFixes.Count == 0) return "";

            _logger?.LogInformation("DebugLoop: Decomposed into {Count} sub-fixes: {Fixes}",
                subFixes.Count, string.Join("; ", subFixes.Take(3).Select(f => f.desc)));

            var fixPrompt = new StringBuilder();
            fixPrompt.AppendLine($"Apply the FIRST sub-fix only. File: {error.FilePath}");
            fixPrompt.AppendLine("```csharp");
            fixPrompt.AppendLine(sourceText.Length > 3000 ? sourceText[..3000] : sourceText);
            fixPrompt.AppendLine("```");
            fixPrompt.AppendLine();
            fixPrompt.AppendLine($"First sub-fix: {subFixes[0].desc}");
            fixPrompt.AppendLine();
            fixPrompt.AppendLine("Output in SEARCH/REPLACE patch format. Change only 1-10 lines.");

            var fixOptions = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 2048 };
            var fixResponse = await _chatClient.GetResponseAsync(fixPrompt.ToString(), fixOptions, ct).ConfigureAwait(false);
            var fixText = fixResponse.Text ?? "";

            if (fixText.Contains("SEARCH:") && fixText.Contains("REPLACE:"))
                return fixText;

            return ExtractCodeBlock(fixText);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("DebugLoop: Decompose failed: {Message}", ex.Message);
            return "";
        }
    }

    private static string ExtractRootCause(string traceText)
    {
        var match = Regex.Match(traceText, @"ROOT_CAUSE:\s*(.+?)(?:\n|$)", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        match = Regex.Match(traceText, @"(?:root cause|origin of the (?:bug|error|issue))[:\s-]+(.+?)(?:\n|$)", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        var lines = traceText.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Contains("ROOT_CAUSE") || lines[i].ToLowerInvariant().Contains("root cause"))
            {
                var idx = lines[i].IndexOf(':');
                return idx >= 0 ? lines[i][(idx + 1)..].Trim() : lines[i];
            }
        }

        return "Unable to determine root cause autonomously";
    }

    private static string BuildCallerContext(string stackTrace, string crashFile)
    {
        var frames = ExtractStackFrames(stackTrace);
        var sb = new StringBuilder();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { crashFile };

        foreach (var (file, line, method) in frames.Take(5))
        {
            if (!seenFiles.Add(file)) continue;
            if (!File.Exists(file)) continue;

            try
            {
                var snippet = ReadFileSnippetStatic(file, line);
                if (!string.IsNullOrEmpty(snippet))
                {
                    sb.AppendLine($"### {file}:{line} ({method})");
                    sb.AppendLine("```csharp");
                    sb.AppendLine(snippet);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex, "DebugLoop: Failed to read caller context snippet"); }
        }

        return sb.Length > 0 ? sb.ToString() : "(No caller context available)";
    }

    private static List<(string File, int Line, string Method)> ExtractStackFrames(string stackTrace)
    {
        var frames = new List<(string, int, string)>();

        var matches = Regex.Matches(stackTrace,
            @"at\s+(.+?)\s+in\s+(.+?):line\s+(\d+)", RegexOptions.Multiline);

        foreach (Match m in matches)
        {
            var method = m.Groups[1].Value.Trim();
            var file = m.Groups[2].Value.Trim();
            int.TryParse(m.Groups[3].Value, out var line);

            if (!string.IsNullOrEmpty(file))
            {
                var resolved = ResolveFilePath(file);
                if (resolved != null)
                    frames.Add((resolved, line, method));
            }
        }

        return frames;
    }

    private static string? ReadFileSnippetStatic(string filePath, int focusLine)
    {
        try
        {
            var allLines = File.ReadAllLines(filePath);
            var start = Math.Max(0, focusLine - 20);
            var end = Math.Min(allLines.Length, focusLine + 20);

            var sb = new StringBuilder();
            for (int i = start; i < end; i++)
            {
                sb.AppendLine($"{i + 1,5}: {allLines[i]}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex, "DebugLoop: Failed to read file snippet static");
            return null;
        }
    }

    private static string ExtractCodeBlock(string llmResponse)
    {
        var match = Regex.Match(llmResponse, @"```(?:csharp|cs|c#)?\s*\n(.*?)```", RegexOptions.Singleline);
        if (match.Success && match.Groups[1].Value.Trim().Length > 20)
            return match.Groups[1].Value.Trim();

        match = Regex.Match(llmResponse, @"```\s*\n(.*?)```", RegexOptions.Singleline);
        if (match.Success && match.Groups[1].Value.Trim().Length > 20)
            return match.Groups[1].Value.Trim();

        if (llmResponse.Trim().Length > 50 && llmResponse.Contains("using ") || llmResponse.Contains("namespace ") || llmResponse.Contains("class "))
            return llmResponse.Trim();

        return "";
    }

    private static string? HeuristicFallback(ErrorSnapshot error, int attemptNum)
    {
        if (error.ExceptionType.Contains("NullReference", StringComparison.OrdinalIgnoreCase) ||
            error.ExceptionType.Contains("ArgumentNull", StringComparison.OrdinalIgnoreCase))
        {
            return $"// NULL_REFERENCE_HEURISTIC: Consider adding null check at line {error.LineNumber}\n";
        }

        if (error.ExceptionType.Contains("Build", StringComparison.OrdinalIgnoreCase) ||
            error.ExceptionType.Contains("CS", StringComparison.OrdinalIgnoreCase))
        {
            var errorCode = Regex.Match(error.ExceptionMessage, @"CS\d+");
            return errorCode.Success
                ? $"// BUILD_ERROR_{errorCode}: Review type compatibility, missing using, or syntax issue at line {error.LineNumber}\n"
                : $"// BUILD_ERROR: Check build output for specific error location at line {error.LineNumber}\n";
        }

        return $"// AUTO_FIX_ATTEMPT_{attemptNum + 1}: LLM unavailable, review error at line {error.LineNumber}\n";
    }

    private static string? ReadFileSnippet(string filePath, int errorLine)
    {
        try
        {
            var allLines = File.ReadAllLines(filePath);

            if (allLines.Length <= MaxSourceLines)
                return File.ReadAllText(filePath);

            var start = Math.Max(0, errorLine - ContextPadding - 1);
            var end = Math.Min(allLines.Length, errorLine + ContextPadding);
            var count = end - start;

            if (count > MaxSourceLines)
            {
                start = Math.Max(0, errorLine - MaxSourceLines / 2 - 1);
                end = Math.Min(allLines.Length, start + MaxSourceLines);
            }

            var sb = new StringBuilder();
            for (int i = start; i < end; i++)
            {
                sb.AppendLine($"{i + 1,5}: {allLines[i]}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex, "DebugLoop: Failed to read file snippet");
            return null;
        }
    }

    private static string[] ReadSourceContext(string filePath, int errorLine)
    {
        try
        {
            var allLines = File.ReadAllLines(filePath);
            var start = Math.Max(0, errorLine - ContextPadding - 1);
            var end = Math.Min(allLines.Length, errorLine + ContextPadding);
            return allLines[start..end];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex, "DebugLoop: Failed to read source context");
            return Array.Empty<string>();
        }
    }

    private static bool ApplyFix(string filePath, string newContent)
    {
        if (string.IsNullOrEmpty(newContent) || !File.Exists(filePath))
            return false;

        if (newContent.StartsWith("//") && !newContent.Contains("SEARCH:") && !newContent.Contains("diff"))
            return false;

        if (newContent.Contains("SEARCH:") && newContent.Contains("REPLACE:"))
            return ApplyPatch(filePath, newContent);

        var original = File.ReadAllText(filePath).TrimEnd();
        var fixedCode = newContent.TrimEnd();

        if (string.Equals(original, fixedCode, StringComparison.Ordinal))
            return false;

        try
        {
            File.WriteAllText(filePath, fixedCode);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DebugLoop: Failed to apply fix: {ex.Message}");
            return false;
        }
    }

    private static bool ApplyPatch(string filePath, string patchText)
    {
        try
        {
            var lines = File.ReadAllLines(filePath).ToList();
            var patches = new List<(string[] search, string[] replace)>();

            var searchMatch = Regex.Match(patchText, @"SEARCH:\s*<<<\s*\n(.*?)>>>", RegexOptions.Singleline);
            var replaceMatch = Regex.Match(patchText, @"REPLACE:\s*<<<\s*\n(.*?)>>>", RegexOptions.Singleline);

            if (!searchMatch.Success || !replaceMatch.Success)
                return false;

            var searchBlock = searchMatch.Groups[1].Value.Trim('\r', '\n').Split('\n')
                .Select(l => l.TrimEnd('\r')).ToArray();
            var replaceBlock = replaceMatch.Groups[1].Value.Trim('\r', '\n').Split('\n')
                .Select(l => l.TrimEnd('\r')).ToArray();

            if (searchBlock.Length == 0 || replaceBlock.Length == 0)
                return false;

            if (searchBlock.SequenceEqual(replaceBlock))
                return false;

            int matchIdx = -1;
            for (int i = 0; i <= lines.Count - searchBlock.Length; i++)
            {
                var match = true;
                for (int j = 0; j < searchBlock.Length; j++)
                {
                    if (lines[i + j].TrimEnd('\r') != searchBlock[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) { matchIdx = i; break; }
            }

            if (matchIdx < 0)
            {
                for (int i = 0; i <= lines.Count - searchBlock.Length; i++)
                {
                    var match = true;
                    for (int j = 0; j < searchBlock.Length; j++)
                    {
                        if (lines[i + j].TrimEnd('\r').Trim() != searchBlock[j].Trim())
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match) { matchIdx = i; break; }
                }
            }

            if (matchIdx < 0) return false;

            lines.RemoveRange(matchIdx, searchBlock.Length);
            for (int j = replaceBlock.Length - 1; j >= 0; j--)
                lines.Insert(matchIdx, replaceBlock[j].TrimEnd('\r'));

            var result = string.Join('\n', lines);
            File.WriteAllText(filePath, result);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DebugLoop: Failed to apply patch: {ex.Message}");
            return false;
        }
    }

    private void BackupFile(string filePath)
    {
        if (_backups.ContainsKey(filePath)) return;

        try
        {
            var content = GetHeadVersion(filePath) ?? (File.Exists(filePath) ? File.ReadAllText(filePath) : null);
            if (content != null)
                _backups[filePath] = content;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("DebugLoop: Backup failed for {File}: {Message}", filePath, ex.Message);
        }
    }

    private void Rollback(string filePath)
    {
        if (!_backups.TryGetValue(filePath, out var original))
        {
            try
            {
                original = GetHeadVersion(filePath);
                if (original == null) return;
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "DebugLoop: git checkout fallback failed"); return; }
        }

        try
        {
            File.WriteAllText(filePath, original);
            _backups.TryRemove(filePath, out _);
            _logger?.LogInformation("DebugLoop: Rolled back {File}", filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("DebugLoop: Rollback failed for {File}: {Message}", filePath, ex.Message);
        }
    }

    private static string? CaptureCurrentState(string filePath)
    {
        try
        {
            var repoPath = Repository.Discover(Directory.GetCurrentDirectory());
            if (string.IsNullOrEmpty(repoPath)) return null;
            using var repo = new Repository(repoPath);
            return repo.Head.Tip?.Sha;
        }
        catch (Exception) { return null; }
    }

    private string? GetHeadVersion(string filePath)
    {
        if (string.IsNullOrEmpty(_repoPath)) return null;
        try
        {
            using var repo = new Repository(_repoPath);
            var headCommit = repo.Head.Tip;
            if (headCommit == null) return null;
            var entry = headCommit[filePath];
            if (entry?.Target is Blob blob)
                return blob.GetContentText();
        }
        catch { }
        return null;
    }

    private void Rollback(FixAttempt fix)
    {
        if (!string.IsNullOrEmpty(fix.AppliedFile))
            Rollback(fix.AppliedFile);
    }

    private void CleanupBackups()
    {
        foreach (var key in _backups.Keys.ToList())
        {
            _backups.TryRemove(key, out _);
        }
    }

    public DebugSession? GetSession(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : null;

    public async Task<List<PreAnalysisIssue>> AnalyzeAsync(string filePath, CancellationToken ct = default)
    {
        var issues = new List<PreAnalysisIssue>();

        if (!File.Exists(filePath))
        {
            _logger?.LogWarning("DebugLoop: AnalyzeAsync — file not found: {File}", filePath);
            return issues;
        }

        try
        {
            var sourceText = ReadFileSnippet(filePath, 1);
            if (string.IsNullOrEmpty(sourceText)) return issues;

            var prompt = string.Format(PreAnalysisPrompt, sourceText);

            var options = new ChatOptions
            {
                Temperature = 0.1f,
                MaxOutputTokens = 4096
            };

            var response = await _chatClient.GetResponseAsync(prompt, options, ct).ConfigureAwait(false);
            var text = response.Text ?? "";

            if (text.Contains("NO_ISSUES")) return issues;

            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("ISSUE|"))
                {
                    var parts = line.Split('|', 5);
                    if (parts.Length >= 5)
                    {
                        issues.Add(new PreAnalysisIssue
                        {
                            FilePath = filePath,
                            Severity = parts[1].Trim(),
                            LineNumber = int.TryParse(parts[2].Trim(), out var ln) ? ln : 0,
                            Category = parts[3].Trim(),
                            Description = parts[4].Trim()
                        });
                    }
                }

                if (line.StartsWith("FIX|"))
                {
                    var parts = line.Split('|', 3);
                    if (parts.Length >= 3 && issues.Count > 0)
                    {
                        issues[^1].SuggestedFix = parts[2].Trim();
                    }
                }
            }

            _logger?.LogInformation("DebugLoop: Static analysis found {Count} potential issues in {File}",
                issues.Count, Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("DebugLoop: Static analysis failed for {File}: {Message}", filePath, ex.Message);
        }

        return issues;
    }

    public Dictionary<string, object> GetStats()
    {
        var instinctStats = FixInstinctStore.GetStats();
        var health = FixInstinctStore.GetHealthScore();
        var result = new Dictionary<string, object>
        {
            ["sessions"] = _sessions.Count,
            ["fixed"] = _sessions.Values.Count(s => s.Fixed),
            ["escalated"] = _sessions.Values.Count(s => s.Escalated),
            ["total_attempts"] = _sessions.Values.Sum(s => s.Attempts.Count),
            ["llm_driven_fixes"] = _sessions.Values.Sum(s => s.Attempts.Count(a => a.LlmTokens > 0)),
            ["avg_duration_ms"] = _sessions.Values.Count > 0
                ? _sessions.Values.Average(s => s.TotalDurationMs) : 0,
            ["harness_health_score"] = health,
            ["safety_posture"] = _harnessProfile?.SafetyPosture ?? "standard"
        };
        foreach (var kv in instinctStats)
            result[$"instinct_{kv.Key}"] = kv.Value;
        return result;
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_persistPath, JsonSerializer.Serialize(
                new { sessions = _sessions.Values.ToList() }, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("DebugLoop: Save failed: {Message}", ex.Message);
        }
    }

    private static int EstimateTokens(string text) =>
        Math.Max(1, text.Length / 4);

    private void RecordCorrection(ErrorSnapshot error, FixAttempt fix)
    {
        if (_correctionMemory == null) return;

        try
        {
            var query = $"{error.ExceptionType}: {error.ExceptionMessage}";
            var wrongOutput = error.SourceContext.Length > 0
                ? error.SourceContext
                : $"File: {error.FilePath}, Line: {error.LineNumber}";
            var fixedCode = fix.GeneratedPatch.Length > 2000
                ? fix.GeneratedPatch[..2000]
                : fix.GeneratedPatch;

            _correctionMemory.RecordFailure(
                query[..Math.Min(query.Length, 500)],
                wrongOutput[..Math.Min(wrongOutput.Length, 800)],
                0.1f,
                error.ExceptionType);

            _logger?.LogInformation("DebugLoop: Correction recorded for {ExceptionType}", error.ExceptionType);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("DebugLoop: Failed to record correction: {Message}", ex.Message);
        }
    }
}
