using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace LTAI.Agent.Delegation;

/// <summary>
/// DeLM-inspired shared verified context (arXiv 2606.10662).
///
/// A decentralized task queue where agents claim subtasks, read accumulated
/// verified progress, and write back compact verified updates — all without
/// a central orchestrator.
///
/// Persisted as <c>.livingtree/delegation.jsonl</c> (append-only JSONL).
/// In-memory index for O(1) lookup.
/// </summary>
public sealed class DelegationContext : IDisposable
{
    // ── Task states ──
    public const string StatusPending = "pending";
    public const string StatusClaimed = "claimed";
    public const string StatusVerified = "verified";
    public const string StatusFailed = "failed";

    public sealed record DelegationTask(
        string Id,
        string Description,
        string RequiredSkills,
        string Status,
        string? ClaimedBy,
        string? ClaimedAt,
        string CreatedAt);

    public sealed record VerifiedUpdate(
        string Agent,
        string TaskId,
        string Content,
        string Timestamp);

    private readonly string _storePath;
    private readonly ConcurrentDictionary<string, DelegationTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<VerifiedUpdate>> _updates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public DelegationContext(string? storeDir = null)
    {
        storeDir ??= Path.Combine(AppContext.BaseDirectory, ".livingtree");
        Directory.CreateDirectory(storeDir);
        _storePath = Path.Combine(storeDir, "delegation.jsonl");
        LoadFromDisk();
    }

    // ═══════════════════════════════════════════
    //  Write path
    // ═══════════════════════════════════════════

    /// <summary>Enqueue a new task. Returns its ID.</summary>
    public string EnqueueTask(string description, string requiredSkills)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var task = new DelegationTask(id, description, requiredSkills,
            StatusPending, null, null, DateTime.UtcNow.ToString("O"));
        _tasks[id] = task;
        AppendToDisk("enqueue", task);
        return id;
    }

    /// <summary>
    /// Claim the next pending task matching the given skill keywords (OR match).
    /// Atomically marks it as claimed so no other agent gets it.
    /// Returns null if no matching task is available.
    /// </summary>
    public DelegationTask? ClaimNext(string agentName, string[] skills)
    {
        // Find first pending task whose RequiredSkills overlaps with agent's skills
        foreach (var (id, task) in _tasks)
        {
            if (task.Status != StatusPending) continue;
            var taskSkills = task.RequiredSkills.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!skills.Any(s => taskSkills.Any(ts => ts.Contains(s, StringComparison.OrdinalIgnoreCase))))
                continue;

            // Atomically claim
            var claimed = task with
            {
                Status = StatusClaimed,
                ClaimedBy = agentName,
                ClaimedAt = DateTime.UtcNow.ToString("O"),
            };
            if (!_tasks.TryUpdate(id, claimed, task))
                continue; // race — another agent claimed it first

            AppendToDisk("claim", claimed);
            return claimed;
        }
        return null;
    }

    /// <summary>Append a verified update to a claimed task.</summary>
    public VerifiedUpdate? WriteVerifiedUpdate(string taskId, string agentName, string content)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return null;
        if (task.Status != StatusClaimed)
            return null;

        var update = new VerifiedUpdate(agentName, taskId, content, DateTime.UtcNow.ToString("O"));
        _updates.AddOrUpdate(taskId,
            _ => [update],
            (_, list) => { lock (list) list.Add(update); return list; });

        // If this is the terminal update, mark task as verified
        if (content.Contains("<verified>"))
        {
            var verified = task with { Status = StatusVerified };
            _tasks.TryUpdate(taskId, verified, task);
            AppendToDisk("verify", verified);
        }

        AppendToDisk("update", update);
        return update;
    }

    /// <summary>Mark a claimed task as failed.</summary>
    public void FailTask(string taskId, string error)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;
        var failed = task with { Status = StatusFailed };
        _tasks.TryUpdate(taskId, failed, task);
        AppendToDisk("fail", new { task = failed, error });
    }

    // ═══════════════════════════════════════════
    //  Read path
    // ═══════════════════════════════════════════

    /// <summary>Read all verified updates for a task (shared context).</summary>
    public IReadOnlyList<VerifiedUpdate> ReadVerifiedContext(string taskId)
    {
        if (_updates.TryGetValue(taskId, out var list))
        {
            lock (list) return list.ToArray();
        }
        return [];
    }

    /// <summary>Format verified context as a compact string for agent consumption.</summary>
    public string FormatVerifiedContext(string taskId)
    {
        var updates = ReadVerifiedContext(taskId);
        if (updates.Count == 0) return "(no verified context yet)";

        var sb = new StringBuilder();
        sb.AppendLine("<delegation-context>");
        foreach (var u in updates)
            sb.AppendLine($"  <update agent=\"{u.Agent}\" ts=\"{u.Timestamp}\">{u.Content}</update>");
        sb.Append("</delegation-context>");
        return sb.ToString();
    }

    /// <summary>List all tasks, optionally filtered by status.</summary>
    public IReadOnlyList<DelegationTask> ListTasks(string? status = null)
    {
        return _tasks.Values
            .Where(t => status == null || t.Status == status)
            .OrderBy(t => t.CreatedAt)
            .ToArray();
    }

    /// <summary>List tasks claimed by a specific agent.</summary>
    public IReadOnlyList<DelegationTask> ListClaimedBy(string agentName)
    {
        return _tasks.Values
            .Where(t => string.Equals(t.ClaimedBy, agentName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.CreatedAt)
            .ToArray();
    }

    /// <summary>Get a single task by ID.</summary>
    public DelegationTask? GetTask(string taskId)
        => _tasks.TryGetValue(taskId, out var t) ? t : null;

    // ═══════════════════════════════════════════
    //  Persistence
    // ═══════════════════════════════════════════

    private void AppendToDisk(string op, object data)
    {
        lock (_writeLock)
        {
            try
            {
                var line = JsonSerializer.Serialize(new { op, data, ts = DateTime.UtcNow.ToString("O") }, JsonOpts);
                File.AppendAllText(_storePath, line + "\n");
            }
            catch
            {
                // non-critical, best-effort
            }
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var lines = File.ReadAllLines(_storePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var op = doc.RootElement.GetProperty("op").GetString();
                    var data = doc.RootElement.GetProperty("data");
                    switch (op)
                    {
                        case "enqueue":
                        case "claim":
                        case "verify":
                        case "fail":
                            var task = JsonSerializer.Deserialize<DelegationTask>(data.GetRawText(), JsonOpts);
                            if (task != null) _tasks[task.Id] = task;
                            break;
                        case "update":
                            var update = JsonSerializer.Deserialize<VerifiedUpdate>(data.GetRawText(), JsonOpts);
                            if (update != null)
                                _updates.AddOrUpdate(update.TaskId,
                                    _ => [update],
                                    (_, list) => { lock (list) list.Add(update); return list; });
                            break;
                    }
                }
                catch
                {
                    // skip malformed lines
                }
            }
        }
        catch
        {
            // non-critical, best-effort
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
