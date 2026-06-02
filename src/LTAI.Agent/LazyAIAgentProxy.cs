// Copyright (c) LTAI. All rights reserved.

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Agent;

/// <summary>
/// <see cref="AIAgent"/> proxy that breaks the circular dependency that arises
/// when <c>HarnessAgent.BackgroundAgents</c> is set to the full agent list:
/// every agent's <see cref="HarnessAgent"/> constructor must enumerate its
/// background agents, but enumerating them re-enters <see cref="BuildAgentImpl"/>
/// for each, which in turn builds its own <see cref="HarnessAgent"/> and
/// needs the same list. <see cref="LazyAIAgentProxy"/> sidesteps this by
/// returning a stable <c>Name</c>/<c>Description</c> from the static
/// <c>AgentRegistry</c> at property-access time, without forcing the inner
/// agent to be constructed. The inner agent is resolved on first call into
/// <see cref="RunAsync"/>, <see cref="RunStreamingAsync"/>, etc. — by which
/// time the agent graph has been built and the call is safe. Only members
/// accessed at <c>BackgroundAgentsProvider</c> construction time
/// (<c>Name</c>, <c>Description</c>) are served from the registry; everything
/// else forwards to the resolved inner agent via the supplied
/// <see cref="IServiceProvider"/>.
/// </summary>
public sealed class LazyAIAgentProxy : AIAgent
{
    private readonly IServiceProvider _sp;
    private readonly string _agentName;
    private readonly Lazy<string?> _description;
    private AIAgent? _resolved;
    private readonly object _resolveLock = new();

    public LazyAIAgentProxy(IServiceProvider sp, string agentName)
    {
        _sp = sp;
        _agentName = agentName;
        _description = new Lazy<string?>(() =>
            AgentRegistry.LoadAll().FirstOrDefault(d =>
                string.Equals(d.Name, agentName, StringComparison.OrdinalIgnoreCase))?.Description,
            LazyThreadSafetyMode.PublicationOnly);
    }

    public override string? Name => _agentName;
    public override string? Description => _description.Value;

    private AIAgent Resolve()
    {
        if (_resolved is not null) return _resolved;
        lock (_resolveLock)
        {
            if (_resolved is not null) return _resolved;
            var agent = _sp.GetKeyedService<AIAgent>(_agentName)
                ?? throw new InvalidOperationException(
                    $"LazyAIAgentProxy: keyed service AIAgent('{_agentName}') not found in DI.");
            _resolved = agent;
            return _resolved;
        }
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => Resolve().CreateSessionAsync(cancellationToken);

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        => Resolve().SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        => Resolve().DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);

    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => await Resolve().RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => Resolve().RunStreamingAsync(messages, session, options, cancellationToken);
}
