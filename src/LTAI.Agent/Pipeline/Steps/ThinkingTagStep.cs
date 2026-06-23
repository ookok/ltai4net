using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Post-generation step that checks if the LLM response contains
/// &lt;thinking&gt;...&lt;/thinking&gt; tags. If absent on a long response,
/// injects a system reminder.
/// Formerly an IChatClient wrapper (ThinkingTagValidator); now an IPipelineStep.
/// </summary>
public sealed partial class ThinkingTagStep : IPipelineStep
{
    private readonly ILogger<ThinkingTagStep> _logger;

    [GeneratedRegex(@"<thinking>.*?</thinking>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkingTagPattern();

    public string Name => "ThinkingTag";

    public ThinkingTagStep(ILogger<ThinkingTagStep>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ThinkingTagStep>.Instance;
    }

    public Task<MessageContext> ProcessAsync(MessageContext context)
    {
        var lastMsg = context.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastMsg?.Text == null) return Task.FromResult(context);

        var text = lastMsg.Text;
        if (text.Length < 100 || ThinkingTagPattern().IsMatch(text))
            return Task.FromResult(context);

        lock (context.MessagesLock)
        {
            context.Messages.Add(new ChatMessage(ChatRole.System,
                "[System reminder: Please use <thinking>...</thinking> tags to show your reasoning process.]"));
        }
        _logger.LogDebug("ThinkingTag: injected reminder (response length {Len})", text.Length);
        return Task.FromResult(context);
    }
}
