using LTAI.AI.Governors;
using LTAI.AI.Utilities;
using LTAI.Core.Execution;
using LTAI.Core.Models;
using LTAI.Core.System;
using LTAI.DNA;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace LTAI.MAF;

public static class LTAIMiddleware
{
    public static AIAgent WithLTAIGovernance(this AIAgent agent, IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<LTAIAgent>>();
        var journal = services.GetRequiredService<TaskJournal>();
        var dna = services.GetService<DNAOrchestrator>();
        var guardian = services.GetRequiredService<SystemGuardian>();

        return agent.AsBuilder()
            .Use(
                runFunc: (messages, session, options, innerAgent, ct) =>
                    AgentRunWithGovernance(messages, session, options, innerAgent, journal, guardian, dna, logger, ct),
                runStreamingFunc: (messages, session, options, innerAgent, ct) =>
                    AgentRunStreamingWithGovernance(messages, session, options, innerAgent, journal, guardian, dna, logger, ct))
            .Build();
    }

    private static async Task<AgentResponse> AgentRunWithGovernance(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        AIAgent innerAgent, TaskJournal journal, SystemGuardian guardian,
        DNAOrchestrator? dna, ILogger logger, CancellationToken ct)
    {
        if (!TryPreProcess(messages, out var query, out _, out _, logger))
            return await innerAgent.RunAsync(messages, session, options, ct);

        var blockResult = await CheckBlockAsync(guardian, dna, query, logger, ct);
        if (blockResult != null) return blockResult;

        var entry = journal.Add(query);
        try
        {
            var response = await innerAgent.RunAsync(messages, session, options, ct);
            journal.Complete(entry, response.Text[..Math.Min(response.Text.Length, 500)]);
            PostProcess(dna, query, response.Text, logger);
            return response;
        }
        catch (Exception ex)
        {
            journal.Fail(entry, ex.Message);
            guardian.RecordError();
            logger.LogError(ex, "MAF middleware: agent run failed");
            throw;
        }
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> AgentRunStreamingWithGovernance(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        AIAgent innerAgent, TaskJournal journal, SystemGuardian guardian,
        DNAOrchestrator? dna, ILogger logger,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!TryPreProcess(messages, out var query, out _, out _, logger))
        {
            await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, ct))
                yield return update;
            yield break;
        }

        var blockResult = await CheckBlockAsync(guardian, dna, query, logger, ct);
        if (blockResult != null)
        {
            foreach (var update in blockResult.ToAgentResponseUpdates())
                yield return update;
            yield break;
        }

        var entry = journal.Add(query);
        var fullText = new System.Text.StringBuilder();
        string? streamError = null;

        await using var enumerator = innerAgent.RunStreamingAsync(messages, session, options, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            AgentResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync()) break;
                update = enumerator.Current;
            }
            catch (OperationCanceledException) { yield break; }
            catch (Exception ex)
            {
                streamError = ex.Message;
                journal.Fail(entry, ex.Message);
                guardian.RecordError();
                logger.LogError(ex, "MAF middleware: stream run failed");
                break;
            }

            fullText.Append(update.Text);
            yield return update;
        }

        if (streamError != null)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, $"Error: {streamError}");
        }
        else
        {
            journal.Complete(entry, fullText.ToString()[..Math.Min(fullText.Length, 500)]);
            PostProcess(dna, query, fullText.ToString(), logger);
        }
    }

    private static bool TryPreProcess(IEnumerable<ChatMessage> messages, out string query, out string label, out string? emotion, ILogger logger)
    {
        query = ""; label = "deep"; emotion = null;
        var msgList = messages.ToList();
        var userMessages = msgList.Where(m => m.Role == ChatRole.User).ToList();
        if (userMessages.Count == 0) return false;

        query = string.Join("\n", userMessages.Select(m => m.Text ?? ""));
        var (complexity, detectedLabel) = GovernorUtilities.ClassifyIntent(query);
        label = detectedLabel;
        emotion = GovernorUtilities.DetectEmotion(query);

        var shieldResult = PromptShield.Instance.SanitizeInput(query);
        if (!shieldResult.Passed)
        {
            logger.LogWarning("MAF middleware: PromptShield blocked input: {Violations}", string.Join(", ", shieldResult.Violations));
            query = shieldResult.SanitizedText;
        }

        logger.LogDebug("MAF middleware: label={Label} emotion={Emotion}", label, emotion);
        return true;
    }

    private static async Task<AgentResponse?> CheckBlockAsync(SystemGuardian guardian, DNAOrchestrator? dna, string query,
        ILogger logger, CancellationToken ct)
    {
        if (guardian.Mode == SystemMode.LifeSupport)
        {
            logger.LogWarning("MAF middleware: LifeSupport mode, blocking");
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "System is in emergency maintenance mode."));
        }

        if (dna != null)
        {
            try
            {
                var safetyCheck = await dna.Safety.EvaluateAsync(query, cancellationToken: ct);
                if (!safetyCheck.Allowed)
                {
                    logger.LogWarning("MAF middleware: DNA safety blocked: {Reason}", safetyCheck.BlockReason);
                    return new AgentResponse(new ChatMessage(ChatRole.Assistant, $"[Safety: {safetyCheck.BlockReason}]"));
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "MAF middleware: DNA safety check skipped"); }
        }

        return null;
    }

    private static void PostProcess(DNAOrchestrator? dna, string query, string response, ILogger? logger = null)
    {
        if (dna != null && !string.IsNullOrEmpty(response))
        {
            _ = Task.Run(async () =>
            {
                try { await dna.ProcessAsync(query, response, CancellationToken.None); }
                catch (Exception ex) { logger?.LogDebug(ex, "DNA background processing failed"); }
            }).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    logger?.LogDebug(t.Exception, "DNA background task failed");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
