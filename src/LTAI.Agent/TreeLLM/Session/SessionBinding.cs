using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LTAI.Agent.Models;

namespace LTAI.Agent.Session;

public sealed class SessionBinding
{
    private const double COST_SAVING_THRESHOLD = 0.50;
    private const double STICKINESS_BONUS = 0.15;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly object _lock = new();
    private readonly ILogger<SessionBinding>? _logger;
    private int _saveCounter;
    private readonly string _persistPath;

    public SessionBinding(ILogger<SessionBinding>? logger = null, string? persistPath = null)
    {
        _logger = logger;
        _persistPath = persistPath ?? Path.Combine("livingtree", "meta", "session_binding.json");
        Load();
    }

    public SessionState GetSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, _ => new SessionState
        {
            SessionId = sessionId,
            BoundSince = DateTime.UtcNow,
            TurnCount = 0,
            ConsecutiveTurns = 0,
            SwitchCount = 0,
            LastTaskType = "general"
        });
    }

    public void Bind(string sessionId, string model, string taskType)
    {
        var session = GetSession(sessionId);

        if (!string.IsNullOrEmpty(session.BoundModel) && session.BoundModel != model)
        {
            session.SwitchHistory.Add($"{session.BoundModel}->{model}");
            session.SwitchCount++;
            session.ConsecutiveTurns = 0;
        }
        else if (session.BoundModel == model)
        {
            session.ConsecutiveTurns++;
        }
        else
        {
            session.ConsecutiveTurns = 1;
        }

        session.BoundModel = model;
        session.BoundSince = DateTime.UtcNow;
        session.LastTaskType = taskType;
        session.TurnCount++;

        _logger?.LogDebug("Session {SessionId} bound to {Model} ({TaskType}), turn {Turn}, consecutive {Consecutive}",
            sessionId, model, taskType, session.TurnCount, session.ConsecutiveTurns);

        var counter = Interlocked.Increment(ref _saveCounter);
        if (counter % 10 == 0)
            Save();
    }

    public (bool should, string message) ShouldSwitch(
        string sessionId, string candidate, string reason)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return (false, "No session state found");

        if (!string.IsNullOrEmpty(session.UserPreference))
            return (false, $"User locked to {session.UserPreference}");

        if (!string.IsNullOrEmpty(session.BoundModel) && session.BoundModel == candidate)
            return (false, "Already bound to candidate");

        var reasonLower = reason?.ToLowerInvariant() ?? "";
        var wasRateLimited = reasonLower.Contains("rate_limit") || reasonLower.Contains("429");
        var wasDead = reasonLower.Contains("dead") || reasonLower.Contains("failed") || reasonLower.Contains("down");
        var isTaskShift = reasonLower.Contains("task_shift");
        var isUserRequest = reasonLower.Contains("user") || reasonLower.Contains("request");
        var isCostSave = reasonLower.Contains("cost") || reasonLower.Contains("cheaper");

        if (wasRateLimited)
            return (true, "Rate limited, switching to alternative");

        if (wasDead)
            return (true, "Current model failed/dead, switching");

        if (isUserRequest)
            return (true, "User-requested switch");

        if (isTaskShift)
            return (true, "Task type changed, re-evaluating binding");

        if (isCostSave && !string.IsNullOrEmpty(session.BoundModel))
            return (true, "Cost saving opportunity detected");

        return (false, "No valid switch condition met");
    }

    public double StickinessScore(string sessionId, string candidate)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return 0.0;

        if (string.IsNullOrEmpty(session.BoundModel))
            return 0.0;

        var baseScore = session.BoundModel == candidate ? STICKINESS_BONUS : 0.0;

        if (session.ConsecutiveTurns > 3)
            baseScore += 0.05 * Math.Min(session.ConsecutiveTurns - 3, 5);

        return Math.Min(baseScore, 0.5);
    }

    public string TransitionContext(string sessionId, string fromModel, string toModel)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return string.Empty;

        var parts = new List<string>
        {
            $"[模型切换: {fromModel} -> {toModel}]",
            $"任务: {session.LastTaskType}",
            $"第{session.TurnCount}轮对话"
        };

        if (session.SwitchCount > 0)
            parts.Add($"此前已切换{session.SwitchCount}次");

        return string.Join("; ", parts);
    }

    public void SetPreference(string sessionId, string model)
    {
        var session = GetSession(sessionId);
        session.UserPreference = model;
        _logger?.LogInformation("Session {SessionId} user preference set to {Model}", sessionId, model);
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_persistPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var data = _sessions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var json = JsonSerializer.Serialize(data, JsonOptions);
                File.WriteAllText(_persistPath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to save session binding state");
            }
        }
    }

    public void Load()
    {
        if (!File.Exists(_persistPath))
            return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, SessionState>>(json, JsonOptions);
            if (data == null) return;

            foreach (var (key, state) in data)
                _sessions[key] = state;

            _logger?.LogInformation("Loaded {Count} session bindings from {Path}",
                _sessions.Count, _persistPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load session binding state");
        }
    }
}
