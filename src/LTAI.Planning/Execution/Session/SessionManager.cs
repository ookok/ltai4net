using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTAI.Planning.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Planning.Session;

public sealed class SessionManager
{
    private static readonly Lazy<SessionManager> _instance = new(() => new SessionManager());
    public static SessionManager Instance => _instance.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _basePath = ".livingtree/sessions";
    private readonly ConcurrentDictionary<string, SessionState> _cache = new();
    private ILogger<SessionManager>? _logger;

    private SessionManager() { }

    public static void SetLogger(ILogger<SessionManager> logger)
    {
        Instance._logger = logger;
    }

    public async Task SaveAsync(SessionState state)
    {
        state.UpdatedAt = DateTime.UtcNow;

        _cache[state.SessionId] = state;

        var dir = Path.GetDirectoryName(Path.GetFullPath(_basePath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var filePath = Path.Combine(_basePath, $"{state.SessionId}.json");
        var json = JsonSerializer.Serialize(state, JsonOptions);

        await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);

        _logger?.LogDebug("Session {SessionId} saved ({Name})", state.SessionId, state.Name);
    }

    public async Task<SessionState?> LoadAsync(string sessionId)
    {
        if (_cache.TryGetValue(sessionId, out var cached))
            return cached;

        var filePath = Path.Combine(_basePath, $"{sessionId}.json");
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
            if (state != null)
                _cache[sessionId] = state;

            return state;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load session {SessionId}", sessionId);
            return null;
        }
    }

    public Dictionary<string, SessionState> ListSessions(bool includeArchived = false)
    {
        var result = new Dictionary<string, SessionState>();

        if (!Directory.Exists(_basePath))
            return result;

        foreach (var file in Directory.GetFiles(_basePath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
                if (state == null) continue;
                if (!string.IsNullOrEmpty(state.SessionId))
                    result[state.SessionId] = state;
            }
            catch { /* non-fatal */ }
        }

        var filtered = includeArchived
            ? result
            : result.Where(kvp => !kvp.Value.Archived)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return filtered
            .OrderByDescending(kvp => kvp.Value.UpdatedAt)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public void Delete(string sessionId)
    {
        _cache.TryRemove(sessionId, out _);

        var filePath = Path.Combine(_basePath, $"{sessionId}.json");
        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
                _logger?.LogDebug("Session {SessionId} deleted", sessionId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete session {SessionId}", sessionId);
            }
        }
    }

    public void Archive(string sessionId)
    {
        var filePath = Path.Combine(_basePath, $"{sessionId}.json");
        if (!File.Exists(filePath))
        {
            if (_cache.TryGetValue(sessionId, out var cachedState))
            {
                cachedState.Archived = true;
                _ = SaveAsync(cachedState);
            }
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
            if (state == null) return;

            state.Archived = true;
            _ = SaveAsync(state);
            _logger?.LogInformation("Session {SessionId} archived", sessionId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to archive session {SessionId}", sessionId);
        }
    }

    public async Task<SessionState?> ResumeLatestAsync()
    {
        var sessions = ListSessions(includeArchived: false);
        if (sessions.Count == 0)
            return null;

        var latestId = sessions.First().Key;
        return await LoadAsync(latestId).ConfigureAwait(false);
    }

    public void CleanupOld(int maxAgeDays = 90)
    {
        if (!Directory.Exists(_basePath))
            return;

        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        var removed = 0;

        foreach (var file in Directory.GetFiles(_basePath, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
                if (state != null && state.UpdatedAt < cutoff)
                {
                    File.Delete(file);
                    _cache.TryRemove(state.SessionId, out _);
                    removed++;
                }
            }
            catch { /* non-fatal */ }
        }

        _logger?.LogInformation("Cleaned up {Count} sessions older than {Days} days", removed, maxAgeDays);
    }

    public Dictionary<string, object?> GetStats()
    {
        var sessions = ListSessions(includeArchived: true);
        var sessionCount = sessions.Count;
        var archivedCount = sessions.Values.Count(s => s.Archived);
        var totalTokens = sessions.Values.Sum(s => s.TotalTokens);
        var avgTokens = sessionCount > 0 ? (double)totalTokens / sessionCount : 0;

        return new Dictionary<string, object?>
        {
            ["session_count"] = sessionCount,
            ["archived_count"] = archivedCount,
            ["total_tokens"] = totalTokens,
            ["avg_tokens"] = avgTokens
        };
    }
}
