using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Cell.Training;

public sealed class CellTrainer
{
    private static readonly Lazy<CellTrainer> _instance = new(() =>
        new CellTrainer(NullLoggerFactory.Instance.CreateLogger<CellTrainer>()));

    public static CellTrainer Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, TrainingTask> _tasks = new();
    private readonly object _lock = new();
    private readonly ILogger<CellTrainer> _logger;

    public CellTrainer() : this(NullLogger<CellTrainer>.Instance) { }

    public CellTrainer(ILogger<CellTrainer> logger)
    {
        _logger = logger ?? NullLogger<CellTrainer>.Instance;
    }

    public Task<TrainingTask> StartTrainingAsync(
        string modelName,
        string datasetName,
        Dictionary<string, string> hyperParams)
    {
        var id = $"cell_{Guid.NewGuid().ToString("N")[..8]}";
        var task = new TrainingTask
        {
            Id = id,
            ModelName = modelName,
            DatasetName = datasetName,
            HyperParams = new Dictionary<string, string>(hyperParams),
            Status = "pending",
            Epoch = 0,
            Loss = 0f,
            StartedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        _tasks[id] = task;
        _logger.LogInformation("Training task {TaskId} started for model {ModelName}", id, modelName);
        return Task.FromResult(task);
    }

    public Task UpdateEpochAsync(string taskId, float loss)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            lock (_lock)
            {
                var updated = task with
                {
                    Epoch = task.Epoch + 1,
                    Loss = loss,
                    Status = "running"
                };
                _tasks[taskId] = updated;
            }

            _logger.LogDebug("Task {TaskId} updated: epoch={Epoch}, loss={Loss}", taskId, task.Epoch + 1, loss);
        }

        return Task.CompletedTask;
    }

    public Task CompleteTrainingAsync(string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            lock (_lock)
            {
                var updated = task with
                {
                    Status = "done",
                    CompletedAt = DateTime.UtcNow
                };
                _tasks[taskId] = updated;
            }

            _logger.LogInformation("Task {TaskId} completed", taskId);
        }

        return Task.CompletedTask;
    }

    public Task FailTrainingAsync(string taskId, string error)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            lock (_lock)
            {
                var updated = task with
                {
                    Status = "failed"
                };
                _tasks[taskId] = updated;
            }

            _logger.LogWarning("Task {TaskId} failed: {Error}", taskId, error);
        }

        return Task.CompletedTask;
    }

    public TrainingTask? GetTask(string taskId)
    {
        return _tasks.GetValueOrDefault(taskId);
    }

    public IReadOnlyList<TrainingTask> ListTasks()
    {
        return _tasks.Values.ToList();
    }

    public Dictionary<string, int> GetStats()
    {
        var values = _tasks.Values.ToList();
        return new Dictionary<string, int>
        {
            ["total"] = values.Count,
            ["pending"] = values.Count(t => t.Status == "pending"),
            ["running"] = values.Count(t => t.Status == "running"),
            ["done"] = values.Count(t => t.Status == "done"),
            ["failed"] = values.Count(t => t.Status == "failed")
        };
    }
}
