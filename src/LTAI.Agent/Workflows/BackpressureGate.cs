using System.Diagnostics;
using LTAI.AI.Interfaces;
using LTAI.Core.Governors;
using Microsoft.Extensions.Logging;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Workflows;

public sealed record GateResult
{
    public bool Passed { get; init; }
    public string GateName { get; init; } = "";
    public string Reason { get; init; } = "";
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public long ElapsedMs { get; init; }
    public string? RawOutput { get; init; }
    public List<string> Suggestions { get; init; } = new();
}

public sealed record BackpressureResult
{
    public bool AllPassed { get; init; }
    public int AttemptCount { get; init; }
    public List<GateResult> GateResults { get; init; } = new();
    public TimeSpan TotalTime { get; init; }
    public string? Summary { get; init; }

    public string RejectSummary()
    {
        var failed = GateResults.Where(g => !g.Passed).ToList();
        if (failed.Count == 0) return "";
        return string.Join("\n", failed.Select(g =>
            $"[{g.GateName}] FAILED ({g.ErrorCount} errors): {g.Reason}"));
    }
}

public interface IBackpressureGate
{
    string Name { get; }
    int Order { get; }
    Task<GateResult> CheckAsync(string worktreePath, CancellationToken ct = default);
    bool ShouldRun(BackpressureContext context);
}

public sealed record BackpressureContext
{
    public string WorktreePath { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string TaskDescription { get; init; } = "";
    public int AttemptNumber { get; init; }
    public List<GateResult> PreviousResults { get; init; } = new();
    public Dictionary<string, string> Metadata { get; init; } = new();
}

// ============================================================================
// Concrete Gates
// ============================================================================

public sealed class LintGate : IBackpressureGate
{
    public string Name => "lint";
    public int Order => 0;

    private readonly ILogger<LintGate> _logger;
    private readonly IMicroKernel? _kernel;
    private readonly IProjectSpecProvider? _projectSpec;

    public LintGate(IMicroKernel? kernel = null, ILogger<LintGate>? logger = null, IProjectSpecProvider? projectSpec = null)
    {
        _kernel = kernel;
        _logger = logger ?? NullLogger<LintGate>.Instance;
        _projectSpec = projectSpec;
    }

    public bool ShouldRun(BackpressureContext context) => true;

