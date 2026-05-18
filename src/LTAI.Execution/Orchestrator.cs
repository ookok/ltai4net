using System.Diagnostics;
using LTAI.Execution.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Execution;

public sealed class Orchestrator
{
    private readonly int _maxAgents;
    private readonly int _maxParallel;
    private readonly SemaphoreSlim _semaphore;
    private readonly List<AgentSpec> _agents = new();
    private readonly object _lock = new();
    private readonly ILogger<Orchestrator> _logger;

    public Orchestrator(int maxAgents = 20, int maxParallel = 10)
    {
        _maxAgents = maxAgents;
        _maxParallel = maxParallel;
        _semaphore = new SemaphoreSlim(maxParallel);
        _logger = NullLogger.Instance;
    }

    internal Orchestrator(int maxAgents, int maxParallel, ILogger<Orchestrator> logger)
    {
        _maxAgents = maxAgents;
        _maxParallel = maxParallel;
        _semaphore = new SemaphoreSlim(maxParallel);
        _logger = logger;
    }

    public string RegisterAgent(AgentSpec spec)
    {
        lock (_lock)
        {
            if (_agents.Count >= _maxAgents)
            {
                var oldest = _agents[0];
                _agents.RemoveAt(0);
                _logger.LogWarning("Agent capacity reached ({Max}), evicting oldest: {Id}", _maxAgents, oldest.Id);
            }

            _agents.Add(spec);
        }

        _logger.LogInformation("Agent registered: {Id} ({Name})", spec.Id, spec.Name);
        return spec.Id;
    }

    public bool UnregisterAgent(string agentId)
    {
        lock (_lock)
        {
            var removed = _agents.RemoveAll(a => a.Id == agentId);
            if (removed > 0)
            {
                _logger.LogInformation("Agent unregistered: {Id}", agentId);
                return true;
            }
        }

        return false;
    }

    public List<AgentSpec> GetAvailableAgents(string? role = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(role))
                return _agents.Where(a => a.Status == "idle").ToList();

