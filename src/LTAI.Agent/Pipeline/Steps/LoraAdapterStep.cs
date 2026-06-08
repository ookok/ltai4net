// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  LoraAdapterStep — pipeline step that injects Code2LoRA prefix
//
//  Injects repo-specific context prefix into the conversation
//  before RouterStep runs, adapting the agent to the codebase.
//
//  Flow: LoraAdapterStep → RagContextStep → RouterStep → ...
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that injects Code2LoRA context into the message stream.
/// Must be registered before RouterStep to take effect.
///
/// If currentRepoPath is set in MessageContext properties, the step
/// automatically analyzes the repo and injects the context prefix.
/// </summary>
public sealed class LoraAdapterStep : IPipelineStep
{
    private readonly Lora.ICodeLoraAdapter _adapter;
    private readonly ILogger<LoraAdapterStep> _logger;

    public string Name => "LoraAdapter";

    public LoraAdapterStep(
        Lora.ICodeLoraAdapter adapter,
        ILogger<LoraAdapterStep>? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? NullLogger<LoraAdapterStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        // Check if there's a current repo path
        if (!context.TryGet<string>("currentRepoPath", out var repoPath) ||
            string.IsNullOrEmpty(repoPath))
        {
            _logger.LogDebug("LoraAdapterStep: no repo path in context, skipping");
            return context;
        }

        // Check if adapter already injected its prefix
        if (context.TryGet("_loraPrefixInjected", out bool injected) && injected)
        {
            _logger.LogDebug("LoraAdapterStep: prefix already injected, skipping");
            return context;
        }

        try
        {
            var prefix = await ((Lora.CodeLoraAdapter)_adapter)
                .GeneratePrefixAsync(repoPath, context.CancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(prefix))
            {
                // Prepend the prefix as a system message
                context.Messages.Insert(0, new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.System, prefix));

                context.Set("_loraPrefixInjected", true);
                _logger.LogInformation("LoraAdapterStep: injected Code2LoRA prefix for {Repo}", repoPath);
            }
            else
            {
                _logger.LogDebug("LoraAdapterStep: empty prefix for {Repo}, skipping", repoPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LoraAdapterStep: failed to generate prefix (non-fatal)");
        }

        return context;
    }
}
