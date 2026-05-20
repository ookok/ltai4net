namespace LTAI.TreeLLM.EDCO;

public sealed class InfoLawConfig
{
    public double ModelSizeB { get; set; } = 4;     // Model size in billions of parameters (default Qwen3-4B)
    public double BaseQuality { get; set; } = 0.7;   // Base data quality [0,1]
    public double QualityExponent { get; set; } = 0.35;
    public double TokenExponent { get; set; } = -0.28;
    public double RepetitionDecay { get; set; } = 0.15;
    public double MixtureEntropyWeight { get; set; } = 0.12;
    public int MaxRepetitions { get; set; } = 8;
}

public sealed class DataRecipe
{
    public string RecipeId { get; set; } = "";
    public Dictionary<string, double> DomainWeights { get; set; } = new();
    public int Repetitions { get; set; } = 1;
    public double TotalTokens { get; set; }
}

public sealed class InfoLawPrediction
{
    public double PredictedLoss { get; set; }
    public double Confidence { get; set; }
    public double OptimalWeight { get; set; }
    public int OptimalRepetitions { get; set; }
    public string Recommendation { get; set; } = "";
    public Dictionary<string, double> DomainContributions { get; set; } = new();
}

public sealed class InfoLawPredictor
{
    private readonly InfoLawConfig _config;
    private readonly Dictionary<string, double> _domainQuality = new();
    private readonly Dictionary<string, int> _repetitionCount = new();
    private readonly List<PredictionRecord> _history = new();

    public InfoLawPredictor(InfoLawConfig? config = null) => _config = config ?? new();

    public InfoLawPrediction Predict(DataRecipe recipe)
    {
        var informationDensity = ComputeInformationDensity(recipe);
        var repetitionPenalty = ComputeRepetitionPenalty(recipe.Repetitions);
        var mixtureBonus = ComputeMixtureBonus(recipe.DomainWeights);
        var tokenScale = ComputeTokenScale(recipe.TotalTokens);
        var modelScale = ComputeModelScale();

        var predictedLoss = BaseLoss() - informationDensity * _config.BaseQuality
                            + repetitionPenalty - mixtureBonus - tokenScale - modelScale;
        predictedLoss = Math.Max(0.01, Math.Min(10.0, predictedLoss));

        var (optReps, minLoss) = FindOptimalRepetitions(recipe);
        var domainContributions = ComputeDomainContributions(recipe);

        var prediction = new InfoLawPrediction
        {
            PredictedLoss = Math.Round(predictedLoss, 4),
            Confidence = Math.Round(1.0 / (1.0 + Math.Abs(predictedLoss - minLoss)), 3),
            OptimalWeight = Math.Round(minLoss, 4),
            OptimalRepetitions = optReps,
            Recommendation = GenerateRecommendation(predictedLoss, optReps, recipe.Repetitions),
            DomainContributions = domainContributions
        };

        _history.Add(new PredictionRecord(recipe.RecipeId, predictedLoss, recipe.TotalTokens));
        return prediction;
    }

    public DataRecipe FindOptimalRecipe(List<(string domain, string content, double quality)> samples, double budgetTokens, int maxReps = 8)
    {
        var domains = samples.GroupBy(s => s.domain).ToDictionary(g => g.Key, g => g.ToList());
        var bestRecipe = new DataRecipe();
        var bestLoss = double.MaxValue;

        for (var reps = 1; reps <= maxReps; reps++)
        {
            var tokenPerSample = budgetTokens / (samples.Count * reps);
            if (tokenPerSample < 5) continue;

            var weights = domains.ToDictionary(d => d.Key, d => (double)d.Value.Count / samples.Count);
            var recipe = new DataRecipe
            {
                RecipeId = $"optimal_r{reps}",
                DomainWeights = weights,
                Repetitions = reps,
                TotalTokens = budgetTokens
            };

            var prediction = Predict(recipe);
            if (prediction.PredictedLoss < bestLoss)
            {
                bestLoss = prediction.PredictedLoss;
                bestRecipe = recipe;
            }
        }

        return bestRecipe;
    }

    public void UpdateDomainQuality(string domain, double quality)
    {
        _domainQuality[domain] = _domainQuality.GetValueOrDefault(domain) * 0.8 + quality * 0.2;
    }

    public void RecordRepetition(string domain)
    {
        _repetitionCount[domain] = _repetitionCount.GetValueOrDefault(domain) + 1;
    }

