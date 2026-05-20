using System.Collections.Concurrent;
using LTAI.Execution.Models;

namespace LTAI.Execution;

public class RecoveryAction
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Strategy { get; set; } = "";
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
    public string Status { get; set; } = "pending";
    public object? Result { get; set; }

    public void MarkCompleted(object result)
    {
        Status = "completed";
        Result = result;
        Timestamp = DateTime.UtcNow.ToString("O");
    }

    public void MarkFailed(string error)
    {
        Status = "failed";
        Result = error;
        Timestamp = DateTime.UtcNow.ToString("O");
    }
}

public class SelfHealer
{
    private readonly float _checkInterval;
    private readonly Dictionary<string, Func<CancellationToken, Task<bool>>> _checkFns = new();
    private readonly Dictionary<string, List<Func<CancellationToken, Task<bool>>>> _recoveryStrategies = new();
    private readonly Dictionary<string, int> _maxFailures = new();
    private readonly Dictionary<string, HealthCheck> _healthStatus = new();
    private readonly ConcurrentDictionary<string, RecoveryAction> _activeActions = new();

    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private readonly Lock _stateLock = new();

    public SelfHealer(float checkInterval = 60)
    {
        _checkInterval = checkInterval;
    }

    public void RegisterCheck(
        string name,
        Func<CancellationToken, Task<bool>> checkFn,
        List<Func<CancellationToken, Task<bool>>>? recoveryStrategies = null,
        int maxFailures = 3)
    {
        _checkFns[name] = checkFn;
        _recoveryStrategies[name] = recoveryStrategies ?? new();
        _maxFailures[name] = maxFailures;

        lock (_stateLock)
        {
            _healthStatus[name] = new HealthCheck(
                Name: name,
                Status: "healthy",
                LastCheck: DateTime.UtcNow.ToString("O"),
                ConsecutiveFailures: 0,
                MaxFailures: maxFailures,
                Metadata: new() { ["registered_at"] = DateTime.UtcNow.ToString("O") });
        }
    }

    public Task Start()
    {
        if (_monitorTask is not null)
            return Task.CompletedTask;

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task Stop()
    {
        _cts?.Cancel();

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException) { /* cancelled */ }
            catch (Exception) { /* non-fatal */ }
        }

