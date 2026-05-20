using System.Text.Json;

namespace LTAI.TreeLLM.EDCO;

public sealed class EdcoRound
{
    public int RoundNumber { get; set; }
    public int SamplesSelected { get; set; }
    public double AvgEntropy { get; set; }
    public double AvgReward { get; set; }
    public double Improvement { get; set; }
    public long DurationMs { get; set; }
    public List<string> SelectedSampleIds { get; set; } = new();
    public Dictionary<string, double> Metrics { get; set; } = new();
}

public sealed class EdcoCurriculumOrchestrator
{
    private static readonly Lazy<EdcoCurriculumOrchestrator> _instance = new(() => new EdcoCurriculumOrchestrator());
    public static EdcoCurriculumOrchestrator Instance => _instance.Value;

    private readonly EdcoEntropyEstimator _estimator;
    private readonly EdcoConfig _config;
    private readonly List<EdcoSample> _pool = new();
    private readonly List<EdcoRound> _rounds = new();
    private readonly Dictionary<string, double> _entropyHistory = new();
    private readonly Random _rng = new(42);
    private readonly string _checkpointDir;
    private readonly object _lock = new();

    public EdcoCurriculumOrchestrator(EdcoConfig? config = null)
    {
        _config = config ?? new();
        _estimator = new(_config);
        _checkpointDir = global::System.IO.Path.Combine(".livingtree", "edco");
        global::System.IO.Directory.CreateDirectory(_checkpointDir);
    }

    public void AddToPool(List<EdcoSample> samples)
    {
        lock (_lock)
        {
            foreach (var s in samples)
            {
                s.Id = string.IsNullOrEmpty(s.Id) ? Guid.NewGuid().ToString("N")[..10] : s.Id;
                _pool.Add(s);
            }
        }
    }

    public List<EdcoSample> SelectCurriculum(int round)
    {
        lock (_lock)
        {
            if (round > 1)
            {
                foreach (var s in _pool)
                {
                    s.Entropy = _config.EnableEntropyHistory
                        ? _estimator.EstimateEntropy(s.Content)
                        : _estimator.EstimatePrefixEntropy(s.Content);
                    s.EntropyHistory.Add(s.Entropy);
                }
            }

            var selected = new List<EdcoSample>();
            var candidates = _pool.OrderByDescending(s => s.Entropy).ThenByDescending(s => s.TokenCount).ToList();

            foreach (var s in candidates)
            {
                if (selected.Count >= _config.SamplesPerRound)
                    break;

                if (s.RoundSelected > 0 && s.Entropy < _config.EntropyThreshold * 0.5)
                    continue;

                s.Selected = true;
                s.RoundSelected = round;
                selected.Add(s);
            }

            var exploreCount = (int)(_config.SamplesPerRound * _config.ExplorationRate);
            for (var i = 0; i < exploreCount && i < candidates.Count; i++)
            {
                var idx = _rng.Next(candidates.Count);
                if (!candidates[idx].Selected)
                {
                    candidates[idx].Selected = true;
                    candidates[idx].RoundSelected = round;
                    selected.Add(candidates[idx]);
                }
            }

            return selected.Take(_config.SamplesPerRound).ToList();
        }
    }

    public async Task<EdcoRound> RunRoundAsync(int round, Func<List<EdcoSample>, Task<Dictionary<string, double>>> trainFn)
    {
        var sw = global::System.Diagnostics.Stopwatch.StartNew();

        var selected = SelectCurriculum(round);
        var metrics = await trainFn(selected);

        var edcoRound = new EdcoRound
        {
            RoundNumber = round,
            SamplesSelected = selected.Count,
            AvgEntropy = selected.Count > 0 ? selected.Average(s => s.Entropy) : 0,
            AvgReward = metrics.GetValueOrDefault("avg_reward", 0),
            Improvement = metrics.GetValueOrDefault("improvement", 0),
            DurationMs = sw.ElapsedMilliseconds,
            SelectedSampleIds = selected.Select(s => s.Id).ToList(),
            Metrics = metrics
        };

        lock (_lock) { _rounds.Add(edcoRound); }

        SaveCheckpoint(round, selected, edcoRound);
        sw.Stop();

        return edcoRound;
    }

    public async Task<List<EdcoRound>> RunFullCurriculumAsync(
        Func<List<EdcoSample>, Task<Dictionary<string, double>>> trainFn,
        int? totalRounds = null)
    {
        var rounds = totalRounds ?? _config.TotalRounds;
        var results = new List<EdcoRound>();

        for (var r = 1; r <= rounds; r++)
        {
            var edcoRound = await RunRoundAsync(r, trainFn);
            results.Add(edcoRound);
        }

        return results;
    }

    public void UpdateWithRewards(List<(string sampleId, double reward)> feedback)
    {
        lock (_lock)
        {
            foreach (var (id, reward) in feedback)
            {
                var sample = _pool.FirstOrDefault(s => s.Id == id);
                if (sample != null)
                    sample.Reward = (sample.Reward * 0.7 + reward * 0.3);
            }
        }
    }

    public Dictionary<string, object> GetCurriculumStats()
    {
        lock (_lock)
        {
            return new()
            {
                ["pool_size"] = _pool.Count,
                ["rounds_completed"] = _rounds.Count,
                ["rounds"] = _rounds.Select(r => new
                {
                    r.RoundNumber,
                    r.SamplesSelected,
                    avg_entropy = Math.Round(r.AvgEntropy, 3),
                    avg_reward = Math.Round(r.AvgReward, 3),
                    r.Improvement,
                    duration_s = r.DurationMs / 1000.0
                }).ToList(),
                ["entropy_distribution"] = new
                {
                    high = _pool.Count(s => s.Entropy > 0.7),
                    medium = _pool.Count(s => s.Entropy is >= 0.3 and <= 0.7),
                    low = _pool.Count(s => s.Entropy < 0.3)
                },
                ["entropy_estimation"] = _estimator.GetStats()
            };
        }
    }

    public EdcoConfig Config => _config;

    private void SaveCheckpoint(int round, List<EdcoSample> selected, EdcoRound edcoRound)
    {
        var path = global::System.IO.Path.Combine(_checkpointDir, $"round_{round}.json");
        var data = new
        {
            round = edcoRound.RoundNumber,
            samples = selected.Select(s => new { s.Id, s.Entropy, s.RoundSelected }).ToList(),
            metrics = edcoRound.Metrics,
            duration_ms = edcoRound.DurationMs
        };
        global::System.IO.File.WriteAllText(path, JsonSerializer.Serialize(data));
    }
}
