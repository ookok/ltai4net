using LTAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Agents;

public sealed class CodeAgentFactory : LTAI.Core.Interfaces.IAgentFactory
{
    public string FactoryId => "code-agent";
    public string[] SupportedNiches => new[] { "code" };

    private readonly IServiceProvider _sp;
    public CodeAgentFactory(IServiceProvider sp) => _sp = sp;

    public Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct)
    {
        return Task.FromResult<IAgent>(new CodeAgentAdapter(_sp));
    }
}

public sealed class EIAAgentFactory : LTAI.Core.Interfaces.IAgentFactory
{
    public string FactoryId => "eia-agent";
    public string[] SupportedNiches => new[] { "eia" };

    public Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct)
        => Task.FromResult<IAgent>(new EIAAgentAdapter());
}

public sealed class ChatAgentFactory : LTAI.Core.Interfaces.IAgentFactory
{
    public string FactoryId => "chat-agent";
    public string[] SupportedNiches => new[] { "chat" };

    public Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct)
        => Task.FromResult<IAgent>(new ChatAgentAdapter());
}

public sealed class ReasoningAgentFactory : LTAI.Core.Interfaces.IAgentFactory
{
    public string FactoryId => "reasoning-agent";
    public string[] SupportedNiches => new[] { "reasoning" };

    public Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct)
        => Task.FromResult<IAgent>(new ReasoningAgentAdapter());
}
