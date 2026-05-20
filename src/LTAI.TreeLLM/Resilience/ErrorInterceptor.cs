using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LTAI.TreeLLM.Models;

namespace LTAI.TreeLLM.Resilience;

public sealed class ErrorInterceptor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentBag<InterceptedError> _errors = new();
    private readonly ILogger<ErrorInterceptor>? _logger;
    private readonly string _logPath;
    private readonly string _jsonlPath;
    private bool _installed;

    public ErrorInterceptor(ILogger<ErrorInterceptor>? logger = null, string? dataDir = null)
    {
        _logger = logger;
        var dir = dataDir ?? Path.Combine("livingtree", "meta");
        _logPath = Path.Combine(dir, "errors.log");
        _jsonlPath = Path.Combine(dir, "errors.jsonl");
    }

    public void Install()
    {
        if (_installed) return;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _installed = true;
        _logger?.LogInformation("ErrorInterceptor: Global error hooks installed");
    }

    public void Uninstall()
    {
        if (!_installed) return;

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        _installed = false;
    }

    public InterceptedError Capture(Exception exception, string? context = null)
    {
        var error = new InterceptedError
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            ExceptionMessage = exception.Message,
            TracebackText = exception.StackTrace ?? "",
            ThreadName = System.Threading.Thread.CurrentThread.Name,
            TaskName = Task.CurrentId?.ToString(),
            MemoryKb = Environment.WorkingSet / 1024
        };

        ParseSourceLocation(error, exception);

        _errors.Add(error);
        WriteLog(error, context);

        _logger?.LogError("ErrorInterceptor: {ExceptionType}: {Message}",
            error.ExceptionType, error.ExceptionMessage);

        return error;
    }

    public List<InterceptedError> TopErrors(int count = 10)
    {
        return _errors
            .GroupBy(e => e.ExceptionType)
            .OrderByDescending(g => g.Count())
            .Take(count)
            .SelectMany(g => g.Take(3))
            .ToList();
    }

    public void Clear()
    {
        while (_errors.TryTake(out _)) { }
    }

    public List<Lesson> GetLessons()
    {
        var lessons = new List<Lesson>();
        var patterns = _errors
            .GroupBy(e => ExtractPattern(e))
            .Where(g => g.Count() >= 2);

        foreach (var group in patterns)
        {
            lessons.Add(new Lesson
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Pattern = group.Key,
                Fix = GenerateFix(group.First()),
                Occurrences = group.Count(),
                AutoFixConfidence = Math.Min(0.95, 0.5 + group.Count() * 0.05)
            });
        }

        return lessons;
    }

    public Dictionary<string, object> AutoHealCycle()
    {
        var lessons = GetLessons();
        int healed = 0;

        foreach (var lesson in lessons.Where(l => l.AutoFixConfidence >= 0.7))
        {
            if (TryApplyLesson(lesson))
            {
                healed++;
            }
        }

        return new Dictionary<string, object>
        {
            ["lessons_found"] = lessons.Count,
            ["healed"] = healed,
            ["high_confidence"] = lessons.Count(l => l.AutoFixConfidence >= 0.7)
        };
    }

    private bool TryApplyLesson(Lesson lesson)
    {
        try
        {
            if (lesson.Pattern.Contains("NullReference") || lesson.Pattern.Contains("ArgumentNull"))
            {
                _logger?.LogInformation("ErrorInterceptor: Auto-heal not implemented for {Pattern}", lesson.Pattern);
                return false;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractPattern(InterceptedError error)
    {
        var type = error.ExceptionType;
        if (type.Length > 50) type = type[^Math.Min(50, type.Length)..];
        return $"{type}|{error.FilePath}|{error.FunctionName}";
    }

    private static string GenerateFix(InterceptedError error)
    {
        if (error.ExceptionType.Contains("NullReference") ||
            error.ExceptionType.Contains("ArgumentNull"))
            return "Add null check before accessing member";

        if (error.ExceptionType.Contains("KeyNotFound"))
            return "Use TryGetValue or ContainsKey before dictionary access";

        if (error.ExceptionType.Contains("ArgumentOutOfRange"))
            return "Add bounds check before index access";

        if (error.ExceptionType.Contains("InvalidOperation"))
            return "Check collection state before enumeration";

        return "Review error context and apply appropriate fix";
    }

    private static void ParseSourceLocation(InterceptedError error, Exception exception)
    {
        if (string.IsNullOrEmpty(exception.StackTrace)) return;

        var lines = exception.StackTrace.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return;

        var match = System.Text.RegularExpressions.Regex.Match(lines[0],
            @"at\s+.*\s+in\s+(.+):line\s+(\d+)");
        if (match.Success)
        {
            error.FilePath = match.Groups[1].Value;
            error.LineNumber = int.Parse(match.Groups[2].Value);

            var funcMatch = System.Text.RegularExpressions.Regex.Match(lines[0],
                @"at\s+(.+)\(");
            if (funcMatch.Success)
                error.FunctionName = funcMatch.Groups[1].Value;
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            var error = Capture(ex, "unhandled");
            _logger?.LogCritical("ErrorInterceptor: Unhandled exception: {Type}", error.ExceptionType);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var error = Capture(e.Exception, "unobserved_task");
        e.SetObserved();
        _logger?.LogWarning("ErrorInterceptor: Unobserved task exception: {Type}", error.ExceptionType);
    }

    private void WriteLog(InterceptedError error, string? context)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var logLine = $"[{DateTime.UtcNow:O}] {context ?? "captured"} | {error.ExceptionType}: {error.ExceptionMessage}";
            File.AppendAllText(_logPath, logLine + Environment.NewLine);

            var jsonl = JsonSerializer.Serialize(error, JsonOptions);
            File.AppendAllText(_jsonlPath, jsonl + Environment.NewLine);
        }
        catch { /* non-fatal */ }
    }

    public Dictionary<string, object> GetStats()
    {
        return new Dictionary<string, object>
        {
            ["errors_captured"] = _errors.Count,
            ["installed"] = _installed,
            ["by_type"] = _errors.GroupBy(e => e.ExceptionType)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }
}
