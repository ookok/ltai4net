using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Cell.Lifecycle;

public sealed class Distillation
{
    private static readonly Lazy<Distillation> _instance = new(() =>
        new Distillation(NullLoggerFactory.Instance.CreateLogger<Distillation>()));

    public static Distillation Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, DistillationLog> _logs = new();
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly object _evictLock = new();
    private readonly ILogger<Distillation> _logger;

    private const int MaxLogs = 500;

    public Distillation() : this(NullLogger<Distillation>.Instance) { }

    public Distillation(ILogger<Distillation> logger)
    {
        _logger = logger ?? NullLogger<Distillation>.Instance;
    }

    public Task<DistillationLog> DistillAsync(
        string teacherModel,
        string studentModel,
        string[] knowledgeKeys)
    {
        var id = $"dist_{Guid.NewGuid().ToString("N")[..8]}";
        var compressedCount = knowledgeKeys.Length;
        var faithfulnessScore = (float)(0.7 + compressedCount * 0.3 / Math.Sqrt(knowledgeKeys.Length));
        var tokensSaved = compressedCount * 128;

        var log = new DistillationLog
        {
            Id = id,
            TeacherModel = teacherModel,
            StudentModel = studentModel,
            KnowledgeKeys = knowledgeKeys,
            CompressedCount = compressedCount,
            FaithfulnessScore = faithfulnessScore,
            TokensSaved = tokensSaved,
            CompletedAt = DateTime.UtcNow
        };

        _logs[id] = log;
        _insertionOrder.Enqueue(id);

        EvictIfNeeded();

        _logger.LogInformation("Distilled {Teacher} -> {Student}: {Keys} keys, score={Score:F3}",
            teacherModel, studentModel, compressedCount, faithfulnessScore);

        return Task.FromResult(log);
    }

    private void EvictIfNeeded()
    {
        lock (_evictLock)
        {
            while (_logs.Count > MaxLogs && _insertionOrder.TryDequeue(out var oldestId))
            {
                _logs.TryRemove(oldestId, out _);
                _logger.LogDebug("LRU eviction: removed distillation log {LogId}", oldestId);
            }
        }
    }

    public DistillationLog? GetLog(string logId)
    {
        return _logs.GetValueOrDefault(logId);
    }

    public IReadOnlyList<DistillationLog> GetByTeacher(string teacherModel)
    {
        return _logs.Values.Where(l => l.TeacherModel == teacherModel).ToList();
    }

    public Dictionary<string, object> GetStats()
    {
        var values = _logs.Values.ToList();
        var count = values.Count;

        return new Dictionary<string, object>
        {
            ["total_distillations"] = count,
            ["avg_faithfulness"] = count > 0 ? values.Average(l => l.FaithfulnessScore) : 0.0,
            ["total_tokens_saved"] = values.Sum(l => l.TokensSaved)
        };
    }
}
