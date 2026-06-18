using LTAI.Agent.Context;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.LanguageServer;

/// <summary>
/// AIContextProvider that injects live LSP diagnostics into agent context.
/// Provides real-time diagnostic feedback for MoonBit/Mojo/Cangjie files
/// without requiring a build step.
/// </summary>
public sealed class LspDiagnosticsProvider : AIContextProvider
{
    private readonly LspLanguageManager _lsp;

    public LspDiagnosticsProvider(LspLanguageManager lsp)
        : base(null, null, null)
    {
        _lsp = lsp;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        if (context.AIContext.IsProviderSkipped("LspDiagnosticsProvider"))
            return ValueTask.FromResult(context.AIContext ?? new AIContext());
        LookaheadProviderSelector.RecordProviderUsed("LspDiagnosticsProvider");

        var ctx = context.AIContext;
        var diagnostics = _lsp.FormatDiagnostics();
        if (string.IsNullOrEmpty(diagnostics))
            return ValueTask.FromResult(ctx);

        // Prepend diagnostics as a system-level context before user messages
        var existingMessages = ctx.Messages?.ToList() ?? [];
        var diagMsg = new ChatMessage(ChatRole.System, diagnostics);
        existingMessages.Insert(0, diagMsg);

        return ValueTask.FromResult(new AIContext
        {
            Messages = existingMessages,
        });
    }
}
