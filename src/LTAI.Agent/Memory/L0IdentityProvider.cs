using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class L0IdentityProvider : AIContextProvider
{
    private const int MaxTokens = 100;
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

        var prompt = "## L0 — Identity\n" + _identityText;

        if (prompt.Length / 4 > MaxTokens)
            prompt = prompt[..(MaxTokens * 4)] + "...";

        return ValueTask.FromResult(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.User, prompt)],
        });
    }
}
