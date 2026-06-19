// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  EditLedgerProvider — AIContextProvider for session edit tracking
//
//  Injects the EditLedger summary into the system prompt so the
//  agent always knows what files it has touched in this session.
//
//  Registered at position [after EnvironmentProvider] in the
//  AIContextProvider chain.
// ═══════════════════════════════════════════════════════════════

using System;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Agents.AI;

namespace LTAI.Agent.Memory;

[ToolDomain("memory")]
public sealed class EditLedgerProvider : AIContextProvider
{
    private readonly EditLedger _ledger;
    private readonly int _maxTokens;

    public EditLedgerProvider(EditLedger ledger, int maxTokens = 200)
    {
        _ledger = ledger ?? EditLedger.Default;
        _maxTokens = maxTokens;
    }

    public override IReadOnlyList<string> StateKeys => ["EditLedger"];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var summary = _ledger.GetSummary();
        if (summary == null)
            return ValueTask.FromResult(new AIContext());

        // Truncate to max tokens
        var maxChars = _maxTokens * 4;
        if (summary.Length > maxChars)
            summary = summary[..maxChars] + "...";

        var prompt = "## L2 — Edit Ledger\n<memory>\n" + summary + "\n</memory>";

        return ValueTask.FromResult(new AIContext
        {
            Messages = [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, prompt)],
        });
    }
}
