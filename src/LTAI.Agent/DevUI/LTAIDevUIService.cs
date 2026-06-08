// Copyright (c) LTAI. All rights reserved.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.DevUI;

/// <summary>
/// UI-portable AgentCard describing a single LTAI agent. Mirrors the subset of
/// A2A <c>AgentCard</c> (Name, Description, Version, Skills, Capabilities,
/// DefaultInputModes, DefaultOutputModes) plus LTAI-specific metadata (model,
/// temperature, tool list, permission flags, tool count) used by TUI Dashboard
/// and Desktop DevUI surfaces. LTAI.Web converts this to A2A AgentCard for
/// the <c>/.well-known/agent-card.json</c> endpoint.
/// </summary>
public sealed record LTAIAgentCard
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string? DocumentationUrl { get; init; }
    public IReadOnlyList<LTAIAgentSkill> Skills { get; init; } = [];
    public LTAIAgentCapabilities Capabilities { get; init; } = new();
    public IReadOnlyList<string> DefaultInputModes { get; init; } = ["text"];
    public IReadOnlyList<string> DefaultOutputModes { get; init; } = ["text"];
    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? ModelId { get; init; }
    public double Temperature { get; init; } = 0.7;
    public double TopP { get; init; } = 0.95;
    public IReadOnlyList<string> Tools { get; init; } = [];
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public int ToolCount => Tools.Count;
}

public sealed record LTAIAgentSkill
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record LTAIAgentCapabilities
{
    public bool Streaming { get; init; } = true;
    public bool PushNotifications { get; init; } = false;
    public bool StateTransitionHistory { get; init; } = true;
}

/// <summary>
/// Shared service used by LTAI.Web (DevUI REST surface), LTAI.TUI
/// (<c>/dashboard</c> slash command) and LTAI.Desktop (WebView2 inspector) to
/// enumerate agents, render their <see cref="LTAIAgentCard"/>, and run them
/// with streaming updates. Resolves keyed <see cref="AIAgent"/> instances
/// registered by <see cref="AddLTAIAgent"/> (P4 Hosting migration).
/// </summary>
public sealed class LTAIDevUIService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<LTAIDevUIService> _logger;
    private readonly object _cardCacheLock = new();
    private IReadOnlyList<LTAIAgentCard>? _cardCache;
    private int _cardCacheGeneration;

    public LTAIDevUIService(IServiceProvider sp, ILogger<LTAIDevUIService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    public IReadOnlyList<LTAIAgentCard> ListAgentCards()
    {
        var gen = Volatile.Read(ref _cardCacheGeneration);
        var cache = _cardCache;
        if (cache != null && gen == _cardCacheGeneration)
            return cache;

        var defs = AgentRegistry.LoadAll();
        var cards = new List<LTAIAgentCard>(defs.Count);
        foreach (var def in defs)
        {
            if (!AgentExists(def.Name))
            {
                _logger.LogDebug("Skipping agent {Name} (not registered in DI)", def.Name);
                continue;
            }
            cards.Add(BuildCard(def));
        }
        lock (_cardCacheLock)
        {
            _cardCache = cards;
            Volatile.Write(ref _cardCacheGeneration, gen);
        }
        return cards;
    }

    public void InvalidateCardCache()
    {
        lock (_cardCacheLock)
        {
            _cardCache = null;
            Interlocked.Increment(ref _cardCacheGeneration);
        }
    }

    public LTAIAgentCard? GetAgentCard(string name)
    {
        // Check cache first
        var cache = _cardCache;
        if (cache != null)
        {
            var found = cache.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
        }
        var def = AgentRegistry.LoadAll().FirstOrDefault(d =>
            string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (def is null || !AgentExists(def.Name))
        {
            return null;
        }
        return BuildCard(def);
    }

    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        string name,
        string message,
        string? sessionId,
        Action<string>? onSessionUpdated = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var agent = ResolveAgent(name)
            ?? throw new InvalidOperationException($"Agent '{name}' is not registered.");

        AgentSession session;
        if (!string.IsNullOrEmpty(sessionId))
        {
            session = await DeserializeSessionAsync(agent, sessionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        var chatMessage = new ChatMessage(ChatRole.User, message);
        await foreach (var update in agent.RunStreamingAsync(chatMessage, session, cancellationToken: cancellationToken).ConfigureAwait(false))
            yield return update;

        if (onSessionUpdated != null)
        {
            var json = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.GetRawText()));
            onSessionUpdated(base64);
        }
    }

    private static async Task<AgentSession> DeserializeSessionAsync(AIAgent agent, string sessionId, CancellationToken ct)
    {
        var jsonBytes = Convert.FromBase64String(sessionId);
        var json = Encoding.UTF8.GetString(jsonBytes);
        var element = JsonDocument.Parse(json).RootElement;
        return await agent.DeserializeSessionAsync(element, cancellationToken: ct).ConfigureAwait(false);
    }

    private AIAgent? ResolveAgent(string name)
    {
        try
        {
            return _sp.GetKeyedService<AIAgent>(name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve agent {Name}", name);
            return null;
        }
    }

    private bool AgentExists(string name)
    {
        try
        {
            return _sp.GetKeyedService<AIAgent>(name) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static LTAIAgentCard BuildCard(AgentFileDef def)
    {
        var skills = def.Tools
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tool => new LTAIAgentSkill
            {
                Id = tool,
                Name = tool,
                Description = $"Tool: {tool}",
                Tags = [def.Name, "tool"],
            })
            .ToList();
        return new LTAIAgentCard
        {
            Name = def.Name,
            Description = def.Description,
            Version = "1.0.0",
            Skills = skills,
            Capabilities = new LTAIAgentCapabilities
            {
                Streaming = true,
                PushNotifications = false,
                StateTransitionHistory = true,
            },
            DefaultInputModes = ["text"],
            DefaultOutputModes = ["text"],
            Tags = [def.Name, "ltai"],
            ModelId = def.ModelId,
            Temperature = def.Temperature,
            TopP = def.TopP,
            Tools = def.Tools,
            Permissions = def.Permissions,
        };
    }

}
