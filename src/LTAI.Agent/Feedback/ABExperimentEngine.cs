using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Feedback;

public enum ExperimentStatus { Draft, Running, Completed, Cancelled }

public sealed record ExperimentVariant
{
    public string Name { get; init; } = "";
    public double Weight { get; init; } = 1.0;
    public Dictionary<string, string> Parameters { get; init; } = new();
    public int Impressions { get; set; }
    public int Conversions { get; set; }
    public double TotalScore { get; set; }
}

public sealed record ExperimentResult
{
    public string ExperimentId { get; init; } = "";
    public string VariantName { get; init; } = "";
    public int Impressions { get; init; }
    public int Conversions { get; init; }
    public double ConversionRate { get; init; }
    public double AverageScore { get; init; }
    public double ConfidenceLevel { get; init; }
    public bool IsWinner { get; init; }
}

public sealed class ABExperiment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ExperimentStatus Status { get; set; } = ExperimentStatus.Draft;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int MinSampleSize { get; init; } = 100;
    public Dictionary<string, ExperimentVariant> Variants { get; init; } = new();
    public string? WinnerVariant { get; set; }
}

public sealed class ABExperimentEngine
{
    private readonly ILogger<ABExperimentEngine> _logger;
    private readonly ConcurrentDictionary<string, ABExperiment> _experiments = new();
    private readonly ConcurrentDictionary<string, string> _sessionAssignments = new();
    private readonly object _lock = new();

    public ABExperimentEngine(ILogger<ABExperimentEngine> logger)
    {
        _logger = logger;
    }

    public ABExperiment CreateExperiment(string name, string description, string[] variantNames, int minSampleSize = 100)
    {
        var experiment = new ABExperiment
        {
            Name = name,
            Description = description,
            MinSampleSize = minSampleSize,
            Variants = variantNames.ToDictionary(
                v => v,
                v => new ExperimentVariant { Name = v, Weight = 1.0 })
        };

        _experiments[experiment.Id] = experiment;
        _logger.LogInformation("ABExperiment: Created '{Name}' with {Count} variants", name, variantNames.Length);
        return experiment;
    }

    public void StartExperiment(string experimentId)
    {
        if (_experiments.TryGetValue(experimentId, out var experiment))
        {
            experiment.Status = ExperimentStatus.Running;
            experiment.StartedAt = DateTime.UtcNow;
            _logger.LogInformation("ABExperiment: Started '{Name}'", experiment.Name);
        }
    }

    public string AssignVariant(string experimentId, string sessionId)
    {
        var assignmentKey = $"{experimentId}:{sessionId}";

        if (_sessionAssignments.TryGetValue(assignmentKey, out var existing))
            return existing;

        if (!_experiments.TryGetValue(experimentId, out var experiment) ||
            experiment.Status != ExperimentStatus.Running)
            return "control";

        var variant = SelectVariant(experiment);
        _sessionAssignments[assignmentKey] = variant;

        lock (_lock)
        {
            if (experiment.Variants.TryGetValue(variant, out var v))
                v.Impressions++;
        }

        return variant;
    }

    public void RecordConversion(string experimentId, string sessionId, double score = 1.0)
    {
        var assignmentKey = $"{experimentId}:{sessionId}";

        if (!_sessionAssignments.TryGetValue(assignmentKey, out var variantName))
            return;

        if (!_experiments.TryGetValue(experimentId, out var experiment))
            return;

        lock (_lock)
        {
            if (experiment.Variants.TryGetValue(variantName, out var variant))
            {
                variant.Conversions++;
                variant.TotalScore += score;
            }

            CheckCompletion(experiment);
        }
    }

    public List<ExperimentResult> GetResults(string experimentId)
    {
        if (!_experiments.TryGetValue(experimentId, out var experiment))
            return new List<ExperimentResult>();

        var results = new List<ExperimentResult>();
        var maxConversionRate = 0.0;
        string? winnerName = null;

        foreach (var (name, variant) in experiment.Variants)
        {
            var conversionRate = variant.Impressions > 0
                ? (double)variant.Conversions / variant.Impressions
                : 0.0;
            var averageScore = variant.Conversions > 0
                ? variant.TotalScore / variant.Conversions
                : 0.0;

            if (conversionRate > maxConversionRate)
            {
                maxConversionRate = conversionRate;
                winnerName = name;
            }

            results.Add(new ExperimentResult
            {
                ExperimentId = experimentId,
                VariantName = name,
                Impressions = variant.Impressions,
                Conversions = variant.Conversions,
                ConversionRate = conversionRate,
                AverageScore = averageScore,
                ConfidenceLevel = ComputeConfidence(variant),
                IsWinner = false
            });
        }

        if (winnerName != null)
        {
            var winnerResult = results.Find(r => r.VariantName == winnerName);
            if (winnerResult != null)
            {
                var idx = results.IndexOf(winnerResult);
                results[idx] = winnerResult with { IsWinner = true };
            }
        }

        return results;
    }

    public Dictionary<string, object> GetStatus()
    {
        return new()
        {
            ["total_experiments"] = _experiments.Count,
            ["running"] = _experiments.Values.Count(e => e.Status == ExperimentStatus.Running),
            ["completed"] = _experiments.Values.Count(e => e.Status == ExperimentStatus.Completed),
            ["experiments"] = _experiments.Values.Select(e => new
            {
                e.Id,
                e.Name,
                status = e.Status.ToString(),
                variants = e.Variants.Count,
                total_impressions = e.Variants.Values.Sum(v => v.Impressions),
                total_conversions = e.Variants.Values.Sum(v => v.Conversions),
                e.WinnerVariant
            }).ToList()
        };
    }

    private static string SelectVariant(ABExperiment experiment)
    {
        var totalWeight = experiment.Variants.Values.Sum(v => v.Weight);
        var random = Random.Shared.NextDouble() * totalWeight;
        var cumulative = 0.0;

        foreach (var (name, variant) in experiment.Variants)
        {
            cumulative += variant.Weight;
            if (random <= cumulative)
                return name;
        }

        return experiment.Variants.Keys.First();
    }

    private void CheckCompletion(ABExperiment experiment)
    {
        var totalImpressions = experiment.Variants.Values.Sum(v => v.Impressions);
        if (totalImpressions < experiment.MinSampleSize)
            return;

        var results = GetResults(experiment.Id);
        var allConfident = results.All(r => r.ConfidenceLevel >= 0.95);

        if (allConfident || totalImpressions >= experiment.MinSampleSize * 3)
        {
            experiment.Status = ExperimentStatus.Completed;
            experiment.CompletedAt = DateTime.UtcNow;
            experiment.WinnerVariant = results
                .OrderByDescending(r => r.ConversionRate)
                .FirstOrDefault()?.VariantName;

            _logger.LogInformation("ABExperiment: Completed '{Name}', winner='{Winner}'",
                experiment.Name, experiment.WinnerVariant);
        }
    }

    private static double ComputeConfidence(ExperimentVariant variant)
    {
        if (variant.Impressions < 10) return 0.0;

        var p = (double)variant.Conversions / variant.Impressions;
        var se = Math.Sqrt(p * (1 - p) / variant.Impressions);
        var z = se > 0 ? p / se : 0;

        // Approximate normal CDF for z-score
        return z switch
        {
            >= 2.576 => 0.99,
            >= 1.96 => 0.95,
            >= 1.645 => 0.90,
            >= 1.28 => 0.80,
            _ => Math.Min(0.79, z / 2.0)
        };
    }
}
