using System.Text.Json;

namespace LTAI.Core.System;

public sealed class SessionSnapshot
{
    public string SessionId { get; set; } = "";
    public string UserId { get; set; } = "";
    public List<Dictionary<string, string>> Messages { get; set; } = new();
    public string LastIntent { get; set; } = "";
    public string LastModel { get; set; } = "";
    public double CreatedAt { get; set; }
    public double UpdatedAt { get; set; }
    public int TurnCount { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public sealed class SessionResilience
{
    private static readonly Lazy<SessionResilience> _instance = new(() => new SessionResilience());
    public static SessionResilience Instance => _instance.Value;

    private const int MaxCheckpoints = 50;
    private const int MaxSessionAgeHours = 24;
    private const int MaxMessagesPerSession = 50;

    private readonly Dictionary<string, SessionSnapshot> _active = new();
    private readonly string _checkpointDir;
    private readonly object _lock = new();

    private SessionResilience()
    {
        _checkpointDir = Path.Combine(".livingtree", "checkpoints");
        Directory.CreateDirectory(_checkpointDir);
        LoadAll();
    }

    private string FileFor(string sessionId) => Path.Combine(_checkpointDir, $"{sessionId}.json");

    private void LoadAll()
    {
        var files = Directory.GetFiles(_checkpointDir, "*.json").OrderBy(f => f);
        foreach (var f in files)
        {
            try
            {
                var json = File.ReadAllText(f);
                var snap = JsonSerializer.Deserialize<SessionSnapshot>(json);
                if (snap == null) continue;

                var ageH = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - snap.UpdatedAt) / 3600.0;
                if (ageH < MaxSessionAgeHours)
                    _active[snap.SessionId] = snap;
            }
            catch { /* non-fatal */ }
        }
    }

    public void Save(string sessionId, string userMsg, string assistantMsg,
        string intent = "", string model = "", Dictionary<string, object>? meta = null)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(sessionId, out var snap))
            {
                snap = new SessionSnapshot
                {
                    SessionId = sessionId,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
                _active[sessionId] = snap;
            }

            snap.Messages.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = userMsg });
            snap.Messages.Add(new Dictionary<string, string> { ["role"] = "assistant", ["content"] = assistantMsg });
            snap.LastIntent = intent;
            snap.LastModel = model;
            snap.TurnCount++;
            snap.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (meta != null)
            {
                foreach (var kvp in meta)
                    snap.Metadata[kvp.Key] = kvp.Value;
            }

            if (snap.Messages.Count > MaxMessagesPerSession)
                snap.Messages = snap.Messages.Skip(snap.Messages.Count - MaxMessagesPerSession).ToList();

            var json = JsonSerializer.Serialize(snap);
            File.WriteAllText(FileFor(sessionId), json);
        }

        PruneIfNeeded();
    }

    public SessionSnapshot? Restore(string sessionId)
    {
        lock (_lock)
        {
            return _active.GetValueOrDefault(sessionId);
        }
    }

    public List<Dictionary<string, object>> ListRecoverable()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = new List<Dictionary<string, object>>();

        lock (_lock)
        {
            foreach (var (sid, snap) in _active)
            {
                var ageM = (int)((now - snap.UpdatedAt) / 60);
                result.Add(new Dictionary<string, object>
                {
                    ["session_id"] = sid,
                    ["turns"] = snap.TurnCount,
                    ["idle_minutes"] = ageM,
                    ["last_intent"] = snap.LastIntent,
                    ["can_resume"] = ageM < MaxSessionAgeHours * 60
                });
            }
        }

        return result.OrderBy(x => (int)(x["idle_minutes"] ?? 0)).ToList();
    }

    private void PruneIfNeeded()
    {
        var files = Directory.GetFiles(_checkpointDir, "*.json")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        foreach (var f in files.Take(files.Count - MaxCheckpoints))
        {
            try
            {
                f.Delete();
                lock (_lock)
                {
                    _active.Remove(Path.GetFileNameWithoutExtension(f.Name));
                }
            }
            catch { /* non-fatal */ }
        }
    }

    public Dictionary<string, object> GetStats()
    {
        lock (_lock)
        {
            return new Dictionary<string, object>
            {
                ["active_sessions"] = _active.Count,
                ["recoverable"] = ListRecoverable().Count,
                ["max_age_hours"] = MaxSessionAgeHours,
                ["checkpoint_dir"] = _checkpointDir
            };
        }
    }
}
