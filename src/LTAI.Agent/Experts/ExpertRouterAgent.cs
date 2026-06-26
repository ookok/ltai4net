using System.Runtime.CompilerServices;
using LTAI.Agent.Experts.Routing;
using LTAI.Agent.Vector;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Experts;

/// <summary>
/// MAF <see cref="DelegatingAIAgent"/> that intercepts knowledge-intensive queries
/// and routes them through the Expert pipeline (Router → Fan-out → Aggregate).
/// The aggregated expert context is injected into the conversation before
/// delegating to the inner agent.
///
/// Optional switch: only activates for queries classified as knowledge-seeking.
/// Greetings and casual chat pass through directly to the inner agent.
/// </summary>
public sealed class ExpertRouterAgent : DelegatingAIAgent
{
    private readonly ExpertRouter _router;
    private readonly ParallelFanOutExecutor _fanOut;
    private readonly ExpertAggregator _aggregator;
    private readonly ExpertRegistry _registry;
    private readonly ExpertFeedbackLogger? _feedback;

    public ExpertRouterAgent(
        AIAgent innerAgent,
        ExpertRouter router,
        ParallelFanOutExecutor fanOut,
        ExpertAggregator aggregator,
        ExpertRegistry registry,
        ExpertFeedbackLogger? feedback = null)
        : base(innerAgent)
    {
        _router = router;
        _fanOut = fanOut;
        _aggregator = aggregator;
        _registry = registry;
        _feedback = feedback;
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var userText = GetLastUserText(msgList);

        if (string.IsNullOrWhiteSpace(userText) || !IsKnowledgeQuery(userText))
        {
            return await this.InnerAgent.RunAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var selection = await _router.SelectExpertsAsync(userText, cancellationToken).ConfigureAwait(false);

            if (selection.Selections.Count == 0)
            {
                return await this.InnerAgent.RunAsync(messages, session, options, cancellationToken)
                    .ConfigureAwait(false);
            }

            var query = new ExpertQuery(userText, MaxResults: 5);
            var selectedExperts = ResolveExperts(selection);
            var responses = await _fanOut.ExecuteAsync(selectedExperts, query, cancellationToken).ConfigureAwait(false);
            var aggregated = await _aggregator.AggregateAsync(responses, cancellationToken).ConfigureAwait(false);

            _feedback?.Record(userText, selection, responses, aggregated);

            if (!aggregated.HasAnswer)
            {
                return await this.InnerAgent.RunAsync(messages, session, options, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Inject aggregated expert context as a system message before the last user message
            var augmentedMessages = AugmentMessages(msgList, aggregated.Content);
            return await this.InnerAgent.RunAsync(augmentedMessages, session, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("ExpertRouterAgent: expert pipeline failed, falling back to inner agent: {0}", ex.Message);
            return await this.InnerAgent.RunAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var msgList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var augmentedMessages = await DecideMessagesAsync(msgList, session, options, cancellationToken)
            .ConfigureAwait(false);

        await foreach (var update in this.InnerAgent.RunStreamingAsync(
            augmentedMessages, session, options, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    private async Task<IEnumerable<ChatMessage>> DecideMessagesAsync(
        IReadOnlyList<ChatMessage> msgList,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var userText = GetLastUserText(msgList);

        if (string.IsNullOrWhiteSpace(userText) || !IsKnowledgeQuery(userText))
            return msgList;

        try
        {
            var selection = await _router.SelectExpertsAsync(userText, cancellationToken).ConfigureAwait(false);
            if (selection.Selections.Count == 0)
                return msgList;

            var query = new ExpertQuery(userText, MaxResults: 5);
            var selectedExperts = ResolveExperts(selection);
            var responses = await _fanOut.ExecuteAsync(selectedExperts, query, cancellationToken).ConfigureAwait(false);
            var aggregated = await _aggregator.AggregateAsync(responses, cancellationToken).ConfigureAwait(false);

            _feedback?.Record(userText, selection, responses, aggregated);

            if (!aggregated.HasAnswer)
                return msgList;

            return AugmentMessages(msgList, aggregated.Content);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("ExpertRouterAgent: DecideMessagesAsync failed, returning original messages: {0}", ex.Message);
            return msgList;
        }
    }

    private IReadOnlyList<(IExpertModule Expert, float RouterConfidence)> ResolveExperts(
        ExpertSelectionResult selection)
    {
        var entryMap = _registry.Entries.ToDictionary(e => e.Expert.ExpertId, e => e.Expert);
        var result = new List<(IExpertModule, float)>();
        foreach (var sel in selection.Selections)
        {
            if (entryMap.TryGetValue(sel.ExpertId, out var expert))
                result.Add((expert, sel.Confidence));
        }
        return result;
    }

    private static string? GetLastUserText(IReadOnlyList<ChatMessage> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User && !string.IsNullOrWhiteSpace(messages[i].Text))
                return messages[i].Text;
        }
        return null;
    }

    private static List<ChatMessage> AugmentMessages(
        IReadOnlyList<ChatMessage> original, string expertContext)
    {
        var augmented = new List<ChatMessage>(original.Count + 1);
        var insertIdx = original.Count - 1;

        // Find the last user message index to insert system context before it
        for (int i = original.Count - 1; i >= 0; i--)
        {
            if (original[i].Role == ChatRole.User)
            {
                insertIdx = i;
                break;
            }
        }

        for (int i = 0; i < original.Count; i++)
        {
            if (i == insertIdx)
            {
                augmented.Add(new ChatMessage(ChatRole.System, expertContext));
            }
            augmented.Add(original[i]);
        }

        return augmented;
    }

    /// <summary>
    /// Heuristic: classify whether the query is knowledge-seeking (vs. greeting/casual chat).
    /// Greetings and very short queries skip Expert routing.
    /// Uses KbGraph's internal centroid-based classifier as the primary signal.
    /// Delegates greeting detection to the unified <see cref="Memory.QueryClassifier"/>.
    /// </summary>
    internal static bool IsKnowledgeQuery(string text)
    {
        if (text.Length < 10) return false;
        if (Memory.QueryClassifier.IsGreetingOnlyStatic(text)) return false;
        return KbGraph.IsKnowledgeQuery(text);
    }
}
