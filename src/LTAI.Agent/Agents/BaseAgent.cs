using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed record AgentCard(string Name, string Description, string Instructions, string[] Capabilities);

public interface IAnalysisStrategy<TInput, TResult>
{
    string StrategyName { get; }
    bool CanHandle(string query);
    Task<TResult> AnalyzeAsync(TInput input, CancellationToken ct);
}

public sealed record AgentContext(
    string UserQuery,
    List<ChatMessage> FullHistory,
    AgentSession? Session);

public sealed class SkillRegistry
{
    private readonly Dictionary<string, Func<object, CancellationToken, Task<object>>> _skills = new();

    public void Register(string skillId, Func<object, CancellationToken, Task<object>> handler)
    {
        _skills[skillId] = handler;
    }

    public async Task<TResult> RunAsync<TSkill, TResult>(
        string skillId, object input, CancellationToken ct)
    {
        if (!_skills.TryGetValue(skillId, out var handler))
            throw new InvalidOperationException($"Skill '{skillId}' not registered.");

        var result = await handler(input, ct).ConfigureAwait(false);
        if (result is TResult typed)
            return typed;

        throw new InvalidOperationException(
            $"Skill '{skillId}' returned {result?.GetType().Name}, expected {typeof(TResult).Name}.");
    }
}

public abstract class BaseAgent : AIAgent
{
    private readonly SkillRegistry _skills;
    private readonly IChatClient _brain;
    protected readonly ILogger _logger;
    private readonly List<IAnalysisStrategy<AgentContext, AgentResponse>> _strategies = new();

    public override string Name { get; }
    public override string Description { get; }

    protected BaseAgent(
        AgentCard card,
        IChatClient brain,
        SkillRegistry skills,
        ILogger logger)
    {
        Name = card.Name;
        Description = card.Instructions;
        _brain = brain;
        _skills = skills;
        _logger = logger;
    }

    public void RegisterStrategy(IAnalysisStrategy<AgentContext, AgentResponse> strategy)
    {
        _strategies.Add(strategy);
        _logger.LogInformation("{Agent} registered strategy: {Strategy}", Name, strategy.StrategyName);
    }

    protected override sealed async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgList = messages.ToList();
        var userMsg = msgList.LastOrDefault(m => m.Role == ChatRole.User);

        if (userMsg?.Text is null)
            return Fail("No user message.");

        var query = userMsg.Text;
        var context = new AgentContext(query, msgList, session);
        _logger.LogInformation("{Type}[{Name}]: processing", GetType().Name, Name);

        foreach (var strategy in _strategies)
        {
            if (strategy.CanHandle(query))
            {
                _logger.LogDebug("{Agent} using strategy: {Strategy}", Name, strategy.StrategyName);
                return await strategy.AnalyzeAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }

        return await ExecuteLogicAsync(context, cancellationToken).ConfigureAwait(false);
    }

    protected abstract Task<AgentResponse> ExecuteLogicAsync(
        AgentContext context, CancellationToken ct);

    protected async Task<AgentResponse> CallBrainAsync(
        List<ChatMessage> messages,
        ChatOptions? chatOptions = null,
        CancellationToken ct = default)
    {
        var response = await _brain.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);
        return new AgentResponse(new ChatMessage(ChatRole.Assistant, response.Messages?.LastOrDefault()?.Text ?? ""));
    }

    protected async IAsyncEnumerable<AgentResponseUpdate> CallBrainStreamingAsync(
        IEnumerable<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await _brain.GetResponseAsync(messages.ToList(), cancellationToken: ct).ConfigureAwait(false);
        yield return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, response.Messages?.LastOrDefault()?.Text ?? ""));
    }

    protected static AgentResponse Fail(string reason) =>
        new(new ChatMessage(ChatRole.Assistant, $"[{nameof(BaseAgent)}] {reason}"));

    protected void LogWarning(string msg) => _logger.LogWarning(msg);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in CallBrainStreamingAsync(messages, cancellationToken))
            yield return update;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new LTAIAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? o = null, CancellationToken ct = default)
        => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { sessionId = (session as LTAIAgentSession)?.SessionId }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement state, JsonSerializerOptions? o = null, CancellationToken ct = default)
        => ValueTask.FromResult<AgentSession>(new LTAIAgentSession());
}