    public Dictionary<string, object> AnalyzeDiminishingReturns(string domain, int maxReps = 10)
    {
        var returns = new List<object>();
        var prevGain = 1.0;

        for (var r = 1; r <= maxReps; r++)
        {
            var recipe = new DataRecipe
            {
                RecipeId = $"analysis_{domain}_r{r}",
                DomainWeights = new() { [domain] = 1.0 },
                Repetitions = r,
                TotalTokens = 1e9
            };
            var pred = Predict(recipe);
            var gain = prevGain - pred.PredictedLoss;
            var diminishing = gain < prevGain * 0.1;
            returns.Add(new { repetition = r, predicted_loss = pred.PredictedLoss, gain = Math.Round(gain, 4), diminishing });
            prevGain = pred.PredictedLoss;
            if (diminishing) break;
        }

        return new()
        {
            ["domain"] = domain,
            ["returns"] = returns,
            ["recommended_max_reps"] = returns.Count,
            ["info_density"] = Math.Round(_domainQuality.GetValueOrDefault(domain, 0.7), 3)
        };
    }

    public Dictionary<string, object> GetStats() => new()
    {
        ["predictions"] = _history.Count,
        ["domains_tracked"] = _domainQuality.Count,
        ["repetitions_tracked"] = _repetitionCount.Count,
        ["recent_loss"] = _history.TakeLast(5).Select(r => new { r.RecipeId, r.PredictedLoss, r.Tokens }).ToList(),
        ["mean_absolute_error"] = _history.Count > 1 ? Math.Round(_history.Average(r => Math.Abs(r.PredictedLoss - r.PredictedLoss)), 5) : 0
    };

    private double ComputeInformationDensity(DataRecipe recipe)
    {
        if (recipe.DomainWeights.Count == 0) return 0.5;
        return recipe.DomainWeights.Sum(d =>
        {
            var quality = _domainQuality.GetValueOrDefault(d.Key, _config.BaseQuality);
            var weight = d.Value;
            return quality * weight * Math.Exp(-_config.RepetitionDecay * (_repetitionCount.GetValueOrDefault(d.Key) * 0.1));
        });
    }

    private double ComputeRepetitionPenalty(int repetitions)
    {
        if (repetitions <= 1) return 0;
        var effectiveReps = Math.Min(repetitions, _config.MaxRepetitions);
        return _config.RepetitionDecay * Math.Log(effectiveReps) * _config.BaseQuality;
    }

    private double ComputeMixtureBonus(Dictionary<string, double> weights)
    {
        if (weights.Count <= 1) return 0;
        var entropy = -weights.Values.Sum(w => w > 0 ? w * Math.Log(w) : 0);
        return entropy * _config.MixtureEntropyWeight;
    }

    private double ComputeTokenScale(double tokens)
    {
        if (tokens <= 0) return 0;
        return Math.Pow(tokens / 1e9, _config.TokenExponent) * 0.5;
    }

    private double ComputeModelScale()
    {
        return 0.2 * Math.Log10(_config.ModelSizeB);
    }

    private static double BaseLoss() => 3.0;

    private (int optimalReps, double minLoss) FindOptimalRepetitions(DataRecipe recipe)
    {
        var bestReps = recipe.Repetitions;
        var minLoss = double.MaxValue;

        for (var r = 1; r <= _config.MaxRepetitions; r++)
        {
            var testRecipe = new DataRecipe
            {
                DomainWeights = recipe.DomainWeights,
                Repetitions = r,
                TotalTokens = recipe.TotalTokens
            };
            var loss = PredictLossOnly(testRecipe);
            if (loss < minLoss) { minLoss = loss; bestReps = r; }
        }

        return (bestReps, minLoss);
    }

    private double PredictLossOnly(DataRecipe recipe)
    {
        var infoDensity = ComputeInformationDensity(recipe);
        var repPenalty = ComputeRepetitionPenalty(recipe.Repetitions);
        var mixBonus = ComputeMixtureBonus(recipe.DomainWeights);
        return BaseLoss() - infoDensity * _config.BaseQuality + repPenalty - mixBonus;
    }

    private Dictionary<string, double> ComputeDomainContributions(DataRecipe recipe)
    {
        return recipe.DomainWeights.ToDictionary(
            d => d.Key,
            d =>
            {
                var quality = _domainQuality.GetValueOrDefault(d.Key, _config.BaseQuality);
                var repCount = _repetitionCount.GetValueOrDefault(d.Key);
                var contribution = quality * d.Value * Math.Exp(-_config.RepetitionDecay * repCount * 0.1);
                return Math.Round(contribution, 4);
            });
    }

    private static string GenerateRecommendation(double predictedLoss, int optimalReps, int currentReps)
    {
        if (predictedLoss < 0.5) return "reduce_repetition";
        if (currentReps > optimalReps) return $"overtraining_detected (optimal: {optimalReps} reps, current: {currentReps} reps)";
        if (predictedLoss > 2.0) return "increase_quality_density";
        return "optimal_continue";
    }

    private sealed record PredictionRecord(string RecipeId, double PredictedLoss, double Tokens);
}
