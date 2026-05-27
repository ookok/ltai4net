namespace LTAI.Core.Interfaces;

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

public interface IAgentFactory
{
    string FactoryId { get; }
    string[] SupportedNiches { get; }
    Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct);
}
