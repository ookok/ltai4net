using System.Text.Json;
using LTAI.Agent.Pipeline.Steps;

namespace LTAI.Agent.Evolution;

public sealed record ToolCallRecord(
    string Name,
    string Arguments,
    string Result,
    bool Success,
    long DurationMs);

public sealed record TrajectoryData(
    string TaskId,
    string Task,
    int TrajectoryIndex,
    int MetaSkillVersion,
    double Score,
    bool SkillWeaverFastPath,
    IReadOnlyList<string>? Decomposition,
    CompositionPlan? Plan,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    string? ResponseText,
    DateTime CreatedAt)
{
    public static double ComputeScore(
        IReadOnlyList<ToolCallRecord> toolCalls,
        string? responseText,
        bool hasPlanVerificationFailures)
    {
        if (toolCalls.Count == 0)
            return responseText?.Length > 50 ? 0.6 : 0.2;

        var successRate = toolCalls.Count > 0
            ? (double)toolCalls.Count(t => t.Success) / toolCalls.Count
            : 0.0;

        var diversity = Math.Min(1.0,
            toolCalls.Select(t => t.Name).Distinct().Count() / 5.0);

        var lengthQuality = responseText?.Length > 0
            ? Math.Min(1.0, responseText.Length / 500.0)
            : 0.0;

        var penalty = hasPlanVerificationFailures ? 0.2 : 0.0;

        return Math.Clamp(
            0.50 * successRate +
            0.20 * diversity +
            0.15 * lengthQuality -
            penalty,
            0.0, 1.0);
    }
}
