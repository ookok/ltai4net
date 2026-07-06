using LTAI.Agent.Evolution;
using LTAI.Agent.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class MetaSkillInjectorStep : IPipelineStep
{
    private readonly MetaSkillStore _skillStore;
    private readonly ILogger<MetaSkillInjectorStep> _logger;

    public string Name => "MetaSkillInjector";

    public MetaSkillInjectorStep(
        MetaSkillStore skillStore,
        ILogger<MetaSkillInjectorStep>? logger = null)
    {
        _skillStore = skillStore;
        _logger = logger ?? NullLogger<MetaSkillInjectorStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (context.TryGet<bool>("_MetaSkillInjected", out var injected) && injected)
        {
            _logger.LogDebug("MetaSkillInjectorStep: already injected, skipping");
            return context;
        }

        var skill = await _skillStore.GetLatestAsync(context.CancellationToken).ConfigureAwait(false);

        var metaSkillText = FormatMetaSkillForPrompt(skill);

        lock (context.MessagesLock)
        {
            context.Messages.Add(new ChatMessage(ChatRole.System, metaSkillText));
        }

        context.Set("_MetaSkillInjected", true);
        context.Set("_MetaSkillVersion", skill.Version);

        _logger.LogInformation("MetaSkillInjectorStep: injected v{V} (R{R}) — {Modules}",
            skill.Version, skill.Round, skill.ModuleCountLabel);

        return context;
    }

    private static string FormatMetaSkillForPrompt(Evolution.MetaSkill skill)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Meta-Skill ( Orchestration Principles )");
        sb.AppendLine($"Version: v{skill.Version} (Round {skill.Round})");
        sb.AppendLine();
        sb.AppendLine("以下编排原则指导当前请求的执行策略：");
        sb.AppendLine();

        sb.AppendLine("### Task Decomposition （分解策略）");
        foreach (var p in skill.TaskDecomposition.Principles)
            sb.AppendLine($"- {p}");
        sb.AppendLine();

        sb.AppendLine("### Agent Engineering （智能体指派）");
        foreach (var p in skill.AgentEngineering.Principles)
            sb.AppendLine($"- {p}");
        sb.AppendLine();

        sb.AppendLine("### Workflow Orchestration （工作流编排）");
        foreach (var p in skill.WorkflowOrchestration.Principles)
            sb.AppendLine($"- {p}");

        return sb.ToString();
    }
}
