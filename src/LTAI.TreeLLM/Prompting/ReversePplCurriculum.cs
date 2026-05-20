using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.TreeLLM.Prompting;

public sealed record CurriculumSample
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Content { get; init; } = "";
    public string Domain { get; init; } = "general";
    public double Perplexity { get; set; }
    public int TokenCount { get; init; }
    public int Difficulty { get; init; } = 5;
    public bool Selected { get; set; }
    public int EpochSelected { get; set; }
    public double LastReward { get; set; }
}

public sealed record CurriculumEpoch
{
    public int EpochNumber { get; init; }
    public int SamplesSelected { get; init; }
    public double AvgPerplexity { get; init; }
    public double MinPerplexity { get; init; }
    public double MaxPerplexity { get; init; }
    public long DurationMs { get; init; }
    public List<string> SampleIds { get; init; } = new();
}

public sealed class ReversePplCurriculum
{
    private readonly List<CurriculumSample> _samples = new();
    private readonly List<CurriculumEpoch> _epochs = new();
    private readonly ReversePplConfig _config;
    private readonly ILogger<ReversePplCurriculum>? _logger;
    private readonly object _lock = new();

    public ReversePplCurriculum(ReversePplConfig? config = null,
        ILogger<ReversePplCurriculum>? logger = null)
    {
        _config = config ?? new ReversePplConfig();
        _logger = logger;
    }

    public void AddSamples(IEnumerable<(string content, string domain, int tokenCount)> items)
    {
        lock (_lock)
        {
            foreach (var (content, domain, tokenCount) in items)
            {
                _samples.Add(new CurriculumSample
                {
                    Content = content,
                    Domain = domain,
                    TokenCount = tokenCount,
                    Difficulty = EstimateDifficulty(content, tokenCount)
                });
            }
        }
    }

    public void SetPerplexities(Dictionary<string, double> samplePpl)
    {
        lock (_lock)
        {
            foreach (var (id, ppl) in samplePpl)
            {
                var sample = _samples.FirstOrDefault(s => s.Id == id);
                if (sample != null)
                    sample.Perplexity = ppl;
            }
        }
    }

    public List<CurriculumSample> SelectEpoch(int epochNumber)
    {
        lock (_lock)
        {
            var unselected = _samples.Where(s => !s.Selected).ToList();
            if (unselected.Count == 0)
            {
                foreach (var s in _samples) s.Selected = false;
                unselected = _samples;
            }

            var sorted = SortByReversePpl(unselected, epochNumber);

            var epochCount = Math.Min(_config.SamplesPerEpoch, sorted.Count);
            var selected = sorted.Take(epochCount).ToList();

            double totalWeight = 0;
            foreach (var s in selected)
            {
                s.Selected = true;
                s.EpochSelected = epochNumber;
                totalWeight += s.Perplexity > 0 ? s.Perplexity : 1;
            }

            var avgPpl = selected.Count > 0 ? selected.Average(s => s.Perplexity) : 0;

            _logger?.LogInformation(
                "Epoch {Epoch}: selected {Count}/{Total} samples, avgPPL={AvgPpl:F2}",
                epochNumber, selected.Count, _samples.Count, avgPpl);

            return selected;
        }
    }

    public CurriculumEpoch RunEpoch(
        int epochNumber,
        Func<List<CurriculumSample>, Task<Dictionary<string, double>>> trainFn)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var selected = SelectEpoch(epochNumber);
        var metrics = trainFn(selected).GetAwaiter().GetResult();
        sw.Stop();

        var epoch = new CurriculumEpoch
        {
            EpochNumber = epochNumber,
            SamplesSelected = selected.Count,
            AvgPerplexity = selected.Count > 0 ? selected.Average(s => s.Perplexity) : 0,
            MinPerplexity = selected.Count > 0 ? selected.Min(s => s.Perplexity) : 0,
            MaxPerplexity = selected.Count > 0 ? selected.Max(s => s.Perplexity) : 0,
            DurationMs = sw.ElapsedMilliseconds,
            SampleIds = selected.Select(s => s.Id).ToList()
        };

