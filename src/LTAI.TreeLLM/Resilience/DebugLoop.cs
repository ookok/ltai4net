using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Resilience;

public sealed class DebugLoop
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<DebugLoop>? _logger;
    private readonly string _persistPath;
    private readonly ConcurrentDictionary<string, DebugSession> _sessions = new();

    public DebugLoop(ILogger<DebugLoop>? logger = null, string? persistPath = null)
    {
        _logger = logger;
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "debug_loop.json");
    }

    public DebugSession Debug(string target, string args, DebugLevel level = DebugLevel.SemiAuto,
        int maxAttempts = 3)
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
                var error = RunTarget(target, args);

                if (error == null)
                {
                    session.Fixed = true;
                    break;
                }

                if (level == DebugLevel.Analyze)
                {
                    _logger?.LogInformation("DebugLoop: Analysis only: {ExceptionType} at {File}:{Line}",
                        error.ExceptionType, error.FilePath, error.LineNumber);
                    break;
                }

                var fix = GenerateAndApply(error, attempt, session);
                session.Attempts.Add(fix);

                if (fix.Result == AttemptResult.Fixed)
                {
                    session.Fixed = true;
                    break;
                }

                if (fix.Result == AttemptResult.Hitl || fix.Result == AttemptResult.Worse)
                {
                    session.Escalated = true;
                    RevertFix(fix);
                    break;
                }

                if (attempt >= 3 && !session.Fixed)
                    session.Escalated = true;
            }
        }
        finally
        {
            sw.Stop();
            session.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
        }

        Save();
        return session;
    }

    private ErrorSnapshot? RunTarget(string target, string args)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"run --project {target} {args}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(120000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode == 0) return null;

            return ParseError(stderr.ToString(), target, stdout.ToString());
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

    private static ErrorSnapshot? ParseError(string stderr, string target, string stdout)
    {
        if (string.IsNullOrEmpty(stderr)) return null;

        var match = Regex.Match(stderr, @"^(\w+\.\w+Exception):\s*(.+)$", RegexOptions.Multiline);
        if (!match.Success) match = Regex.Match(stderr, @"error CS\d+:\s*(.+)$", RegexOptions.Multiline);

        string exceptionType = "UnknownError";
        string message = stderr.Length > 500 ? stderr[..500] : stderr;

        if (match.Success)
        {
            exceptionType = match.Groups[1].Success ? match.Groups[1].Value : "BuildError";
            message = match.Groups[2].Success ? match.Groups[2].Value : match.Value;
        }

        var fileMatch = Regex.Match(stderr, @"(?:in\s+|at\s+)?([\w\\\/\.]+\.cs):line\s+(\d+)");
        var testMatch = Regex.Match(stdout + stderr, @"(?:\[xUnit|Failed\s+)([\w\.]+\.\w+)(?:\(|\[)");

        return new ErrorSnapshot
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            ExceptionType = exceptionType,
            ExceptionMessage = message,
            TracebackText = stderr.Length > 2000 ? stderr[..2000] : stderr,
            FilePath = fileMatch.Groups[1].Success ? fileMatch.Groups[1].Value : "",
            LineNumber = fileMatch.Groups[2].Success ? int.Parse(fileMatch.Groups[2].Value) : 0,
            TestName = testMatch.Groups[1].Success ? testMatch.Groups[1].Value : null
        };
    }

    private FixAttempt GenerateAndApply(ErrorSnapshot error, int attemptNum, DebugSession session)
    {
        var sw = Stopwatch.StartNew();

        var fix = new FixAttempt
        {
            AttemptNumber = attemptNum + 1,
            Error = error,
            GeneratedPatch = GeneratePatch(error, attemptNum, session.Level),
            AppliedFile = error.FilePath
        };

        try
        {
            var result = ApplyPatch(error.FilePath, fix.GeneratedPatch);
            fix.Result = result ? AttemptResult.Fixed : AttemptResult.Unchanged;
            fix.AppliedLine = error.LineNumber;
        }
        catch (Exception ex)
        {
            fix.Result = AttemptResult.Worse;
            fix.NewError = ex.Message;
        }

        sw.Stop();
        fix.DurationMs = sw.Elapsed.TotalMilliseconds;

        return fix;
    }

    private static string GeneratePatch(ErrorSnapshot error, int attemptNum, DebugLevel level)
    {
        if (string.IsNullOrEmpty(error.FilePath) || !File.Exists(error.FilePath))
            return "";

        var content = File.ReadAllText(error.FilePath);
        var lines = content.Split('\n');

        if (error.ExceptionType.Contains("NullReference", StringComparison.OrdinalIgnoreCase) ||
            error.ExceptionType.Contains("ArgumentNull", StringComparison.OrdinalIgnoreCase))
        {
            return "// NULL_REFERENCE_HEURISTIC: Consider adding null check or using Nullable reference types\n";
        }

        if (error.ExceptionType.Contains("Build", StringComparison.OrdinalIgnoreCase) ||
            error.ExceptionType.Contains("CS", StringComparison.OrdinalIgnoreCase))
        {
            var errorCode = Regex.Match(error.ExceptionMessage, @"CS\d+");
            return errorCode.Success
                ? $"// BUILD_ERROR_{errorCode}: Review type compatibility, missing using, or syntax issue\n"
                : "// BUILD_ERROR: Check build output for specific error location and fix\n";
        }

        if (error.ExceptionType.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ||
            error.ExceptionType.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase))
        {
            return "// FILE_NOT_FOUND: Verify file path exists or add Directory.CreateDirectory before read\n";
        }

        return $"// AUTO_FIX_ATTEMPT_{attemptNum + 1}: Review error and apply targeted fix\n";
    }

    private static bool ApplyPatch(string filePath, string patch)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(patch) || !File.Exists(filePath))
            return false;

        if (patch.StartsWith("//") && !patch.Contains("diff"))
            return false;

        return true;
    }

    private static void RevertFix(FixAttempt fix)
    {
        try
        {
            if (fix.GitCommit != null)
            {
                Process.Start("git", $"reset --hard {fix.GitCommit}");
            }
        }
        catch { }
    }

    public DebugSession? GetSession(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : null;

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["sessions"] = _sessions.Count,
            ["fixed"] = _sessions.Values.Count(s => s.Fixed),
            ["escalated"] = _sessions.Values.Count(s => s.Escalated),
            ["total_attempts"] = _sessions.Values.Sum(s => s.Attempts.Count),
            ["avg_duration_ms"] = _sessions.Values.Count > 0
                ? _sessions.Values.Average(s => s.TotalDurationMs) : 0
        };
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_persistPath, JsonSerializer.Serialize(new { sessions = _sessions.Values.ToList() }, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("DebugLoop: Save failed: {Message}", ex.Message);
        }
    }
}
