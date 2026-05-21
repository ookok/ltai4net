using Microsoft.Extensions.AI;

namespace LTAI.Core.Session;

public sealed class SessionTurn
{
    public string UserQuery { get; init; } = "";
    public string AssistantResponse { get; init; } = "";
    public string? Intent { get; init; }
    public string? ModelUsed { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class SessionState
{
    public string SessionId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public List<SessionTurn> Turns { get; init; } = new();
    public int MaxTurns { get; init; } = 200;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;

    private readonly Lock _turnsLock = new();

    public void AddTurn(string query, string response, string? intent = null, string? model = null)
    {
        lock (_turnsLock)
        {
            Turns.Add(new SessionTurn { UserQuery = query, AssistantResponse = response, Intent = intent, ModelUsed = model });
            LastActivity = DateTime.UtcNow;
            while (Turns.Count > MaxTurns)
                Turns.RemoveAt(0);
        }
    }

    public string GetCompressedHistory(int recentFull = 2, int summaryRange = 4)
    {
        if (Turns.Count == 0) return "";
        var parts = new List<string>();
        for (int i = 0; i < Turns.Count; i++)
        {
            var dist = Turns.Count - 1 - i;
            var t = Turns[i];
            if (dist < recentFull)
                parts.Add($"Q: {Truncate(t.UserQuery, 200)}\nA: {Truncate(t.AssistantResponse, 200)}");
            else if (dist < recentFull + summaryRange)
                parts.Add($"[summary] Q: {Truncate(t.UserQuery, 80)} A: {Truncate(t.AssistantResponse, 80)}");
        }
        return string.Join("\n", parts);
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..Math.Min(s.Length, max)] : s;
}

public interface ISessionStore
{
    SessionState GetOrCreate(string sessionId);
    SessionState? Get(string sessionId);
    void Save(SessionState session);
    void Delete(string sessionId);
    List<SessionState> ListActive(TimeSpan? maxAge = null);
    int Count { get; }
}

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, SessionState> _sessions = new();
    private readonly Lock _lock = new();
    private const int MaxSessions = 1000;

    public int Count { get { lock (_lock) return _sessions.Count; } }

    public SessionState GetOrCreate(string sessionId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var existing)) return existing;
            if (_sessions.Count >= MaxSessions) EvictOldest();
            var session = new SessionState { SessionId = sessionId };
            _sessions[sessionId] = session;
            return session;
        }
    }

    public SessionState? Get(string sessionId)
    {
        lock (_lock) { return _sessions.GetValueOrDefault(sessionId); }
    }

    public void Save(SessionState session)
    {
        lock (_lock) { _sessions[session.SessionId] = session; }
    }

    public void Delete(string sessionId)
    {
        lock (_lock) { _sessions.Remove(sessionId); }
    }

    public List<SessionState> ListActive(TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? TimeSpan.FromHours(24));
        lock (_lock) { return _sessions.Values.Where(s => s.LastActivity > cutoff).ToList(); }
    }

    private void EvictOldest()
    {
        var oldest = _sessions.Values.OrderBy(s => s.LastActivity).FirstOrDefault();
        if (oldest != null) _sessions.Remove(oldest.SessionId);
    }
}
