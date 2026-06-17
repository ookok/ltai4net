using LTAI.Agent.Pipeline.Steps;

namespace LTAI.Agent.Orchestration;

public sealed record SolutionCandidate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Content { get; init; } = "";
    public int Generation { get; init; }
    public int StrategyIndex { get; init; }
    public double Score { get; set; }
    public QualityGateResult? GateResult { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public bool IsArchived { get; set; }
}

public sealed class SolutionPool
{
    private readonly List<SolutionCandidate> _candidates = [];
    private readonly int _maxPerStrategy;
    private readonly object _lock = new();

    public IReadOnlyList<SolutionCandidate> Candidates
    {
        get { lock (_lock) return _candidates.ToList(); }
    }

    public int ActiveCount
    {
        get { lock (_lock) return _candidates.Count(c => !c.IsArchived); }
    }

    public SolutionPool(int maxPerStrategy = 5)
    {
        _maxPerStrategy = maxPerStrategy;
    }

    public void Add(SolutionCandidate candidate)
    {
        lock (_lock)
        {
            var strategyCount = _candidates.Count(c =>
                c.StrategyIndex == candidate.StrategyIndex && !c.IsArchived);
            if (strategyCount >= _maxPerStrategy)
            {
                var oldest = _candidates
                    .Where(c => c.StrategyIndex == candidate.StrategyIndex && !c.IsArchived)
                    .OrderBy(c => c.Score)
                    .ThenBy(c => c.CreatedAt)
                    .FirstOrDefault();
                if (oldest != null && oldest.Score < candidate.Score)
                {
                    oldest.IsArchived = true;
                    _candidates.Add(candidate);
                }
            }
            else
            {
                _candidates.Add(candidate);
            }
        }
    }

    public void Archive(string id)
    {
        lock (_lock)
        {
            var c = _candidates.FirstOrDefault(c => c.Id == id);
            if (c != null) c.IsArchived = true;
        }
    }

    public List<SolutionCandidate> GetActive(int minScore = 0)
    {
        lock (_lock)
            return _candidates.Where(c => !c.IsArchived && c.Score >= minScore)
                .OrderByDescending(c => c.Score).ToList();
    }

    public List<SolutionCandidate> GetBest(int count = 3)
    {
        lock (_lock)
            return _candidates.Where(c => !c.IsArchived)
                .OrderByDescending(c => c.Score)
                .Take(count).ToList();
    }

    public string FormatForJudge()
    {
        var best = GetBest(5);
        if (best.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- 候选方案 ---");
        for (int i = 0; i < best.Count; i++)
        {
            sb.AppendLine($"\n[{i + 1}] 策略#{best[i].StrategyIndex + 1} (得分 {best[i].Score:P1})");
            sb.AppendLine(best[i].Content);
        }
        return sb.ToString();
    }

    public void Cleanup(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        lock (_lock)
        {
            _candidates.RemoveAll(c => c.IsArchived && c.CreatedAt < cutoff);
        }
    }
}
