using LTAI.Agent.Memory;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class ReflectionAugmentedStep : IPipelineStep
{
    private readonly ReflectionStore _reflectionStore;
    private readonly ILogger<ReflectionAugmentedStep> _logger;

    public string Name => "ReflectionAugmented";

    public ReflectionAugmentedStep(
        ReflectionStore reflectionStore,
        ILogger<ReflectionAugmentedStep>? logger = null)
    {
        _reflectionStore = reflectionStore;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReflectionAugmentedStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Request))
            return context;

        try
        {
            var reflections = await _reflectionStore.RetrieveRelevantReflectionsAsync(
                context.Request, topK: 3, ct: context.CancellationToken)
                .ConfigureAwait(false);

            if (reflections.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("## Past Reflections (may be relevant)");
                foreach (var reflection in reflections)
                {
                    var trimmed = reflection.Length > 600 ? reflection[..600] + "..." : reflection;
                    sb.AppendLine("<reflection>");
                    sb.AppendLine(trimmed);
                    sb.AppendLine("</reflection>");
                }

                lock (context.MessagesLock)
                    context.Messages.Add(new ChatMessage(ChatRole.System, sb.ToString()));

                context.Set("ReflectionCount", reflections.Count);
                _logger.LogInformation("ReflectionAugmented: injected {Count} reflections", reflections.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReflectionAugmentedStep failed");
        }

        return context;
    }
}