    public async Task<GateResult> CheckAsync(string worktreePath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var errors = 0;
        var warnings = 0;
        var rawOutput = "";
        var suggestions = new List<string>();

        try
        {
            var formatCmd = _projectSpec?.GetFormatCommand() ?? "dotnet format --verify-no-changes --verbosity quiet";
            var formatResult = await RunAsync(formatCmd, worktreePath, ct)
                .ConfigureAwait(false);

            rawOutput = formatResult.Output.Trim();
            if (!formatResult.Success)
            {
                errors++;
                suggestions.Add("Run 'dotnet format' to auto-fix formatting issues");
                suggestions.Add("Check for trailing whitespace and consistent indentation");
            }

            var lintCmd = _projectSpec?.GetLintCommand() ?? "dotnet build --no-restore --warnaserror";
            var buildResult = await RunAsync(lintCmd, worktreePath, ct)
                .ConfigureAwait(false);

            if (!buildResult.Success)
            {
                var errorLines = buildResult.Output.Split('\n')
                    .Where(l => l.Contains("error CS", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                errors += errorLines.Count;
                foreach (var line in errorLines.Take(5))
                    suggestions.Add($"Compile error: {line.Trim()}");

                var warnLines = buildResult.Output.Split('\n')
                    .Where(l => l.Contains("warning CS", StringComparison.OrdinalIgnoreCase));
                warnings += warnLines.Count();
            }

            rawOutput = string.Join("\n", rawOutput, buildResult.Output).Trim();
        }
        catch (Exception ex)
        {
            errors = 1;
            _logger.LogWarning(ex, "LintGate: tool execution failed");
            suggestions.Add($"Lint check error: {ex.Message}");
        }

        sw.Stop();

        return new GateResult
        {
            Passed = errors == 0,
            GateName = Name,
            Reason = errors == 0 ? "Lint/compile check passed" : $"Lint/compile check failed: {errors} error(s), {warnings} warning(s)",
            ErrorCount = errors,
            WarningCount = warnings,
            ElapsedMs = sw.ElapsedMilliseconds,
            RawOutput = rawOutput,
            Suggestions = suggestions
        };
    }

    private async Task<(bool Success, string Output)> RunAsync(string command, string workDir, CancellationToken ct)
    {
        var parts = command.Split(' ', 2);
        var fileName = parts[0];
        var args = parts.Length > 1 ? parts[1] : "";
        return await RunCmdAsync(fileName, args, workDir, ct).ConfigureAwait(false);
    }

    private async Task<(bool Success, string Output)> RunCmdAsync(string fileName, string args, string workDir, CancellationToken ct)
    {
        if (_kernel != null)
        {
            var result = await _kernel.ExecuteAsync(new KernelOp
            {
                Command = fileName,
                Arguments = args,
                WorkingDirectory = workDir,
                Timeout = TimeSpan.FromMinutes(2)
            }, ct).ConfigureAwait(false);
            return (result.Success, result.Data ?? result.Error ?? "");
        }

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            return (proc.ExitCode == 0, $"{stdout}\n{stderr}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

public sealed class TypecheckGate : IBackpressureGate
{
    public string Name => "typecheck";
    public int Order => 1;

    private readonly ILogger<TypecheckGate> _logger;
    private readonly IMicroKernel? _kernel;
    private readonly IProjectSpecProvider? _projectSpec;

    public TypecheckGate(IMicroKernel? kernel = null, ILogger<TypecheckGate>? logger = null, IProjectSpecProvider? projectSpec = null)
    {
        _kernel = kernel;
        _logger = logger ?? NullLogger<TypecheckGate>.Instance;
        _projectSpec = projectSpec;
    }

    public bool ShouldRun(BackpressureContext context) =>
        !context.Metadata.GetValueOrDefault("skip_typecheck", "false").Equals("true", StringComparison.OrdinalIgnoreCase);

    public async Task<GateResult> CheckAsync(string worktreePath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var errors = 0;
        var suggestions = new List<string>();
        var rawOutput = "";

        try
        {
            var typecheckCmd = _projectSpec?.GetBuildCommand() ?? "dotnet build --no-restore --warnaserror";
            var (success, output) = await RunAsync(typecheckCmd, worktreePath, ct)
                .ConfigureAwait(false);

            rawOutput = output.Trim();

            if (!success)
            {
                var errorLines = rawOutput.Split('\n')
                    .Where(l => l.Contains("error CS", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                errors = errorLines.Count;

                foreach (var line in errorLines.Take(5))
                {
                    var simplified = line.Replace(worktreePath + Path.DirectorySeparatorChar, "");
                    suggestions.Add($"Type error: {simplified.Trim()}");
                }

                if (errors > 5)
                    suggestions.Add($"... and {errors - 5} more error(s)");
            }
        }
        catch (Exception ex)
        {
            errors = 1;
            _logger.LogWarning(ex, "TypecheckGate: build failed");
            suggestions.Add($"Build error: {ex.Message}");
        }

        sw.Stop();

        return new GateResult
        {
            Passed = errors == 0,
            GateName = Name,
            Reason = errors == 0 ? "Type checking passed" : $"Type checking failed: {errors} error(s)",
            ErrorCount = errors,
            WarningCount = 0,
            ElapsedMs = sw.ElapsedMilliseconds,
            RawOutput = rawOutput,
            Suggestions = suggestions
        };
    }

    private async Task<(bool Success, string Output)> RunAsync(string command, string workDir, CancellationToken ct)
    {
        var parts = command.Split(' ', 2);
        var fileName = parts[0];
        var args = parts.Length > 1 ? parts[1] : "";
        return await RunTypecheckCmdAsync(fileName, args, workDir, ct).ConfigureAwait(false);
    }

    private async Task<(bool Success, string Output)> RunTypecheckCmdAsync(string fileName, string args, string workDir, CancellationToken ct)
    {
        if (_kernel != null)
        {
            var result = await _kernel.ExecuteAsync(new KernelOp
            {
                Command = fileName,
                Arguments = args,
                WorkingDirectory = workDir,
                Timeout = TimeSpan.FromMinutes(2)
            }, ct).ConfigureAwait(false);
            return (result.Success, result.Data ?? result.Error ?? "");
        }

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return (proc.ExitCode == 0, $"{stdout}\n{stderr}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

public sealed class TestGate : IBackpressureGate
{
    public string Name => "test";
    public int Order => 2;

    private readonly ILogger<TestGate> _logger;
    private readonly IMicroKernel? _kernel;
    private readonly IProjectSpecProvider? _projectSpec;

    public TestGate(IMicroKernel? kernel = null, ILogger<TestGate>? logger = null, IProjectSpecProvider? projectSpec = null)
    {
        _kernel = kernel;
        _logger = logger ?? NullLogger<TestGate>.Instance;
        _projectSpec = projectSpec;
    }

    public bool ShouldRun(BackpressureContext context)
    {
        var hasTests = Directory.Exists(Path.Combine(context.WorktreePath, "tests")) ||
                       Directory.Exists(Path.Combine(context.WorktreePath, "test"));
        return hasTests;
    }

    public async Task<GateResult> CheckAsync(string worktreePath, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var errors = 0;
        var suggestions = new List<string>();
        var rawOutput = "";

        try
        {
            var testCmd = _projectSpec?.GetTestCommand() ?? "dotnet test --no-build --nologo";
            var (success, output) = await RunAsync(testCmd, worktreePath, ct)
                .ConfigureAwait(false);

            rawOutput = output.Trim();

            if (!success)
            {
                var failedLines = rawOutput.Split('\n')
                    .Where(l => l.Contains("Failed ", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("FAILED", StringComparison.Ordinal))
                    .ToList();
                errors = failedLines.Count;

                foreach (var line in failedLines.Take(5))
                    suggestions.Add($"Test failure: {line.Trim()}");

                if (failedLines.Count == 0)
                {
                    errors = 1;
                    suggestions.Add("Some tests failed — check output for details");
                }
            }
        }
        catch (Exception ex)
        {
            errors = 1;
            _logger.LogWarning(ex, "TestGate: test execution failed");
            suggestions.Add($"Test error: {ex.Message}");
        }

        sw.Stop();

        return new GateResult
        {
            Passed = errors == 0,
            GateName = Name,
            Reason = errors == 0 ? "All tests passed" : $"Tests failed: {errors} failure(s)",
            ErrorCount = errors,
            WarningCount = 0,
            ElapsedMs = sw.ElapsedMilliseconds,
            RawOutput = rawOutput,
            Suggestions = suggestions
        };
    }

    private async Task<(bool Success, string Output)> RunAsync(string command, string workDir, CancellationToken ct)
    {
        var parts = command.Split(' ', 2);
        var fileName = parts[0];
        var args = parts.Length > 1 ? parts[1] : "";
        return await RunTestCmdAsync(fileName, args, workDir, ct).ConfigureAwait(false);
    }

    private async Task<(bool Success, string Output)> RunTestCmdAsync(string fileName, string args, string workDir, CancellationToken ct)
    {
        if (_kernel != null)
        {
            var result = await _kernel.ExecuteAsync(new KernelOp
            {
                Command = fileName,
                Arguments = args,
                WorkingDirectory = workDir,
                Timeout = TimeSpan.FromMinutes(3)
            }, ct).ConfigureAwait(false);
            return (result.Success, result.Data ?? result.Error ?? "");
        }

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    WorkingDirectory = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return (proc.ExitCode == 0, $"{stdout}\n{stderr}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

public sealed class ReviewGate : IBackpressureGate
{
    public string Name => "review";
    public int Order => 3;

    private readonly ILivingTreeSystem? _lts;
    private readonly ILogger<ReviewGate> _logger;

    public ReviewGate(ILivingTreeSystem? lts = null, ILogger<ReviewGate>? logger = null)
    {
        _lts = lts;
        _logger = logger ?? NullLogger<ReviewGate>.Instance;
    }

    public bool ShouldRun(BackpressureContext context) =>
        context.AttemptNumber == 1 && _lts != null;

    public async Task<GateResult> CheckAsync(string worktreePath, CancellationToken ct = default)
    {
        if (_lts == null)
        {
            return new GateResult
            {
                Passed = true,
                GateName = Name,
                Reason = "Review skipped: no review model available",
                ElapsedMs = 0
            };
        }

        var sw = Stopwatch.StartNew();
        var suggestions = new List<string>();
        var errors = 0;

        try
        {
            var diffOutput = "";
            var diffFile = Path.Combine(worktreePath, ".livingtree", "diff_output.txt");
            if (File.Exists(diffFile))
                diffOutput = await File.ReadAllTextAsync(diffFile, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(diffOutput))
            {
                return new GateResult
                {
                    Passed = true,
                    GateName = Name,
                    Reason = "Review skipped: no diff to review",
                    ElapsedMs = 0
                };
            }

            var reviewPrompt = "Review the following code changes. List any issues found (bugs, style, logic). If none found, say PASS. Be brief.\n\n" + diffOutput;

            var reviewResponse = await _lts.ChatAsync(reviewPrompt, new CancellationToken())
                .ConfigureAwait(false);

            var reviewText = reviewResponse ?? "";

            if (reviewText.Contains("PASS", StringComparison.OrdinalIgnoreCase) &&
                !reviewText.Contains("NO PASS", StringComparison.OrdinalIgnoreCase))
            {
                return new GateResult
                {
                    Passed = true,
                    GateName = Name,
                    Reason = "Code review passed",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }

            var issueLines = reviewText.Split('\n')
                .Where(l => l.TrimStart().StartsWith("-") || l.TrimStart().StartsWith("*"))
                .Take(5)
                .ToList();
            suggestions.AddRange(issueLines);

            if (suggestions.Count == 0 && reviewText.Length > 10)
            {
                suggestions.Add(reviewText.Trim());
            }

            errors = suggestions.Count > 0 ? suggestions.Count : 1;

            return new GateResult
            {
                Passed = false,
                GateName = Name,
                Reason = $"Code review found {errors} issue(s)",
                ErrorCount = errors,
                ElapsedMs = sw.ElapsedMilliseconds,
                Suggestions = suggestions
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReviewGate: review failed");
            return new GateResult
            {
                Passed = true,
                GateName = Name,
                Reason = $"Review failed (non-blocking): {ex.Message}",
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
    }
}
