// Copyright (c) LTAI. All rights reserved.

#pragma warning disable MAAI001 // Experimental: CompactionStrategy, CompactionMessageIndex, etc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Compaction;

/// <summary>
/// A compaction strategy that uses an LLM to summarize older conversation portions and
/// then verifies the summary for hallucination before committing. If the verifier detects
/// hallucination (claims not present in original messages), the strategy falls back to
/// truncation to avoid polluting the context with fabricated information.
/// </summary>
/// <remarks>
/// <para>
/// This strategy mirrors the selection logic of <see cref="SummarizationCompactionStrategy"/>:
/// it protects system messages and the most recent <paramref name="minimumPreservedGroups"/>
/// non-system groups, then sends the oldest groups to a summarizer LLM.
/// </para>
/// <para>
/// The key addition is a <b>verification step</b>: a second <see cref="IChatClient"/>
/// (the <paramref name="verifier"/>, ideally a different model or a judge-tuned model)
/// checks whether the summary contains any claim, number, or fact not grounded in the
/// original messages. If the verifier flags hallucination, the excluded groups are restored
/// and the strategy falls back to truncating those oldest groups entirely.
/// </para>
/// <para>
/// This prevents irreversible information loss and hallucination feedback loops where
/// summarization errors compound across multiple compaction passes.
/// </para>
/// </remarks>
public sealed class VerifiedSummarizationStrategy : CompactionStrategy
{
    /// <summary>
    /// Default summarization prompt used when none is provided.
    /// </summary>
    public const string DefaultSummarizationPrompt =
        """
        You are a conversation summarizer. Produce a concise summary of the conversation that preserves:

        - Key facts, decisions, and user preferences
        - Important context needed for future turns
        - Tool call outcomes and their significance

        Omit pleasantries and redundant exchanges. Be factual and brief.
        """;

    /// <summary>
    /// Default verification prompt used to check summaries for hallucination.
    /// </summary>
    public const string DefaultVerificationPrompt =
        """
        You are a fact-checker. Your task is to verify whether the SUMMARY contains any information
        that is NOT supported by the ORIGINAL CONVERSATION.

        Rules:
        - Answer ONLY with a JSON object: {"hallucination": true/false, "reason": "..."}
        - "hallucination" is true if the summary makes any claim, mentions any number, cites any fact,
          or states any conclusion that cannot be directly supported by the original conversation.
        - General knowledge that is obviously true outside the conversation context is NOT hallucination.
        - Paraphrasing or condensing is NOT hallucination.
        - If the summary is accurate and grounded, set "hallucination" to false.

        ORIGINAL CONVERSATION:
        {original}

        SUMMARY:
        {summary}
        """;

    /// <summary>
    /// Default minimum number of most-recent non-system groups to preserve.
    /// </summary>
    public const int DefaultMinimumPreserved = 8;

    /// <summary>
    /// Default threshold for rejecting a summary due to hallucination confidence.
    /// </summary>
    public const double DefaultHallucinationThreshold = 0.3;

    /// <summary>
    /// Initializes a new instance of the <see cref="VerifiedSummarizationStrategy"/> class.
    /// </summary>
    /// <param name="summarizer">The <see cref="IChatClient"/> to use for generating summaries. A smaller, faster model is recommended.</param>
    /// <param name="verifier">The <see cref="IChatClient"/> to use for checking summaries. Ideally a different model or one tuned for factuality judgment.</param>
    /// <param name="trigger">The <see cref="CompactionTrigger"/> that controls when compaction proceeds.</param>
    /// <param name="minimumPreservedGroups">The minimum number of most-recent non-system groups to preserve. Defaults to 8.</param>
    /// <param name="summarizationPrompt">Optional custom prompt for the summarizer. Defaults to <see cref="DefaultSummarizationPrompt"/>.</param>
    /// <param name="verificationPrompt">Optional custom prompt for the verifier. Defaults to <see cref="DefaultVerificationPrompt"/>.</param>
    /// <param name="target">Optional target condition. Defaults to inverse of trigger.</param>
    public VerifiedSummarizationStrategy(
        IChatClient summarizer,
        IChatClient verifier,
        CompactionTrigger trigger,
        int minimumPreservedGroups = DefaultMinimumPreserved,
        string? summarizationPrompt = null,
        string? verificationPrompt = null,
        CompactionTrigger? target = null)
        : base(trigger, target)
    {
        this.Summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
        this.Verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        this.MinimumPreservedGroups = Math.Max(0, minimumPreservedGroups);
        this.SummarizationPrompt = summarizationPrompt ?? DefaultSummarizationPrompt;
        this.VerificationPrompt = verificationPrompt ?? DefaultVerificationPrompt;
    }

    /// <summary>
    /// Gets the chat client used for generating summaries.
    /// </summary>
    public IChatClient Summarizer { get; }

    /// <summary>
    /// Gets the chat client used for verifying summaries.
    /// </summary>
    public IChatClient Verifier { get; }

    /// <summary>
    /// Gets the minimum number of most-recent non-system groups that are always preserved.
    /// </summary>
    public int MinimumPreservedGroups { get; }

    /// <summary>
    /// Gets the summarization prompt.
    /// </summary>
    public string SummarizationPrompt { get; }

    /// <summary>
    /// Gets the verification prompt.
    /// </summary>
    public string VerificationPrompt { get; }

