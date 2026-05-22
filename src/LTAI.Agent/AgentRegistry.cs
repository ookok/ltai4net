using LTAI.Models;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

public sealed class AgentRegistry
{
    private readonly AgentConfig _config;
    private readonly IAgentFactory _factory;
    private readonly ILogger<AgentRegistry> _logger;

    public IReadOnlyList<LTAIAgentCard> Cards => _config.Agents;

    public AgentRegistry(AgentConfig config, IAgentFactory factory, ILogger<AgentRegistry> logger)
    {
        _config = config;
        _factory = factory;
        _logger = logger;
    }

    public void Initialize()
    {
        _logger.LogInformation("AgentRegistry: Loading {Count} agents", _config.Agents.Count);

        foreach (var card in _config.Agents)
        {
            _factory.GetOrCreate(card.Name);
            _logger.LogInformation("AgentRegistry: Registered '{Name}' type={Type}", card.Name, card.Type);
        }
    }

    public LTAIAgentCard? GetCard(string name) =>
        _config.Agents.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> ListAgentNames() => _config.Agents.Select(a => a.Name);
}
