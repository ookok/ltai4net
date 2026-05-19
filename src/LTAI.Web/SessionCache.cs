using System.Collections.Concurrent;

namespace LTAI.Web;

public sealed record CachedSession
{
    public string Id { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public List<string> Messages { get; init; } = new();
    public int MessagesCount => Messages.Count;
    public DateTime LastAccessed { get; set; }
    public bool IsComplete { get; set; }

    public CachedSession() { }

    public CachedSession(string id, DateTime createdAt)
    {
        Id = id;
        CreatedAt = createdAt;
        LastAccessed = createdAt;
        Messages = new List<string>();
    }
}

public sealed class SessionCache
{
    private static readonly Lazy<SessionCache> _instance = new(() => new SessionCache());
    public static SessionCache Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, CachedSession> _sessions = new();
    private const int MaxSessions = 1000;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(300);
    private readonly object _evictLock = new();

    private SessionCache() { }

    public CachedSession Create(string sessionId)
    {
        var session = new CachedSession(sessionId, DateTime.UtcNow);

        if (_sessions.Count >= MaxSessions)
        {
            lock (_evictLock)
            {
                if (_sessions.Count >= MaxSessions)
                {
                    var lru = _sessions.Values.OrderBy(s => s.LastAccessed).FirstOrDefault();
                    if (lru != null)
                        _sessions.TryRemove(lru.Id, out _);
                }
            }
        }

        _sessions.TryAdd(sessionId, session);
        return session;
    }

    public void Append(string sessionId, string message)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastAccessed = DateTime.UtcNow;
            lock (session.Messages)
            {
                session.Messages.Add(message);
            }
        }
    }

    public void Complete(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.IsComplete = true;
            session.LastAccessed = DateTime.UtcNow;
        }
    }

    public CachedSession? Get(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        if (DateTime.UtcNow - session.LastAccessed > Ttl)
        {
            _sessions.TryRemove(sessionId, out _);
            return null;
        }

        session.LastAccessed = DateTime.UtcNow;
        return session;
    }

    public int Cleanup()
    {
        var removed = 0;
        var cutoff = DateTime.UtcNow - Ttl;

        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastAccessed < cutoff)
            {
                if (_sessions.TryRemove(kvp.Key, out _))
                    removed++;
            }
        }

        return removed;
    }

    public Dictionary<string, int> GetStats()
    {
        Cleanup();
        var total = _sessions.Count;
        var active = _sessions.Values.Count(s => !s.IsComplete);
        var completed = _sessions.Values.Count(s => s.IsComplete);

        return new Dictionary<string, int>
        {
            ["total"] = total,
            ["active"] = active,
            ["completed"] = completed
        };
    }
}