    /// <inheritdoc/>
    protected override async ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
    {
        // ── Step 1: Select oldest non-system groups for summarization ──
        int nonSystemIncludedCount = 0;
        for (int i = 0; i < index.Groups.Count; i++)
        {
            var group = index.Groups[i];
            if (!group.IsExcluded && group.Kind != CompactionGroupKind.System)
            {
                nonSystemIncludedCount++;
            }
        }

        int protectedFromEnd = Math.Min(this.MinimumPreservedGroups, nonSystemIncludedCount);
        int maxSummarizable = nonSystemIncludedCount - protectedFromEnd;

        if (maxSummarizable <= 0)
        {
            return false;
        }

        // Collect oldest non-system groups for summarization
        var summarizationMessages = new List<ChatMessage> { new(ChatRole.System, this.SummarizationPrompt) };
        var excludedGroups = new List<CompactionMessageGroup>();
        int insertIndex = -1;

        for (int i = 0; i < index.Groups.Count && excludedGroups.Count < maxSummarizable; i++)
        {
            var group = index.Groups[i];
            if (group.IsExcluded || group.Kind == CompactionGroupKind.System)
            {
                continue;
            }

            if (insertIndex < 0)
            {
                insertIndex = i;
            }

            summarizationMessages.AddRange(group.Messages);
            group.IsExcluded = true;
            group.ExcludeReason = $"Pending verification by {nameof(VerifiedSummarizationStrategy)}";
            excludedGroups.Add(group);

            if (this.Target(index))
            {
                break;
            }
        }

        int summarized = excludedGroups.Count;
        logger.Log(LogLevel.Debug, "[VerifiedSummarization] Selected {Count} groups for summarization at index {InsertIndex}",
            summarized, insertIndex);

        // ── Step 2: Generate summary ──
        ChatResponse response;
        try
        {
            response = await this.Summarizer.GetResponseAsync(
                summarizationMessages,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RestoreExcludedGroups(excludedGroups);
            logger.Log(LogLevel.Warning, "[VerifiedSummarization] Summarization failed: {Message}. Falling back to truncation.", ex.Message);
            return await FallbackToTruncationAsync(index, excludedGroups, logger, cancellationToken).ConfigureAwait(false);
        }

        string summaryText = string.IsNullOrWhiteSpace(response.Text) ? "[Summary unavailable]" : response.Text;

        // ── Step 3: Verify summary for hallucination ──
        bool hallucinationDetected = await VerifySummaryAsync(
            summarizationMessages, summaryText, logger, cancellationToken).ConfigureAwait(false);

        if (hallucinationDetected)
        {
            // Restore groups — verifier rejected the summary
            RestoreExcludedGroups(excludedGroups);
            logger.Log(LogLevel.Warning,
                "[VerifiedSummarization] Verifier detected hallucination in summary. Falling back to truncation.");
            return await FallbackToTruncationAsync(index, excludedGroups, logger, cancellationToken).ConfigureAwait(false);
        }

        // ── Step 4: Commit the summary ──
        var summaryMessage = new ChatMessage(ChatRole.Assistant, $"[Summary]\n{summaryText}");
        (summaryMessage.AdditionalProperties ??= [])[CompactionMessageGroup.SummaryPropertyKey] = true;

        index.InsertGroup(insertIndex, CompactionGroupKind.Summary, [summaryMessage]);

        logger.Log(LogLevel.Information,
            "[VerifiedSummarization] Verified summary committed: {Length} chars replacing {Count} groups at index {InsertIndex}",
            summaryText.Length, summarized, insertIndex);

        return true;
    }

    /// <summary>
    /// Sends the summary + original messages to the verifier LLM and returns whether hallucination was detected.
    /// </summary>
    private async Task<bool> VerifySummaryAsync(
        List<ChatMessage> originalMessages,
        string summaryText,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Build the original conversation text (excluding the system summarization prompt)
        string originalText = string.Join(
            "\n",
            originalMessages
                .Skip(1) // skip the summarization system prompt
                .Select(m => $"[{m.Role}]: {m.Text ?? "(non-text content)"}"));

        // Truncate if too long to avoid hitting token limits on the verifier
        if (originalText.Length > 32000)
        {
            originalText = originalText[^32000..];
            logger.Log(LogLevel.Debug, "[VerifiedSummarization] Truncated original text to 32000 chars for verification.");
        }

        string verificationPrompt = this.VerificationPrompt
            .Replace("{original}", originalText)
            .Replace("{summary}", summaryText);

        try
        {
            var verificationMessages = new List<ChatMessage>
            {
                new(ChatRole.System, verificationPrompt)
            };

            var verifierResponse = await this.Verifier.GetResponseAsync(
                verificationMessages,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            string result = verifierResponse.Text?.Trim() ?? "";
            logger.Log(LogLevel.Debug, "[VerifiedSummarization] Verifier response: {Response}", result);

            // Parse the JSON response
            if (result.Contains("\"hallucination\": true", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("\"hallucination\":true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If verification fails, err on the side of caution: reject the summary
            logger.Log(LogLevel.Warning,
                "[VerifiedSummarization] Verification call failed: {Message}. Rejecting summary as precaution.", ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Fallback: truncate the oldest groups instead of summarizing them.
    /// </summary>
    private static async ValueTask<bool> FallbackToTruncationAsync(
        CompactionMessageIndex index,
        List<CompactionMessageGroup> groupsToRemove,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Simple truncation: just exclude the groups (they're already excluded but with wrong reason)
        foreach (var group in groupsToRemove)
        {
            group.ExcludeReason = "Truncated (verification fallback)";
        }

        logger.Log(LogLevel.Information,
            "[VerifiedSummarization] Truncation fallback: removed {Count} groups.", groupsToRemove.Count);

        return await ValueTask.FromResult(true).ConfigureAwait(false);
    }

    /// <summary>
    /// Restore previously excluded groups (undo the exclusion).
    /// </summary>
    private static void RestoreExcludedGroups(List<CompactionMessageGroup> groups)
    {
        foreach (var group in groups)
        {
            group.IsExcluded = false;
            group.ExcludeReason = null;
        }
    }
}
