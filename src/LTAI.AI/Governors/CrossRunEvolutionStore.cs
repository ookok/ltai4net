using LiteDB;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

public enum LessonCategory
{
    ExperimentFailure,
    HypothesisRejected,
    ModelDegradation,
    BudgetExhausted,
    SafetyViolation,
    RoutingError,
    ContextOverflow,
    QualityRegression,
    DependencyConflict,
    ExecutionTimeout,
    DataDrift,
    GeneralWarning
}

public sealed class EvolutionLesson
{
    public ObjectId Id { get; set; } = default!;
    public string Category { get; set; } = "";
    public float Severity { get; set; }
    public string Summary { get; set; } = "";
    public string Mitigation { get; set; } = "";
    public string? SourceRun { get; set; }
    public string? SourceStage { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public int AppliedCount { get; set; }
    public DateTime? LastAppliedAt { get; set; }

    public float GetWeight(double halfLifeDays = 30)
    {
        var deltaDays = (DateTime.UtcNow - RecordedAt).TotalDays;
        return Severity * (float)Math.Exp(-Math.Log(2) * deltaDays / halfLifeDays);
    }
}

public interface ICrossRunEvolutionStore : IDisposable
{
    void RecordLesson(EvolutionLesson lesson);
    void RecordLessons(IEnumerable<EvolutionLesson> lessons);
    List<EvolutionLesson> GetActiveLessons(int limit = 50);
    List<EvolutionLesson> GetLessonsByCategory(LessonCategory category, int limit = 20);
    List<EvolutionLesson> GetRelevantLessons(string? context = null, int limit = 10);
    void MarkApplied(ObjectId id);
    void MarkBatchApplied(IEnumerable<ObjectId> ids);
    string FormatLessonsAsPrompt(int maxLessons = 10);
    int LessonCount { get; }
    int ActiveLessonCount { get; }
}

public sealed class CrossRunEvolutionStore : ICrossRunEvolutionStore
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<EvolutionLesson> _lessons;
    private readonly Lock _lock = new();
    private readonly double _halfLifeDays;
    private readonly ILogger<CrossRunEvolutionStore> _logger;

    public CrossRunEvolutionStore(string dbPath, double halfLifeDays = 30, ILogger<CrossRunEvolutionStore>? logger = null)
    {
        _halfLifeDays = halfLifeDays;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CrossRunEvolutionStore>.Instance;
        _db = new LiteDatabase($"Filename={dbPath};Connection=Shared");
        _lessons = _db.GetCollection<EvolutionLesson>("evolution_lessons");
        _lessons.EnsureIndex(x => x.Category);
        _lessons.EnsureIndex(x => x.RecordedAt);
        _lessons.EnsureIndex(x => x.AppliedCount);
    }

    public int LessonCount
    {
        get { lock (_lock) return _lessons.Count(); }
    }

    public int ActiveLessonCount
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddDays(-_halfLifeDays * 3);
            lock (_lock) return _lessons.Count(l => l.RecordedAt > cutoff && l.Severity > 0);
        }
    }

    public void RecordLesson(EvolutionLesson lesson)
    {
        lesson.Id = ObjectId.NewObjectId();
        lesson.RecordedAt = DateTime.UtcNow;
        lock (_lock) _lessons.Insert(lesson);
    }

    public void RecordLessons(IEnumerable<EvolutionLesson> lessons)
    {
        var now = DateTime.UtcNow;
        var list = lessons.ToList();
        foreach (var l in list)
        {
            l.Id = ObjectId.NewObjectId();
            l.RecordedAt = now;
        }
        lock (_lock) _lessons.InsertBulk(list);
    }

    public List<EvolutionLesson> GetActiveLessons(int limit = 50)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_halfLifeDays * 3);
        lock (_lock)
        {
            return _lessons.Find(l => l.RecordedAt > cutoff && l.Severity > 0)
                .OrderByDescending(l => l.GetWeight(_halfLifeDays))
                .Take(limit)
                .ToList();
        }
    }

    public List<EvolutionLesson> GetLessonsByCategory(LessonCategory category, int limit = 20)
    {
        var cat = category.ToString();
        lock (_lock)
        {
            return _lessons.Find(l => l.Category == cat)
                .OrderByDescending(l => l.Severity)
                .ThenByDescending(l => l.RecordedAt)
                .Take(limit)
                .ToList();
        }
    }

    public List<EvolutionLesson> GetRelevantLessons(string? context = null, int limit = 10)
    {
        var cutoff = DateTime.UtcNow.AddDays(-_halfLifeDays * 3);
        lock (_lock)
        {
            var active = _lessons.Find(l => l.RecordedAt > cutoff && l.Severity > 0).ToList();

            if (!string.IsNullOrWhiteSpace(context))
            {
                var tokens = context.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                active = active.Where(l =>
                    tokens.Any(t =>
                        l.Category.ToLowerInvariant().Contains(t) ||
                        l.Mitigation.ToLowerInvariant().Contains(t) ||
                        l.Summary.ToLowerInvariant().Contains(t)))
                    .ToList();
            }

            return active
                .OrderByDescending(l => l.GetWeight(_halfLifeDays))
                .Take(limit)
                .ToList();
        }
    }

    public void MarkApplied(ObjectId id)
    {
        lock (_lock)
        {
            var lesson = _lessons.FindById(id);
            if (lesson != null)
            {
                lesson.AppliedCount++;
                lesson.LastAppliedAt = DateTime.UtcNow;
                _lessons.Update(lesson);
            }
        }
    }

    public void MarkBatchApplied(IEnumerable<ObjectId> ids)
    {
        foreach (var id in ids) MarkApplied(id);
    }

    public string FormatLessonsAsPrompt(int maxLessons = 10)
    {
        var active = GetActiveLessons(maxLessons);
        if (active.Count == 0) return "";

        var lines = new List<string>
        {
            "## Past Lessons from Previous Runs (time-decayed)",
            "The following lessons were learned in prior runs. Consider them when making decisions:",
            ""
        };

        foreach (var l in active)
        {
            var weight = l.GetWeight(_halfLifeDays);
            var bar = weight > 0.7f ? "HIGH" : weight > 0.3f ? "MED" : "LOW";
            lines.Add($"- [{bar} | {l.Category}] {l.Summary}");
            lines.Add($"  Mitigation: {l.Mitigation}");
        }

        return string.Join("\n", lines);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose database in CrossRunEvolutionStore"); }
    }
}
