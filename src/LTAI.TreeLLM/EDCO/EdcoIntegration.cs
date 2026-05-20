namespace LTAI.TreeLLM.EDCO;

public static class EdcoIntegration
{
    public static EdcoCurriculumOrchestrator CreateForCellTrainer()
    {
        var orchestrator = new EdcoCurriculumOrchestrator(new EdcoConfig
        {
            SamplesPerRound = 200,
            TotalRounds = 5,
            EntropyThreshold = 0.25,
            PrefixRatio = 0.15,
            EnablePrefixApproximation = true,
            ExplorationRate = 0.1
        });

        return orchestrator;
    }

    public static (EdcoEntropyEstimator estimator, List<EdcoSample>) BuildFromPrompts(
        Dictionary<string, string> prompts, string domain)
    {
        var estimator = new EdcoEntropyEstimator(new EdcoConfig { PrefixRatio = 0.15 });
        var samples = new List<EdcoSample>();

        foreach (var (id, content) in prompts)
        {
            var entropy = estimator.EstimateEntropy(content);
            var isPrefix = estimator.GetStats()["computation_saved"].ToString()!.Contains("83");
            if (isPrefix)
                estimator.UpdateWithSample(content);

            samples.Add(new EdcoSample
            {
                Id = id,
                Content = content,
                Domain = domain,
                Entropy = entropy,
                TokenCount = content.Length / 4,
                Difficulty = entropy
            });
        }

        return (estimator, samples);
    }

    public static List<EdcoSample> RankByEntropy(List<EdcoSample> samples)
    {
        foreach (var s in samples)
            s.Entropy = new EdcoEntropyEstimator().EstimatePrefixEntropy(s.Content);

        return samples.OrderByDescending(s => s.Entropy).ToList();
    }

    public static List<string> SelectTopEntropyPrompts(Dictionary<string, string> prompts, int topK = 5)
    {
        var (_, samples) = BuildFromPrompts(prompts, "prompt-selection");
        return samples.OrderByDescending(s => s.Entropy)
            .Take(topK)
            .Select(s => s.Id)
            .ToList();
    }

    public static async Task SelfImproveWithEDCOAsync(
        EdcoCurriculumOrchestrator orchestrator,
        Func<string, Task<string>> improveFn,
        List<string> defectList,
        int rounds = 3)
    {
        var samples = defectList.Select((d, i) => new EdcoSample
        {
            Id = $"defect_{i}",
            Content = d,
            Domain = "improvement",
            TokenCount = d.Length / 4
        }).ToList();

        orchestrator.AddToPool(samples);

        await orchestrator.RunFullCurriculumAsync(async selected =>
        {
            double totalReward = 0;
            foreach (var s in selected)
            {
                var result = await improveFn(s.Content);
                var reward = result.Contains("fixed", StringComparison.OrdinalIgnoreCase) ? 1.0 :
                             result.Contains("improved", StringComparison.OrdinalIgnoreCase) ? 0.7 : 0.3;
                totalReward += reward;
            }

            var avgReward = selected.Count > 0 ? totalReward / selected.Count : 0;
            return new Dictionary<string, double>
            {
                ["avg_reward"] = avgReward,
                ["improvement"] = avgReward - 0.5,
                ["samples_processed"] = selected.Count
            };
        }, rounds);
    }
}
