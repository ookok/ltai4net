using System.Text.Json;
using LTAI.Agent.Evolution;
using LTAI.Agent.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class MultiTrajectoryRolloutStep : IPipelineStep
{
    private readonly IChatClient? _llm;
    private readonly MetaSkillStore _skillStore;
    private readonly ILogger<MultiTrajectoryRolloutStep> _logger;

    public string Name => "MultiTrajectoryRollout";

    private const int DefaultK = 5;
    private const double MinScoreSpread = 0.05;

    public MultiTrajectoryRolloutStep(
        MetaSkillStore skillStore,
        IChatClient? llm = null,
        ILogger<MultiTrajectoryRolloutStep>? logger = null)
    {
        _skillStore = skillStore;
        _llm = llm;
        _logger = logger ?? NullLogger<MultiTrajectoryRolloutStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var mode = context.TryGet<string>("_MultiTrajectoryMode", out var m) ? m : "";
        if (mode != "collect" && mode != "rollout")
        {
            _logger.LogDebug("MultiTrajectoryRolloutStep: not in rollout mode, skipping");
            return context;
        }

        var query = context.Request;
        if (string.IsNullOrWhiteSpace(query))
        {
            _logger.LogDebug("MultiTrajectoryRolloutStep: empty request, skipping");
            return context;
        }

        var k = context.TryGet<int>("_MultiTrajectoryK", out var kVal) ? Math.Clamp(kVal, 2, 10) : DefaultK;
        var skill = await _skillStore.GetLatestAsync(context.CancellationToken).ConfigureAwait(false);

        _logger.LogInformation("MultiTrajectoryRolloutStep: rolling out K={K} trajectories for '{Query}'", k, Truncate(query, 60));

        var trajectories = new List<TrajectoryData>(k);

        if (_llm == null)
        {
            _logger.LogDebug("MultiTrajectoryRolloutStep: no LLM available, using heuristic fallback");
            trajectories.AddRange(HeuristicRollout(query, k, skill));
        }
        else
        {
            var semaphore = new SemaphoreSlim(3, 3);
            var tasks = new Task<TrajectoryData?>[k];
            for (int i = 0; i < k; i++)
            {
                var idx = i;
                tasks[i] = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                    try
                    {
                        return await RolloutSingleAsync(query, idx, k, skill, context.CancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally { semaphore.Release(); }
                }, context.CancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var t in tasks)
            {
                if (t.Result != null)
                    trajectories.Add(t.Result);
            }
        }

        if (trajectories.Count < 2)
        {
            _logger.LogDebug("MultiTrajectoryRolloutStep: insufficient trajectories ({N})", trajectories.Count);
            return context;
        }

        var meanScore = trajectories.Average(t => t.Score);
        var stdDev = Math.Sqrt(trajectories.Average(t => Math.Pow(t.Score - meanScore, 2)));
        var difficulty = Math.Clamp(1.0 - meanScore, 0.0, 1.0);
        var uncertainty = Math.Clamp(stdDev * 2.0, 0.0, 1.0);

        var result = new MultiTrajectoryResult
        {
            Query = query,
            Trajectories = trajectories,
            K = trajectories.Count,
            MeanScore = meanScore,
            StdDev = stdDev,
            Difficulty = difficulty,
            Uncertainty = uncertainty,
            Priority = 0.5 * difficulty + 0.5 * uncertainty,
        };

        context.Set("_MultiTrajectoryResult", result);

        _logger.LogInformation(
            "MultiTrajectoryRolloutStep: K={K} μ={Mean:F3} σ={Std:F3} diff={Diff:F3} uncert={Uncert:F3} priority={P:F3}",
            trajectories.Count, meanScore, stdDev, difficulty, uncertainty, result.Priority);

        return context;
    }

    private async Task<TrajectoryData?> RolloutSingleAsync(
        string query, int idx, int k, Evolution.MetaSkill skill, CancellationToken ct)
    {
        try
        {
            var tdStr = string.Join("\n", skill.TaskDecomposition.Principles.Select(p => $"  - {p}"));

            var prompt = $"""
                给定编排原则：
                {tdStr}

                用户请求：{query}

                请输出对该请求的任务分解结果。
                格式：每行一个子任务，用 "- " 开头。
                确保子任务是原子性的，每个子任务只需一个工具调用。
                """;

            var response = await _llm!.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions { Temperature = 0.7f + (idx * 0.05f), MaxOutputTokens = 512 },
                ct).ConfigureAwait(false);

            var text = response.Text ?? "";
            var decomposition = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.TrimStart().StartsWith("- "))
                .Select(l => l.TrimStart().TrimStart('-', ' ').Trim())
                .Where(l => l.Length > 0)
                .ToList();

            var score = EstimateScore(decomposition, text);

            return new TrajectoryData(
                TaskId: NormalizeForGrouping(query),
                Task: query,
                TrajectoryIndex: idx,
                MetaSkillVersion: skill.Version,
                Score: score,
                SkillWeaverFastPath: decomposition.Count <= 2,
                Decomposition: decomposition.Count > 0 ? decomposition : null,
                Plan: null,
                ToolCalls: [],
                ResponseText: text,
                CreatedAt: DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MultiTrajectoryRolloutStep: rollout #{Idx} failed", idx);
            return null;
        }
    }

    private static List<TrajectoryData> HeuristicRollout(
        string query, int k, Evolution.MetaSkill skill)
    {
        var results = new List<TrajectoryData>(k);
        var tdCount = skill.TaskDecomposition.Principles.Count;

        for (int i = 0; i < k; i++)
        {
            var score = 0.3 + (i * 0.1) + (tdCount * 0.02);
            var decomposition = new List<string>
            {
                $"Analyze: {Truncate(query, 40)}",
                $"Execute: {Truncate(query, 40)}",
                $"Verify: {Truncate(query, 40)}",
            };

            results.Add(new TrajectoryData(
                TaskId: NormalizeForGrouping(query),
                Task: query,
                TrajectoryIndex: i,
                MetaSkillVersion: skill.Version,
                Score: Math.Clamp(score + (Random.Shared.NextDouble() - 0.5) * 0.2, 0.0, 1.0),
                SkillWeaverFastPath: false,
                Decomposition: decomposition,
                Plan: null,
                ToolCalls: [],
                ResponseText: null,
                CreatedAt: DateTime.UtcNow));
        }
        return results;
    }

    private static double EstimateScore(List<string> decomposition, string responseText)
    {
        if (decomposition.Count == 0)
            return responseText.Length > 100 ? 0.4 : 0.2;

        var atomicity = Math.Min(1.0, decomposition.Count / 5.0);
        var responseQuality = responseText.Length > 50 ? Math.Min(1.0, responseText.Length / 300.0) : 0.3;

        return Math.Clamp(0.6 * atomicity + 0.4 * responseQuality, 0.0, 1.0);
    }

    private static string NormalizeForGrouping(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var cleaned = string.Join(" ", text
            .ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', ',', '.', '!', '?', '：', '，', '。', '！', '？'],
                StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}

public sealed record MultiTrajectoryResult
{
    public string Query { get; init; } = "";
    public IReadOnlyList<TrajectoryData> Trajectories { get; init; } = [];
    public int K { get; init; }
    public double MeanScore { get; init; }
    public double StdDev { get; init; }
    public double Difficulty { get; init; }
    public double Uncertainty { get; init; }
    public double Priority { get; init; }
}
