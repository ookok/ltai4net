using System.Runtime.CompilerServices;
using System.Text.Json;
using LTAI.AI.Interfaces;
using LTAI.AI.Governors;
using LTAI.Agent.Skills;
using LTAI.Agent.Skills.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace LTAI.Agent;

public sealed class LTAIAgent : AIAgent
{
    private readonly ChatClientAgent _chatAgent;
    private readonly ILivingTreeSystem _livingTree;
    private readonly SkillRegistry _skillRegistry;
    private readonly SkillExtractor? _skillExtractor;
    private readonly MultiRoundOrchestrator? _multiRound;
    private readonly ILogger<LTAIAgent> _logger;
    private readonly LogiInputFilter? _inputFilter;
    private readonly LogiOutputFilter? _outputFilter;

    public override string? Name => "LTAI";
    public override string? Description => "LivingTree AI Agent with bio-inspired governance";

    public LTAIAgent(
        ILivingTreeSystem livingTree,
        SkillRegistry skillRegistry,
        ILogger<LTAIAgent> logger,
        SkillExtractor? skillExtractor = null,
        MultiRoundOrchestrator? multiRound = null,
        LogiInputFilter? inputFilter = null,
        LogiOutputFilter? outputFilter = null)
    {
        _livingTree = livingTree;
        _skillRegistry = skillRegistry;
        _logger = logger;
        _skillExtractor = skillExtractor;
        _multiRound = multiRound;
        _inputFilter = inputFilter;
        _outputFilter = outputFilter;

        _chatAgent = new ChatClientAgent(
            new LivingTreeChatClient(livingTree, null),
            new ChatClientAgentOptions
            {
                Name = "LTAI",
                Description = "LivingTree AI Agent with bio-inspired governance"
            });
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
            if (_multiRound != null && query.Length > 200)
            {
                var result = new System.Text.StringBuilder();
                var lastContent = "";
                await foreach (var evt in _multiRound.ExecuteAsync(query, ct: cancellationToken))
                {
                    if (evt.Phase is MultiRoundPhase.RoundComplete or MultiRoundPhase.Complete)
                    {
                        lastContent = evt.Content;
                        result.AppendLine(evt.Content);
                    }
                    else if (evt.Phase == MultiRoundPhase.PlanReady)
                    {
                        result.AppendLine($"[{evt.Description}]");
                    }
                }

                var finalResult = result.Length > 0 ? result.ToString().Trim() : lastContent;
                if (_outputFilter != null)
                {
                    var (allowed, blockReason) = _outputFilter.Review(finalResult);
                    if (blockReason != null) finalResult = $"[Blocked: {blockReason}]";
                    else if (allowed != null) finalResult = allowed;
                }
                ltaSession?.AddTurn(userMessages.Last().Text ?? query, finalResult);
                RecordSkillPattern(query, finalResult);
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, finalResult));
            }

            var useWorkflow = options?.AdditionalProperties?.TryGetValue("useWorkflow", out var wf) == true && wf is true;
            var forceChat = options?.AdditionalProperties?.TryGetValue("forceChat", out var fc) == true && fc is true;
            bool shouldUseWorkflow = useWorkflow || (!forceChat && ShouldUseWorkflow(query));

            if (shouldUseWorkflow)
            {
                var wfResult = await GovernorWorkflow.ExecuteWorkflowAsync(_livingTree, query, cancellationToken).ConfigureAwait(false);
                var result = wfResult.IsBlocked ? $"[Blocked: {wfResult.BlockReason}]" : wfResult.Response;
                if (_outputFilter != null)
                {
                    var (allowed, blockReason) = _outputFilter.Review(result);
                    if (blockReason != null) result = $"[Blocked: {blockReason}]";
                    else if (allowed != null) result = allowed;
                }
                ltaSession?.AddTurn(userMessages.Last().Text ?? query, result);
                RecordSkillPattern(query, result);
                return new AgentResponse(new ChatMessage(ChatRole.Assistant, result));
            }

            var response = await _chatAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
            var text = response.Text ?? "";
            if (_outputFilter != null)
            {
                var (allowed, blockReason) = _outputFilter.Review(text);
                if (blockReason != null) text = $"[Blocked: {blockReason}]";
                else if (allowed != null) text = allowed;
            }
            ltaSession?.AddTurn(userMessages.Last().Text ?? query, text);
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, text));
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
            await using var enumerator = _chatAgent.RunStreamingAsync(messages, session, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
            string? streamError = null;

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
                    _logger.LogError(ex, "LTAI agent stream error");
                    streamError = ex.Message;
                    break;
                }

                yield return update;
            }

            if (streamError != null)
                yield return new AgentResponseUpdate(ChatRole.Assistant, $"Error: {streamError}");
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
        var ltsAgentSession = session as LTAIAgentSession;
        var data = new Dictionary<string, object?>
        {
            ["session_id"] = ltsAgentSession?.SessionId ?? Guid.NewGuid().ToString("N"),
            ["agent_name"] = Name,
            ["agent_id"] = Id,
            ["turn_count"] = ltsAgentSession?.TurnCount ?? 0,
            ["last_intent"] = ltsAgentSession?.LastIntent ?? "",
            ["pipeline"] = new { mode = _livingTree.Mode.ToString(), dna = _livingTree.DNAEnabled }
        };
        var json = JsonSerializer.SerializeToElement(data);
        return ValueTask.FromResult(json);
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        var session = new LTAIAgentSession();
        if (serializedState.TryGetProperty("last_intent", out var li))
            session.LastIntent = li.GetString();
        if (serializedState.TryGetProperty("turn_count", out var tc) && tc.TryGetInt32(out var turns))
            session.TurnCount = turns;
        return ValueTask.FromResult<AgentSession>(session);
    }

    private bool ShouldUseWorkflow(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var trimmed = query.Trim();
        if (trimmed.Length < 10) return false;
        if (trimmed.StartsWith('/') && trimmed.Length < 30) return false;

        var matchedSkills = _skillRegistry.MatchByTrigger(query);
        if (matchedSkills.Any(s => s.IsReliable))
            return true;

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
            if (lower.Contains(kw)) return true;

        var wordCount = trimmed.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 50) return true;

        var sentenceCount = trimmed.Count(c => c is '.' or '。' or '!' or '！' or '?' or '？');
        if (sentenceCount >= 3) return true;

        return false;
    }

    private void RecordSkillPattern(string query, string response)
    {
        if (_skillExtractor == null) return;
        if (string.IsNullOrEmpty(response) || response.Length < 20) return;
        if (response.StartsWith("Error") || response.StartsWith("[Blocked")) return;

        var matchedSkills = _skillRegistry.MatchByTrigger(query);
        foreach (var skill in matchedSkills.Take(3))
        {
            var patternKey = $"skill_{skill.Name}";
            var toolNames = skill.Steps.Where(s => s.ToolName != null).Select(s => s.ToolName!).ToList();
            _skillExtractor.RecordSuccess(patternKey, toolNames, query, response);
        }
    }
}
