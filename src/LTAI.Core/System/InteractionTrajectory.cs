namespace LTAI.Core.System;

public enum DisclosureAction
{
    Think,
    Speak
}

public sealed record ToolCall(
    string ToolName,
    Dictionary<string, object> Parameters,
    object? Result = null,
    double LatencyMs = 0);

public sealed record AgentStep(
    int StepIndex,
    string Thought,
    ToolCall? Action = null,
    string? Observation = null,
    double Reward = 0,
    double StepLatencyMs = 0,
    DisclosureAction Disclosure = DisclosureAction.Think);

public sealed record DisclosureActionResult(
    int StepIndex,
    DisclosureAction Action,
    string Content,
    double EntailmentScore,
    bool IsDisclosed);

public sealed record ContentLatencySnapshot(
    double ARI,
    double ABO,
    double AIRW,
    int TotalTokens,
    int SpeakTokenCount,
    int ThinkTokenCount,
    int SpeakBlockCount,
    double DisclosureRatio)
{
    public static ContentLatencySnapshot FromSteps(List<AgentStep> steps)
    {
        var speakIndices = new List<int>();
        var blockStarts = new List<int>();
        var thinkSpans = new List<int>();
        int currentThinkSpan = 0;
        bool inSpeak = false;

        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Disclosure == DisclosureAction.Speak)
            {
                speakIndices.Add(i + 1);
                if (!inSpeak)
                {
                    blockStarts.Add(i + 1);
                    if (currentThinkSpan > 0)
                        thinkSpans.Add(currentThinkSpan);
                    currentThinkSpan = 0;
                    inSpeak = true;
                }
            }
            else
            {
                if (inSpeak)
                {
                    inSpeak = false;
                    currentThinkSpan = 0;
                }
                currentThinkSpan++;
            }
        }

        if (currentThinkSpan > 0)
            thinkSpans.Add(currentThinkSpan);

        double ari = speakIndices.Count > 0 ? speakIndices.Average() : 0;
        double abo = blockStarts.Count > 0 ? blockStarts.Average() : 0;
        double airw = thinkSpans.Count > 0 ? thinkSpans.Average() : 0;

        int totalTokens = steps.Sum(s => s.Thought.Length + (s.Observation?.Length ?? 0));
        int speakTokens = steps.Where(s => s.Disclosure == DisclosureAction.Speak)
            .Sum(s => s.Thought.Length + (s.Observation?.Length ?? 0));
        int thinkTokens = totalTokens - speakTokens;
        double disclosureRatio = totalTokens > 0 ? (double)speakTokens / totalTokens : 0;

        return new ContentLatencySnapshot(
            ari, abo, airw, totalTokens,
            speakTokens, thinkTokens,
            blockStarts.Count, disclosureRatio);
    }
}

public sealed record InteractionTrajectory(
    string TrajectoryId,
    string TaskDescription,
    List<AgentStep> Steps,
    double TotalReward,
    bool Completed,
    double ElapsedMs,
    string? SessionId = null,
    Dictionary<string, object>? Metadata = null)
{
    public int StepCount => Steps.Count;

    public double[] RewardPerStep()
    {
        return Steps.Select(s => s.Reward).ToArray();
    }

    public ContentLatencySnapshot LatencySnapshot => ContentLatencySnapshot.FromSteps(Steps);

    public InteractionTrajectory Slice(int startStep, int? endStep = null)
    {
        var end = endStep ?? Steps.Count;
        var slicedSteps = Steps.Skip(startStep).Take(end - startStep).ToList();
        return this with
        {
            Steps = slicedSteps,
            TotalReward = slicedSteps.Sum(s => s.Reward),
            Completed = end >= Steps.Count && Completed
        };
    }

    public static InteractionTrajectory Empty(string taskDescription)
        => new(Guid.NewGuid().ToString("N")[..12], taskDescription, new(), 0, false, 0);
}

public sealed record TrajectoryBatch(
    List<InteractionTrajectory> Trajectories,
    int BatchSize,
    double AvgReward,
    double AvgSteps,
    DateTimeOffset CollectedAt);

public sealed record RolloutConfig(
    int MaxSteps = 30,
    int NumTrajectories = 16,
    double Temperature = 0.7,
    int MaxConcurrent = 8,
    bool EnablePartialRollout = true,
    int PartialRolloutSteps = 5,
    bool SaveCheckpoints = true,
    double TimeoutSeconds = 300);

public sealed record GRPOTrainingResult(
    double AvgReward,
    double BestReward,
    double PolicyLoss,
    double ValueLoss,
    int TrajectoriesUsed,
    int StepsCompleted,
    double TrainingTimeMs,
    Dictionary<string, object> Metrics)
{
    public double AvgCAS => Metrics.TryGetValue("avg_cas", out var v) && v is double d ? d : 0;
    public double TotalTokenCost => Metrics.TryGetValue("total_tokens", out var v) && v is double d ? d : 0;
    public int TotalToolRounds => Metrics.TryGetValue("total_tool_rounds", out var v) && v is int i ? i : 0;
    public double CostEfficiency => TotalTokenCost > 0 ? Math.Round(AvgReward * 100 / TotalTokenCost, 3) : 0;
}
