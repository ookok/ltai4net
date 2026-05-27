using LTAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

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

    private readonly IServiceProvider _sp;
    public EIAAgentFactory(IServiceProvider sp) => _sp = sp;

    public Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct)
        => Task.FromResult<IAgent>(new EIAAgentAdapter(_sp));
}

public sealed class ChatAgentFactory : LTAI.Core.Interfaces.IAgentFactory
{
    public string FactoryId => "chat-agent";
    public string[] SupportedNiches => new[] { "chat" };

    private readonly IServiceProvider _sp;
    public ChatAgentFactory(IServiceProvider sp) => _sp = sp;

    public Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct)
        => Task.FromResult<IAgent>(new ChatAgentAdapter(_sp));
}

public sealed class ReasoningAgentFactory : LTAI.Core.Interfaces.IAgentFactory
{
    public string FactoryId => "reasoning-agent";
    public string[] SupportedNiches => new[] { "reasoning" };

    private readonly IServiceProvider _sp;
    public ReasoningAgentFactory(IServiceProvider sp) => _sp = sp;

    public Task<IAgent> CreateAsync(Dictionary<string, object> config, CancellationToken ct)
        => Task.FromResult<IAgent>(new ReasoningAgentAdapter(_sp));
}