        _monitorTask = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async Task<HealthCheck> RunCheck(string name)
    {
        if (!_checkFns.TryGetValue(name, out var checkFn))
        {
            return new HealthCheck(
                Name: name,
                Status: "unknown",
                LastCheck: DateTime.UtcNow.ToString("O"),
                ConsecutiveFailures: 0,
                MaxFailures: 0,
                Metadata: new() { ["error"] = "check not registered" });
        }

        HealthCheck healthRecord;
        lock (_stateLock)
        {
            healthRecord = _healthStatus.TryGetValue(name, out var existing)
                ? existing
                : new HealthCheck(
                    Name: name,
                    Status: "unknown",
                    LastCheck: "",
                    ConsecutiveFailures: 0,
                    MaxFailures: _maxFailures.GetValueOrDefault(name, 3),
                    Metadata: new());
        }

        try
        {
            var healthy = await checkFn(_cts?.Token ?? CancellationToken.None);

            if (healthy)
            {
                healthRecord = healthRecord with
                {
                    Status = "healthy",
                    LastCheck = DateTime.UtcNow.ToString("O"),
                    ConsecutiveFailures = 0
                };
            }
            else
            {
                var consecutive = healthRecord.ConsecutiveFailures + 1;
                healthRecord = healthRecord with
                {
                    Status = consecutive >= healthRecord.MaxFailures ? "critical" : "degraded",
                    LastCheck = DateTime.UtcNow.ToString("O"),
                    ConsecutiveFailures = consecutive
                };

                if (consecutive >= healthRecord.MaxFailures)
                {
                    await ExecuteRecovery(name, _cts?.Token ?? CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            var consecutive = healthRecord.ConsecutiveFailures + 1;
            healthRecord = healthRecord with
            {
                Status = consecutive >= healthRecord.MaxFailures ? "critical" : "degraded",
                LastCheck = DateTime.UtcNow.ToString("O"),
                ConsecutiveFailures = consecutive,
                Metadata = new(healthRecord.Metadata)
                {
                    ["last_error"] = ex.Message
                }
            };
        }

        lock (_stateLock)
        {
            _healthStatus[name] = healthRecord;
        }

        return healthRecord;
    }

    public async Task<Dictionary<string, HealthCheck>> RunAllChecks()
    {
        var results = new Dictionary<string, HealthCheck>();
        var tasks = new List<Task>();

        lock (_stateLock)
        {
            foreach (var name in _checkFns.Keys.ToList())
            {
                tasks.Add(Task.Run(async () =>
                {
                    var result = await RunCheck(name);
                    lock (results)
                    {
                        results[name] = result;
                    }
                }));
            }
        }

        await Task.WhenAll(tasks);
        return results;
    }

    public Task<Dictionary<string, object?>> HealCell(object cell)
    {
        var result = new Dictionary<string, object?>
        {
            ["action"] = "cell_heal_attempted",
            ["cell_type"] = cell.GetType().Name,
            ["timestamp"] = DateTime.UtcNow.ToString("O"),
            ["status"] = "logged"
        };

        return Task.FromResult(result);
    }

    public Dictionary<string, object?> GetStatus()
    {
        lock (_stateLock)
        {
            var healthy = _healthStatus.Values.Count(h => h.Status == "healthy");
            var total = _healthStatus.Count;

            return new Dictionary<string, object?>
            {
                ["checks_registered"] = _checkFns.Count,
                ["healthy"] = healthy,
                ["degraded"] = _healthStatus.Values.Count(h => h.Status == "degraded"),
                ["critical"] = _healthStatus.Values.Count(h => h.Status == "critical"),
                ["health_ratio"] = total > 0 ? (float)healthy / total : 0f,
                ["active_actions"] = _activeActions.Count,
                ["check_interval"] = _checkInterval,
                ["details"] = _healthStatus.ToDictionary(
                    kv => kv.Key,
                    kv => (object?)new Dictionary<string, object?>
                    {
                        ["status"] = kv.Value.Status,
                        ["consecutive_failures"] = kv.Value.ConsecutiveFailures,
                        ["max_failures"] = kv.Value.MaxFailures,
                        ["last_check"] = kv.Value.LastCheck
                    })
            };
        }
    }

    private async Task MonitorLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunAllChecks();
                await Task.Delay(TimeSpan.FromSeconds(_checkInterval), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(_checkInterval, 10)), cancellationToken);
            }
        }
    }

    private async Task ExecuteRecovery(string name, CancellationToken cancellationToken)
    {
        if (!_recoveryStrategies.TryGetValue(name, out var strategies) || strategies.Count == 0)
            return;

        var action = new RecoveryAction
        {
            Name = $"recover_{name}",
            Target = name,
            Strategy = "sequential",
            Status = "running"
        };

        _activeActions[action.Name] = action;

        try
        {
            foreach (var strategy in strategies)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var success = await strategy(cancellationToken);
                    if (success)
                    {
                        action.MarkCompleted("recovery successful");
                        ResetFailureCount(name);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    action.MarkFailed(ex.Message);
                }
            }

            action.MarkFailed("all recovery strategies exhausted");
        }
        finally
        {
            _activeActions.TryRemove(action.Name, out _);
        }
    }

    private void ResetFailureCount(string name)
    {
        lock (_stateLock)
        {
            if (_healthStatus.TryGetValue(name, out var current))
            {
                _healthStatus[name] = current with
                {
                    Status = "recovering",
                    ConsecutiveFailures = 0,
                    LastCheck = DateTime.UtcNow.ToString("O")
                };
            }
        }
    }
}