            return _agents
                .Where(a => a.Status == "idle" && a.CanHandle(role))
                .ToList();
        }
    }

    public async Task<Dictionary<string, object?>> AssignTask(
        Dictionary<string, object?> task,
        List<AgentSpec>? agents = null)
    {
        var available = agents ?? GetAvailableAgents();

        var roleList = new List<string>();
        if (task.TryGetValue("roles", out var roles) && roles is List<string> rList)
            roleList = rList;
        else if (task.TryGetValue("agent_roles", out var aRoles) && aRoles is List<string> arList)
            roleList = arList;

        var agent = MatchAgent(roleList, available);

        if (agent != null)
        {
            _logger.LogInformation("Assigning task to agent {Id}: {Name}", agent.Id, agent.Name);

            var result = await ExecuteWithAgent(agent, task);

            return new Dictionary<string, object?>
            {
                ["agent_id"] = agent.Id,
                ["agent_name"] = agent.Name,
                ["result"] = result,
                ["status"] = "completed"
            };
        }

        _logger.LogWarning("No agent matched for task roles: {Roles}", string.Join(", ", roleList));

        var fallbackResult = await ExecuteFallback(task);

        return new Dictionary<string, object?>
        {
            ["agent_id"] = "fallback",
            ["agent_name"] = "fallback",
            ["result"] = fallbackResult,
            ["status"] = "completed"
        };
    }

    public async Task<TaskSpec> ExecutePlan(
        TaskSpec taskSpec,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Executing plan: {Id} ({Goal})", taskSpec.Id, taskSpec.Goal);

        var spec = taskSpec;
        var maxRetries = 3;

        while (true)
        {
            var readyTasks = spec.GetReadyTasks();

            if (readyTasks.Count == 0)
            {
                if (spec.SubTasks.All(st => st.Status is "completed" or "failed"))
                    break;

                await Task.Delay(50, ct);
                continue;
            }

            var tasks = new List<Task>();
            foreach (var subTask in readyTasks.Take(_maxParallel))
            {
                tasks.Add(ExecuteSubtask(subTask, maxRetries, ct));
            }

            await Task.WhenAll(tasks);

            spec = spec.UpdateProgress();

            _logger.LogDebug("Plan progress: {Progress:P0} ({Status})", spec.Progress, spec.Status);
        }

        return spec;
    }

    public async Task<SubTask> ExecuteSubtask(
        SubTask subtask,
        int maxRetries = 3,
        CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);

        try
        {
            var current = subtask.MarkRunning();
            var retries = 0;

            while (retries <= maxRetries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var agent = MatchAgent(current.AgentRoles, GetAvailableAgents());

                    if (agent != null)
                    {
                        var task = new Dictionary<string, object?>
                        {
                            ["name"] = current.Name,
                            ["description"] = current.Description,
                            ["action"] = current.Action
                        };

                        var result = await ExecuteWithAgent(agent, task);
                        current = current.MarkCompleted(result!);
                    }
                    else
                    {
                        var fallbackResult = await ExecuteFallback(new Dictionary<string, object?>
                        {
                            ["name"] = current.Name,
                            ["action"] = current.Action
                        });
                        current = current.MarkCompleted(fallbackResult!);
                    }

                    break;
                }
                catch (Exception ex)
                {
                    retries++;
                    _logger.LogWarning(ex, "Subtask {Id} failed (attempt {Attempt}/{Max})",
                        current.Id, retries, maxRetries);

                    if (retries > maxRetries)
                    {
                        current = current.MarkFailed(ex.Message);
                    }
                    else
                    {
                        await Task.Delay(100 * retries, ct);
                    }
                }
            }

            return current;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public AgentSpec? MatchAgent(List<string> roles, List<AgentSpec>? agents)
    {
        var available = agents ?? GetAvailableAgents();

        foreach (var role in roles)
        {
            var match = available.FirstOrDefault(a => a.CanHandle(role));
            if (match != null)
                return match;
        }

        return available.FirstOrDefault();
    }

    public async Task<object?> ExecuteWithAgent(AgentSpec agent, Dictionary<string, object?> task)
    {
        var taskName = task.GetValueOrDefault("name")?.ToString() ?? task.GetValueOrDefault("action")?.ToString() ?? "unknown";

        _logger.LogDebug("Agent {Id} ({Name}) executing: {Task}", agent.Id, agent.Name, taskName);

        var simulatedDuration = Random.Shared.Next(50, 300);
        await Task.Delay(simulatedDuration);

        return new Dictionary<string, object?>
        {
            ["agent_id"] = agent.Id,
            ["agent_name"] = agent.Name,
            ["task"] = taskName,
            ["output"] = $"Mock result from {agent.Name} for {taskName}",
            ["latency_ms"] = simulatedDuration
        };
    }

    public async Task<object?> ExecuteFallback(Dictionary<string, object?> task)
    {
        var taskName = task.GetValueOrDefault("name")?.ToString() ?? task.GetValueOrDefault("action")?.ToString() ?? "unknown";

        _logger.LogWarning("Executing fallback for: {Task}", taskName);

        await Task.Delay(100);

        return new Dictionary<string, object?>
        {
            ["source"] = "fallback",
            ["task"] = taskName,
            ["output"] = $"Fallback result for {taskName}"
        };
    }

    public Dictionary<string, object?> GetStatus()
    {
        lock (_lock)
        {
            return new Dictionary<string, object?>
            {
                ["total_agents"] = _agents.Count,
                ["idle_agents"] = _agents.Count(a => a.Status == "idle"),
                ["busy_agents"] = _agents.Count(a => a.Status == "busy"),
                ["max_agents"] = _maxAgents,
                ["max_parallel"] = _maxParallel,
                ["active_semaphore_count"] = _maxParallel - _semaphore.CurrentCount
            };
        }
    }

    private sealed class NullLogger : ILogger<Orchestrator>
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
