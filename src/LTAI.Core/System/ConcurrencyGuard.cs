using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public record BackgroundTask(
    string Name,
    DateTime CreatedAt,
    string Status,
    string? Exception,
    Task? TaskRef);

public sealed class ConcurrencyGuard
{
    private static readonly Lazy<ConcurrencyGuard> _instance = new(() => new ConcurrencyGuard());
    public static ConcurrencyGuard Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new();
    private readonly object _lock = new();
    private volatile CancellationTokenSource? _globalCts;
    private readonly ILogger<ConcurrencyGuard> _logger;

    public IReadOnlyDictionary<string, BackgroundTask> Tasks => _tasks;

    public ConcurrencyGuard() : this(NullLogger<ConcurrencyGuard>.Instance) { }

    public ConcurrencyGuard(ILogger<ConcurrencyGuard> logger)
    {
        _logger = logger ?? NullLogger<ConcurrencyGuard>.Instance;
    }

    public BackgroundTask Spawn(string name, Func<Task> coroutineFn)
    {
        var resolvedName = _resolveNameCollision(name);
        var bt = new BackgroundTask(resolvedName, DateTime.UtcNow, "pending", null, null);
        _tasks[resolvedName] = bt;

        _logger.LogInformation("Spawning task: {Name}", resolvedName);

        var task = _runWithTracking(bt, coroutineFn);
        var updated = bt with { TaskRef = task, Status = "running" };
        _tasks[resolvedName] = updated;

        return updated;
    }

    public BackgroundTask Spawn(string name, Action action)
    {
        return Spawn(name, () =>
        {
            action();
            return Task.CompletedTask;
        });
    }

    public List<BackgroundTask> SpawnMany(string namePrefix, IEnumerable<Func<Task>> coroutineFns)
    {
        var results = new List<BackgroundTask>();
        int index = 0;
        foreach (var fn in coroutineFns)
        {
            results.Add(Spawn($"{namePrefix}_{index}", fn));
            index++;
        }

        return results;
    }

    public List<BackgroundTask> SpawnMany(string namePrefix, IEnumerable<Action> actions)
    {
        var results = new List<BackgroundTask>();
        int index = 0;
        foreach (var action in actions)
        {
            results.Add(Spawn($"{namePrefix}_{index}", action));
            index++;
        }

        return results;
    }

    public void CancelAll()
    {
        _globalCts?.Cancel();
        _globalCts?.Dispose();
        _globalCts = new CancellationTokenSource();

        _logger.LogInformation("Cancelled all tasks");
    }

    public List<BackgroundTask> ListTasks()
    {
        return _tasks.Values.ToList();
    }

    public BackgroundTask? GetTask(string name)
    {
        return _tasks.GetValueOrDefault(name);
    }

    private async Task _runWithTracking(BackgroundTask bt, Func<Task> fn)
    {
        try
        {
            await fn();

            if (_tasks.TryGetValue(bt.Name, out var current))
            {
                var updated = current with { Status = "done" };
                _tasks[bt.Name] = updated;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {Name} failed", bt.Name);

            if (_tasks.TryGetValue(bt.Name, out var current))
            {
                var updated = current with { Status = "failed", Exception = ex.ToString() };
                _tasks[bt.Name] = updated;
            }
        }
        finally
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                _tasks.TryRemove(bt.Name, out _);
            });
        }
    }

    private string _resolveNameCollision(string name)
    {
        lock (_lock)
        {
            if (!_tasks.ContainsKey(name))
                return name;

            int suffix = 1;
            while (_tasks.ContainsKey($"{name}_{suffix}"))
                suffix++;

            return $"{name}_{suffix}";
        }
    }

    public Dictionary<string, object> Stats()
    {
        var tasks = _tasks.Values.ToList();
        return new Dictionary<string, object>
        {
            ["total"] = tasks.Count,
            ["pending"] = tasks.Count(t => t.Status == "pending"),
            ["running"] = tasks.Count(t => t.Status == "running"),
            ["cancelled"] = tasks.Count(t => t.Status == "cancelled"),
            ["done"] = tasks.Count(t => t.Status == "done"),
            ["failed"] = tasks.Count(t => t.Status == "failed")
        };
    }
}