        lock (_lock) { _epochs.Add(epoch); }
        return epoch;
    }

    public List<CurriculumSample> SortByReversePpl(
        List<CurriculumSample> candidates, int epochNumber)
    {
        var scored = candidates.Select(s =>
        {
            var basePpl = s.Perplexity > 0 ? s.Perplexity : (double)s.Difficulty * 2;
            var recencyBonus = s.EpochSelected > 0
                ? 1.0 / Math.Log(1.5 + epochNumber - s.EpochSelected)
                : 1.0;

            var diversityPenalty = Math.Exp(-0.1 * s.LastReward);

            var score = basePpl * recencyBonus * diversityPenalty;
            return (Sample: s, Score: score);
        }).ToList();

        return scored
            .OrderByDescending(x => x.Score)
            .Select(x => x.Sample)
            .ToList();
    }

    public void UpdateRewards(List<(string sampleId, double reward)> feedback)
    {
        lock (_lock)
        {
            foreach (var (id, reward) in feedback)
            {
                var sample = _samples.FirstOrDefault(s => s.Id == id);
                if (sample != null)
                    sample.LastReward = sample.LastReward * 0.8 + reward * 0.2;
            }
        }
    }

    public List<CurriculumSample> GetSamples(int? topN = null)
    {
        lock (_lock)
        {
            var query = _samples.OrderByDescending(s => s.Perplexity);
            return topN.HasValue ? query.Take(topN.Value).ToList() : query.ToList();
        }
    }

    public Dictionary<string, object> GetCurriculumStats()
    {
        lock (_lock)
        {
            var withPpl = _samples.Where(s => s.Perplexity > 0).ToList();

            return new()
            {
                ["total_samples"] = _samples.Count,
                ["samples_with_ppl"] = withPpl.Count,
                ["epochs_completed"] = _epochs.Count,
                ["avg_perplexity"] = Math.Round(
                    withPpl.Count > 0 ? withPpl.Average(s => s.Perplexity) : 0, 2),
                ["ppl_distribution"] = new
                {
                    very_high = _samples.Count(s => s.Perplexity > 20),
                    high = _samples.Count(s => s.Perplexity is > 10 and <= 20),
                    medium = _samples.Count(s => s.Perplexity is > 5 and <= 10),
                    low = _samples.Count(s => s.Perplexity is > 0 and <= 5)
                },
                ["epochs"] = _epochs.Select(e => new
                {
                    e.EpochNumber,
                    e.SamplesSelected,
                    avg_ppl = Math.Round(e.AvgPerplexity, 2),
                    e.DurationMs
                }).ToList()
            };
        }
    }

    private static int EstimateDifficulty(string content, int tokenCount)
    {
        int difficulty = 5;

        var technicalMarkers = new[] { "proof", "theorem", "lemma", "证明", "定理", "引理",
            "contradiction", "induction", "反证", "归纳", "QED", "证毕", "lim", "∫", "∑" };
        foreach (var m in technicalMarkers)
            if (content.Contains(m, StringComparison.OrdinalIgnoreCase))
                difficulty++;

        var complexPatterns = new[] { "if and only if", "必要充分", "必要且充分",
            "contrapositive", "逆否命题", "Cauchy-Schwarz", "Lagrange" };
        foreach (var p in complexPatterns)
            if (content.Contains(p, StringComparison.OrdinalIgnoreCase))
                difficulty += 2;

        if (tokenCount > 5000) difficulty += 3;
        else if (tokenCount > 2000) difficulty += 2;
        else if (tokenCount > 500) difficulty += 1;

        return Math.Min(20, difficulty);
    }
}

public sealed class ReversePplConfig
{
    public int SamplesPerEpoch { get; set; } = 64;
    public int TotalEpochs { get; set; } = 4;
    public bool SortDescending { get; set; } = true;
    public double ExplorationRate { get; set; } = 0.1;
    public double DiversityWeight { get; set; } = 0.1;
}
