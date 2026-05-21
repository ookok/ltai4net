using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.AI.Governors;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace LTAI.MAF;

public sealed class LTAIAgent : AIAgent
{
    private readonly LivingTreeSystem _livingTree;
    private readonly ILogger<LTAIAgent> _logger;
    private readonly LTAIInputFilter? _inputFilter;
    private readonly LTAIOutputFilter? _outputFilter;

    public override string? Name => "LTAI";
    public override string? Description => "LivingTree AI Agent with bio-inspired governance";

    public LTAIAgent(
        LivingTreeSystem livingTree,
        ILogger<LTAIAgent> logger,
        LTAIInputFilter? inputFilter = null,
        LTAIOutputFilter? outputFilter = null)
    {
        _livingTree = livingTree;
        _logger = logger;
        _inputFilter = inputFilter;
        _outputFilter = outputFilter;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMessages = msgList.Where(m => m.Role == ChatRole.User).ToList();

        if (userMessages.Count == 0)
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, "No user message received."));

        var query = string.Join("\n", userMessages.Select(m => m.Text ?? ""));
        var ltaSession = session as LTAIAgentSession;

        _inputFilter?.Analyze(userMessages.Last());
        _logger.LogInformation("LTAI agent: {Query}", query[..Math.Min(query.Length, 200)]);

        var sessionHistory = ltaSession?.GetCompressedHistory();
        if (!string.IsNullOrEmpty(sessionHistory))
            query = $"Previous conversation:\n{sessionHistory}\n\nCurrent query:\n{query}";

        try
        {
            string result;
            var useWorkflow = options?.AdditionalProperties?.TryGetValue("useWorkflow", out var wf) == true && wf is true;
            var forceChat = options?.AdditionalProperties?.TryGetValue("forceChat", out var fc) == true && fc is true;

            bool shouldUseWorkflow = useWorkflow || (!forceChat && ShouldUseWorkflow(query));

            if (shouldUseWorkflow)
            {
                var wfResult = await GovernorWorkflow.ExecuteWorkflowAsync(_livingTree, query, cancellationToken);
                result = wfResult.IsBlocked ? $"[Blocked: {wfResult.BlockReason}]" : wfResult.Response;
            }
            else
            {
                result = await _livingTree.ChatAsync(query, cancellationToken);
            }

            if (_outputFilter != null) result = _outputFilter.Review(result);
            ltaSession?.AddTurn(userMessages.Last().Text ?? query, result);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LTAI agent error");
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, $"Error: {ex.Message}"));
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMessages = msgList.Where(m => m.Role == ChatRole.User).ToList();

        if (userMessages.Count == 0)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, "No user message received.");
            yield break;
        }

        var query = string.Join("\n", userMessages.Select(m => m.Text ?? ""));

        _inputFilter?.Analyze(userMessages.Last());

        _logger.LogInformation("LTAI agent (stream): {Query}", query[..Math.Min(query.Length, 200)]);

        var useWorkflow = options?.AdditionalProperties?.TryGetValue("useWorkflow", out var wf) == true && wf is true;
        var forceChat = options?.AdditionalProperties?.TryGetValue("forceChat", out var fc) == true && fc is true;
        bool shouldUseWorkflow = useWorkflow || (!forceChat && ShouldUseWorkflow(query));

        if (shouldUseWorkflow)
        {
            await foreach (var update in StreamWorkflowAsync(query, cancellationToken))
                yield return update;
        }
        else
        {
            await using var enumerator = _livingTree.StreamChatAsync(query, cancellationToken).GetAsyncEnumerator(cancellationToken);
            string? streamError = null;

            while (true)
            {
                string chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    chunk = enumerator.Current;
                }
                catch (OperationCanceledException) { yield break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LTAI agent stream error");
                    streamError = ex.Message;
                    break;
                }

                yield return new AgentResponseUpdate(ChatRole.Assistant, chunk);
            }

            if (streamError != null)
            {
                yield return new AgentResponseUpdate(ChatRole.Assistant, $"Error: {streamError}");
            }
        }
    }

    private async IAsyncEnumerable<AgentResponseUpdate> StreamWorkflowAsync(string query, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in GovernorWorkflow.ExecuteWorkflowStreamingAsync(_livingTree, query, ct))
        {
            switch (evt)
            {
                case ProgressEvent progress:
                    yield return new AgentResponseUpdate(ChatRole.Assistant, $"\n[{progress.GetType().Name}]\n");
                    break;
                case WorkflowOutputEvent output when output.Data is GovernorResult gr:
                    var response = gr.IsBlocked ? $"[Blocked: {gr.BlockReason}]" : gr.Response;
                    yield return new AgentResponseUpdate(ChatRole.Assistant, response);
                    break;
                case WorkflowErrorEvent err:
                    yield return new AgentResponseUpdate(ChatRole.Assistant, $"\n[Error: {err.Exception?.Message ?? "Unknown"}]\n");
                    break;
            }
        }
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<AgentSession>(new LTAIAgentSession());
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToElement(new { id = Guid.NewGuid().ToString("N") });
        return ValueTask.FromResult(json);
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var session = new LTAIAgentSession();
        return ValueTask.FromResult<AgentSession>(session);
    }

    private static bool ShouldUseWorkflow(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;

        var trimmed = query.Trim();
        if (trimmed.Length < 10) return false;

        if (trimmed.StartsWith('/') && trimmed.Length < 30) return false;

        var workflowKeywords = new[]
        {
            "分析", "审查", "review", "analyze", "比较", "compare",
            "设计", "design", "架构", "architecture", "规划", "plan",
            "重构", "refactor", "优化", "optimize", "调试", "debug",
            "为什么", "why", "如何", "how to", "解释", "explain",
            "pipeline", "workflow", "流程", "编排", "orchestrate"
        };

        var lower = query.ToLowerInvariant();
        foreach (var kw in workflowKeywords)
        {
            if (lower.Contains(kw)) return true;
        }

        var wordCount = trimmed.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 50) return true;

        var sentenceCount = trimmed.Count(c => c is '.' or '。' or '!' or '！' or '?' or '？');
        if (sentenceCount >= 3) return true;

        return false;
    }
}
