namespace LTAI.Core.Interfaces;

/// <summary>
/// Core agent interface — all agent types (code, chat, EIA, reasoning) implement this.
/// Agents are identified by AgentId and categorized by Niche (a string tag, not an enum).
/// HandleAsync is the main entry: string query in, string response out,
/// with a Dictionary<string, object> context bag for cross-cutting concerns.
/// Callers: LTAI.Agent.MAF.AgenticLoop, LTAI.Agent.Workflows.AgentPool,
///          LTAI.Agent.Federation.FederationCoordinator.
/// </summary>
public interface IAgent
{
    string AgentId { get; }
    string Niche { get; }
    string Description { get; }
    bool IsActive { get; }

    Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct);
    Task ActivateAsync(CancellationToken ct);
    Task DeactivateAsync(CancellationToken ct);
}

/// <summary>
/// Pluggable factory for creating agent instances by niche.
/// Register implementations via DI; AgentPool resolves by SupportedNiches.
/// Callers: LTAI.Agent.AgentFactory, LTAI.Agent.AgentRegistry.
/// </summary>
public interface IAgentFactory
{
    string FactoryId { get; }
    string[] SupportedNiches { get; }
    Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct);
}
