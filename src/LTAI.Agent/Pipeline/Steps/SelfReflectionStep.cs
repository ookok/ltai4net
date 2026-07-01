using LTAI.Agent.Learning;
using LTAI.Agent.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class SelfReflectionStep : IPipelineStep
{
    private readonly ReflectionGenerator _reflectionGenerator;
    private readonly ReflectionStore _reflectionStore;
    private readonly ILogger<SelfReflectionStep> _logger;

    public string Name => "SelfReflection";

    public SelfReflectionStep(
        ReflectionGenerator reflectionGenerator,
        ReflectionStore reflectionStore,
        ILogger<SelfReflectionStep>? logger = null)
    {
        _reflectionGenerator = reflectionGenerator;
        _reflectionStore = reflectionStore;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SelfReflectionStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var hasFailure = context.GrammarCheckBlocked
            || context.AntiPatternBlocked
            || context.QualityGateBlocked
            || context.DoDBlocked
            || context.AbstentionBlocked
            || context.PipelineError != null;

        if (!hasFailure)
            return context;

        var lastMsg = context.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastMsg?.Text == null) return context;

        var failureReasons = new List<string>();
        if (context.GrammarCheckBlocked) failureReasons.Add("Grammar check failed");
        if (context.AntiPatternBlocked) failureReasons.Add("Anti-pattern detected");
        if (context.QualityGateBlocked) failureReasons.Add("Quality gate not passed");
        if (context.DoDBlocked) failureReasons.Add("Definition of Done not met");
        if (context.AbstentionBlocked) failureReasons.Add("Agentic abstention triggered");
        if (context.PipelineError != null) failureReasons.Add("Pipeline error: " + context.PipelineError);

        var reflection = await _reflectionGenerator.GenerateReflectionAsync(
            context.Request,
            lastMsg.Text,
            string.Join("; ", failureReasons),
            context.CancellationToken).ConfigureAwait(false);

        if (reflection.HasContent)
        {
            var agentId = "LTAI-Dev";
            if (context.TryGet<string>("AgentId", out var id) && id != null)
                agentId = id;

            await _reflectionStore.StoreReflectionAsync(agentId, reflection, context.CancellationToken)
                .ConfigureAwait(false);

            context.Set("GeneratedReflection", reflection);
            _logger.LogInformation("SelfReflection: stored reflection for agent '{Agent}'", agentId);
        }

        return context;
    }
}
