// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════════════
//  DiscoursePlanningStep — discourse-driven answer planning
//
//  Disco-RAG inspired: after tool execution, before grammar check,
//  generates a rhetorically-informed answer blueprint based on the
//  query and collected context. The plan is injected as a system
//  message to guide the final generation toward coherent,
//  well-structured responses.
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that generates a discourse-driven answer blueprint
/// (Disco-RAG inspired). Runs after tool execution (context is fully
/// assembled) but before grammar/quality checks. The plan guides the
/// final LLM generation toward coherent rhetorical structure, avoiding
/// the "flat facts bag" problem of standard RAG.
/// </summary>
public sealed class DiscoursePlanningStep : IPipelineStep
{
    private readonly IChatClient? _planner;
    private readonly ILogger<DiscoursePlanningStep> _logger;

    public string Name => "DiscoursePlanning";

    public DiscoursePlanningStep(
        IChatClient? planner = null,
        ILogger<DiscoursePlanningStep>? logger = null)
    {
        _planner = planner;
        _logger = logger ?? NullLogger<DiscoursePlanningStep>.Instance;
    }

    public async Task<MessageContext> ProcessAsync(MessageContext context)
    {
        if (_planner == null)
        {
            _logger.LogDebug("DiscoursePlanningStep: no planner LLM registered, skipping");
            return context;
        }

        // Skip planning for very short/simple queries
        if (context.Request.Length < 15)
        {
            _logger.LogDebug("DiscoursePlanningStep: query too short, skipping");
            return context;
        }

        try
        {
            _logger.LogDebug("DiscoursePlanningStep: generating discourse plan");

            // Collect recent context: last user message + tool results
            var recentContext = CollectRecentContext(context);
            if (string.IsNullOrWhiteSpace(recentContext))
            {
                _logger.LogDebug("DiscoursePlanningStep: no context to plan from");
                return context;
            }

            var plan = await GenerateDiscoursePlanAsync(context.Request, recentContext, context.CancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(plan))
            {
                lock (context.MessagesLock)
                {
                    context.Messages.Add(new ChatMessage(ChatRole.System, $"[回答大纲]\n{plan}"));
                }
                _logger.LogDebug("DiscoursePlanningStep: injected discourse plan ({Len} chars)", plan.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DiscoursePlanningStep: planning failed (non-fatal)");
        }

        return context;
    }

    /// <summary>
    /// Collect the last few user + assistant messages and tool results
    /// as context for planning. Limits to ~2000 chars to avoid token waste.
    /// </summary>
    private static string CollectRecentContext(MessageContext context)
    {
        var sb = new System.Text.StringBuilder();

        // Add tool results
        foreach (var (name, args, result) in context.ToolCalls)
        {
            var snippet = result?.Length > 300 ? result[..300] + "..." : result;
            sb.AppendLine($"[工具: {name}] {snippet}");
            if (sb.Length > 2000) break;
        }

        // Add recent assistant messages (excluding system prompts)
        int systemPrefixes = 0;
        for (int i = context.Messages.Count - 1; i >= 0 && sb.Length < 2000; i--)
        {
            var msg = context.Messages[i];
            if (msg.Role == ChatRole.System && systemPrefixes < 2)
            {
                sb.Insert(0, $"[系统: {msg.Text?[..Math.Min(msg.Text.Length, 200)]}]\n");
                systemPrefixes++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Use LLM to generate a discourse-driven answer blueprint.
    /// The plan specifies: key points, rhetorical structure (background →
    /// evidence → contrast → conclusion), and evidence ordering strategy.
    /// </summary>
    private async Task<string?> GenerateDiscoursePlanAsync(string query, string context, CancellationToken ct)
    {
        var prompt = $@"You are a discourse planning assistant. Given a user query and available context, create a concise answer blueprint.

Query: {query}

Available context:
{context}

Produce a 3-part plan:
1. KEY POINTS: List the essential facts/arguments to include (max 3 bullet points)
2. RHETORICAL STRUCTURE: Organize the answer flow using: background → elaboration/evidence → contrast (if applicable) → conclusion
3. EVIDENCE ORDER: Specify which evidence to present first and why

Keep the plan under 400 characters total. Use this format:

## 要点
- ...

## 结构
background → elaboration → conclusion

## 排序
why this order";

        var response = await _planner
            .GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct)
            .ConfigureAwait(false);

        return response?.Text;
    }
}
