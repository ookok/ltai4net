using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L0IdentityProvider : AIContextProvider
{
    private readonly string _identityText;

    public L0IdentityProvider(string identityText)
    {
        _identityText = identityText;
    }

    public override IReadOnlyList<string> StateKeys => ["L0Identity"];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_identityText))
            return ValueTask.FromResult(new AIContext());

        var prompt = "## L0 — Identity\n<memory>\n" + _identityText + "\n</memory>";

        var maxChars = MemoryBudget.L0MaxTokens * 4;
        if (prompt.Length > maxChars)
            prompt = prompt[..maxChars] + "...";

        return ValueTask.FromResult(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, prompt)],
        });
    }
}
