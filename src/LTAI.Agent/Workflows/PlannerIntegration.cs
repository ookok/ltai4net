using LTAI.Planning;
using LTAI.Planning.Models;
using LTAI.Planning.Planning;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public sealed class PlannerIntegration
{
    private readonly DiffusionPlanner _diffusionPlanner;
    private readonly GtsmPlanner _gtsmPlanner;
    private readonly ILogger<PlannerIntegration> _logger;

    public PlannerIntegration(
        DiffusionPlanner diffusionPlanner,
        GtsmPlanner gtsmPlanner,
        ILogger<PlannerIntegration> logger)
    {
        _diffusionPlanner = diffusionPlanner;
        _gtsmPlanner = gtsmPlanner;
        _logger = logger;
    }

    public async Task<string> PlanAndExecuteAsync(
        string intent, string domain, string task,
        Func<string, string, CancellationToken, Task<string>>? llmCall = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PlannerIntegration: Planning for domain={Domain} intent={Intent}", domain, intent);

        var refinedPlan = await _diffusionPlanner.Refine(intent, domain, llmCall, cancellationToken).ConfigureAwait(false);

        if (refinedPlan.Confidence < 0.5)
        {
            _logger.LogWarning("PlannerIntegration: Diffusion plan confidence low ({Conf:F2}), falling back to GTSM", refinedPlan.Confidence);
            return await ExecuteGtsmPlan(task, domain, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("PlannerIntegration: Diffusion plan ready (conf={Conf:F2}, tools={ToolCount})",
            refinedPlan.Confidence, refinedPlan.ToolsSequence.Count);

        var result = new System.Text.StringBuilder();
        result.AppendLine($"# Plan: {intent}");
        result.AppendLine($"Confidence: {refinedPlan.Confidence:F2}");
        result.AppendLine($"Tools: {string.Join(" → ", refinedPlan.ToolsSequence)}");
        result.AppendLine();
        result.AppendLine(refinedPlan.FinalPlan);

        return result.ToString();
    }

    private async Task<string> ExecuteGtsmPlan(string task, string domain, CancellationToken ct)
    {
        var trajectory = _gtsmPlanner.Plan(task, GTSMMode.Hybrid, domain);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# GTSM Plan ({trajectory.Mode})");
        sb.AppendLine($"Score: {trajectory.TotalScore:F3} | Depth: {trajectory.TreeDepth} | Steps: {trajectory.Steps.Count}");
        sb.AppendLine();

        foreach (var step in trajectory.Steps)
        {
            sb.AppendLine($"{step.Index + 1}. {step.Action} (tool={step.Tool}, conf={step.Confidence:F2}, depth={step.TreeDepth})");
        }

        return sb.ToString();
    }

    public RefinedPlan? GetPlan(string intent, string domain,
        Func<string, string, CancellationToken, Task<string>>? llmCall = null,
        CancellationToken cancellationToken = default)
    {
        return _diffusionPlanner.Refine(intent, domain, llmCall, cancellationToken).GetAwaiter().GetResult();
    }

    public GTSMTrajectory? GetGtsmPlan(string task, GTSMMode mode = GTSMMode.Auto, string domain = "general")
    {
        return _gtsmPlanner.Plan(task, mode, domain);
    }
}
