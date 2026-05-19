using System.Collections.Concurrent;
using System.Text.Json;
using LTAI.Execution.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Execution.Planning;

public sealed class TaskCheckpoint
{
    private const string DefaultBasePath = "data/checkpoints";

    private readonly string _basePath;
    private readonly ConcurrentDictionary<string, CheckpointState> _cache = new();
    private readonly ILogger<TaskCheckpoint> _logger;

    private static readonly Lazy<TaskCheckpoint> _instance = new(() =>
        new TaskCheckpoint(DefaultBasePath, NullLogger<TaskCheckpoint>.Instance));

    public static TaskCheckpoint Instance => _instance.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TaskCheckpoint(string basePath, ILogger<TaskCheckpoint> logger)
    {
        _basePath = basePath;
        _logger = logger;
    }

    public async Task SaveAsync(string sessionId, CheckpointState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        state.SessionId = sessionId;
        state.SavedAt = DateTime.UtcNow;
        state.Version++;

        Directory.CreateDirectory(_basePath);

        var filePath = GetFilePath(sessionId);
        var tempPath = filePath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);

            _cache[sessionId] = state;
            _logger.LogDebug("Checkpoint saved | session={SessionId} version={Version}", sessionId, state.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save checkpoint | session={SessionId}", sessionId);
            try { File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
    }

    public async Task<CheckpointState?> LoadAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (_cache.TryGetValue(sessionId, out var cached))
            return cached;

        var filePath = GetFilePath(sessionId);
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var state = JsonSerializer.Deserialize<CheckpointState>(json, JsonOptions);
            if (state is not null)
                _cache[sessionId] = state;
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load checkpoint | session={SessionId}", sessionId);
            return null;
        }
    }

    public async Task<CheckpointState?> ResumeAsync(string sessionId)
    {
        var state = await LoadAsync(sessionId);
        if (state is null)
            return null;

        return GetRemainingPlan(state);
    }

    public bool Delete(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        _cache.TryRemove(sessionId, out _);

        var filePath = GetFilePath(sessionId);
        if (!File.Exists(filePath))
            return false;

        try
        {
            File.Delete(filePath);
            _logger.LogDebug("Checkpoint deleted | session={SessionId}", sessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete checkpoint | session={SessionId}", sessionId);
            return false;
        }
    }

    public List<(string id, DateTime savedAt)> ListSessions()
    {
        var results = new List<(string id, DateTime savedAt)>();

        if (!Directory.Exists(_basePath))
            return results;

        foreach (var filePath in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var writeTime = File.GetLastWriteTimeUtc(filePath);
            results.Add((fileName, writeTime));
        }

        results.Sort((a, b) => b.savedAt.CompareTo(a.savedAt));
        return results;
    }

    public void CleanupOld(int maxAgeHours = 72)
    {
        if (!Directory.Exists(_basePath))
            return;

        var cutoff = DateTime.UtcNow.AddHours(-maxAgeHours);
        var removed = 0;

        foreach (var filePath in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            var writeTime = File.GetLastWriteTimeUtc(filePath);
            if (writeTime >= cutoff)
                continue;

            try
            {
                var sessionId = Path.GetFileNameWithoutExtension(filePath);
                _cache.TryRemove(sessionId, out _);
                File.Delete(filePath);
                removed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old checkpoint | file={FilePath}", filePath);
            }
        }

        if (removed > 0)
            _logger.LogInformation("Cleaned up {Count} checkpoint(s) older than {Hours}h", removed, maxAgeHours);
    }

    private static CheckpointState GetRemainingPlan(CheckpointState state)
    {
        var completedSet = new HashSet<string>(state.CompletedSteps ?? new());
        var remainingPlan = (state.Plan ?? new())
            .Where(p => !completedSet.Contains(p))
            .ToList();

        var remainingSubtasks = new Dictionary<string, object?>();
        if (state.ExecutionResults.TryGetValue("Subtasks", out var subtasksObj) && subtasksObj is JsonElement element)
        {
            var filtered = FilterPendingSubtasks(element);
            if (filtered is not null)
                remainingSubtasks["Subtasks"] = filtered;
        }
        else if (state.ExecutionResults.TryGetValue("subtasks", out var subtasksObjLower) && subtasksObjLower is JsonElement elementLower)
        {
            var filtered = FilterPendingSubtasks(elementLower);
            if (filtered is not null)
                remainingSubtasks["Subtasks"] = filtered;
        }

        var remainingResults = new Dictionary<string, object?>(state.ExecutionResults ?? new(), StringComparer.OrdinalIgnoreCase);
        remainingResults.Remove("Subtasks");
        remainingResults.Remove("subtasks");
        foreach (var kv in remainingSubtasks)
            remainingResults[kv.Key] = kv.Value;

        return new CheckpointState
        {
            SessionId = state.SessionId,
            TaskGoal = state.TaskGoal,
            Plan = remainingPlan,
            CompletedSteps = new(),
            CurrentStep = remainingPlan.FirstOrDefault(),
            ExecutionResults = remainingResults,
            Reflections = state.Reflections ?? new(),
            SuccessRate = state.SuccessRate,
            SavedAt = state.SavedAt,
            Version = state.Version
        };
    }

    private static JsonElement? FilterPendingSubtasks(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return null;

        var pending = new List<JsonElement>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var isDone = false;
            if (item.TryGetProperty("status", out var statusProp))
                isDone = statusProp.GetString() is "completed" or "done";
            else if (item.TryGetProperty("Status", out var statusPropUpper))
                isDone = statusPropUpper.GetString() is "completed" or "done";

            if (!isDone)
                pending.Add(item);
        }

        if (pending.Count == 0)
            return null;

        var json = JsonSerializer.Serialize(pending, JsonOptions);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private string GetFilePath(string sessionId)
        => Path.Combine(_basePath, $"{sessionId}.json");
}
