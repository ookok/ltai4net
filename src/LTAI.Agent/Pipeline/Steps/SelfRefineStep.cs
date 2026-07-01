using LTAI.Agent.Learning;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

public sealed class SelfRefineStep : IPipelineStep
{
    private readonly SelfCritiqueGenerator _critiqueGenerator;
    private readonly ILogger<SelfRefineStep> _logger;
    private readonly int _maxRefineIterations;

    public string Name => "SelfRefine";

    public SelfRefineStep(
        SelfCritiqueGenerator critiqueGenerator,
        ILogger<SelfRefineStep>? logger = null,
        int? maxRefineIterations = null)
    {
        _critiqueGenerator = critiqueGenerator;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SelfRefineStep>.Instance;
        _maxRefineIterations = maxRefineIterations ?? EnvironmentConfig.SelfRefineMaxIter;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (context.AbstentionBlocked || context.SafetyBlocked)
            return context;

        var lastMsg = context.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastMsg?.Text == null) return context;

        var hasBlockers = context.GrammarCheckBlocked
            || context.AntiPatternBlocked
            || context.QualityGateBlocked
            || context.DoDBlocked;

        if (!hasBlockers) return context;

        var currentText = lastMsg.Text;
        var toolResults = context.ToolCalls
            .Select(c => $"{c.Name}: {c.Result?[..Math.Min(c.Result.Length, 300)]}")
            .ToList();

        for (int iteration = 0; iteration < _maxRefineIterations; iteration++)
        {
            var critiques = await _critiqueGenerator.GenerateCritiqueAsync(
                context.Request, currentText, toolResults, context.CancellationToken)
                .ConfigureAwait(false);

            if (!_critiqueGenerator.HasSignificantIssues(critiques))
            {
                _logger.LogDebug("SelfRefine: no significant issues in iteration {Iter}", iteration + 1);
                break;
            }

            var refinePrompt = _critiqueGenerator.BuildRefinePrompt(
                context.Request, currentText, critiques);

            lock (context.MessagesLock)
            {
                context.Messages.Add(new ChatMessage(ChatRole.System,
                    "[Self-Refine: Generating refined response based on self-critique]"));
            }

            _logger.LogInformation("SelfRefine: iteration {Iter}, {Count} issues", iteration + 1, critiques.Count);

            var refineMsg = context.Messages
                .Concat([new ChatMessage(ChatRole.User, refinePrompt)])
                .ToList();

            var llm = FindLlmClient(context);
            if (llm == null) break;

            try
            {
                var response = await llm.GetResponseAsync(refineMsg, null, context.CancellationToken)
                    .ConfigureAwait(false);
                var newText = response.Text;
                if (string.IsNullOrWhiteSpace(newText)) break;

                lock (context.MessagesLock)
                {
                    var idx = context.Messages.FindLastIndex(m => m.Role == ChatRole.Assistant && m.Text == currentText);
                    if (idx >= 0)
                        context.Messages[idx] = new ChatMessage(ChatRole.Assistant, newText);
                }
                currentText = newText;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SelfRefine iteration {Iter} failed", iteration + 1);
                break;
            }
        }

        context.Set("SelfRefineIterations", _maxRefineIterations);
        return context;
    }

    private static IChatClient? FindLlmClient(MessageContext context)
    {
        return context.ExecutionEngine as IChatClient;
    }
}
