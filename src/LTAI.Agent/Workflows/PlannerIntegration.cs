using LTAI.Agent.Skills;
using LTAI.Planning;
using LTAI.Planning.HTN;
using LTAI.Planning.Models;
using LTAI.Planning.Planning;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Workflows;

public sealed class PlannerIntegration
{
    private readonly DiffusionPlanner _diffusionPlanner;
    private readonly GtsmPlanner _gtsmPlanner;
    private readonly HTNPlanner _htnPlanner;
    private readonly SkillRegistry? _skillRegistry;
    private readonly ILogger<PlannerIntegration> _logger;

    public PlannerIntegration(
        DiffusionPlanner diffusionPlanner,
        GtsmPlanner gtsmPlanner,
        HTNPlanner htnPlanner,
        ILogger<PlannerIntegration> logger,
        SkillRegistry? skillRegistry = null)
    {
        _diffusionPlanner = diffusionPlanner;
        _gtsmPlanner = gtsmPlanner;
        _htnPlanner = htnPlanner;
        _logger = logger;
        _skillRegistry = skillRegistry;
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
            _logger.LogWarning("PlannerIntegration: Diffusion plan confidence low ({Conf:F2}), trying HTN", refinedPlan.Confidence);

            var htnPlan = ExecuteHtnPlan(task, domain);
            if (htnPlan != null)
            {
                _logger.LogInformation("PlannerIntegration: HTN plan used (template found)");
                return htnPlan;
            }

            _logger.LogWarning("PlannerIntegration: HTN plan insufficient, falling back to GTSM");
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

    private string? ExecuteHtnPlan(string task, string domain)
    {
        var tools = new List<string> { "filesystem", "shell", "http", "code", "git", "search", "math", "text" };

        if (_skillRegistry != null)
        {
            var skills = _skillRegistry.GetByDomain(domain);
            if (skills.Count > 0)
            {
                var skillPlan = BuildSkillPlan(task, domain, skills);
                if (skillPlan != null) return skillPlan;
            }
        }

        var root = _htnPlanner.DecomposeTask(task, domain, tools);

        if (root.Children.Count == 0 && root.ToolCalls.Count == 0)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# HTN Plan ({domain})");
        FlattenPlanNode(root, sb, 0);
        return sb.ToString();
    }

    private string? BuildSkillPlan(string task, string domain, List<LTAI.Models.Skill> skills)
    {
        var relevant = skills.Where(s => s.IsActive && s.Triggers.Any(t =>
            task.Contains(t.Pattern, StringComparison.OrdinalIgnoreCase))).ToList();

        if (relevant.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Skill-guided Plan ({domain})");

        foreach (var skill in relevant.OrderByDescending(s => s.Confidence))
        {
            sb.AppendLine($"## {skill.Name} (conf={skill.Confidence:F2})");
            foreach (var step in skill.Steps)
            {
                var action = step.SkillRef != null ? $"→ {step.SkillRef} {step.Action}" : step.Action;
                sb.AppendLine($"{step.Index}. {action}");
            }

            foreach (var dep in skill.Requires)
                sb.AppendLine($"   depends: {dep}");
        }

        return sb.ToString();
    }

    private static void FlattenPlanNode(PlanNode node, System.Text.StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);

        if (!string.IsNullOrEmpty(node.Name))
        {
            var typeLabel = node.Type switch
            {
                PlanNodeType.Parallel => "[P]",
                PlanNodeType.Decision => "[?]",
                PlanNodeType.ToolCall => "[T]",
                _ => ""
            };
            sb.AppendLine($"{indent}{node.Children.Count + 1}. {typeLabel} {node.Name}");

            if (!string.IsNullOrEmpty(node.Description))
                sb.AppendLine($"{indent}   {node.Description}");

            if (node.ToolCalls.Count > 0)
                sb.AppendLine($"{indent}   tools: {string.Join(", ", node.ToolCalls)}");
        }

        foreach (var child in node.Children)
            FlattenPlanNode(child, sb, depth + 1);
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
