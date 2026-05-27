using LTAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Agents;

public sealed class CodeAgentAdapter : IAgent
{
    private readonly CodeAgent _codeAgent;
    public string AgentId => $"code-{_codeAgent.Name}";
    public string Niche => "code";
    public string Description => "Code generation, review, and refactoring agent";
    public bool IsActive { get; private set; }

    public CodeAgentAdapter(CodeAgent codeAgent)
    {
        _codeAgent = codeAgent;
    }

    public Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct)
        => Task.FromResult(_codeAgent.Name);

    public Task ActivateAsync(CancellationToken ct) { IsActive = true; return Task.CompletedTask; }
    public Task DeactivateAsync(CancellationToken ct) { IsActive = false; return Task.CompletedTask; }
}

public sealed class EIAAgentAdapter : IAgent
{
    public string AgentId => "eia-primary";
    public string Niche => "eia";
    public string Description => "Environmental Impact Assessment agent";
    public bool IsActive { get; set; }

    public Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct)
        => Task.FromResult("eia");

    public Task ActivateAsync(CancellationToken ct) { IsActive = true; return Task.CompletedTask; }
    public Task DeactivateAsync(CancellationToken ct) { IsActive = false; return Task.CompletedTask; }
}

public sealed class ChatAgentAdapter : IAgent
{
    public string AgentId => "chat-primary";
    public string Niche => "chat";
    public string Description => "General conversation agent";
    public bool IsActive { get; set; }

    public Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct)
        => Task.FromResult("chat");

    public Task ActivateAsync(CancellationToken ct) { IsActive = true; return Task.CompletedTask; }
    public Task DeactivateAsync(CancellationToken ct) { IsActive = false; return Task.CompletedTask; }
}

public sealed class ReasoningAgentAdapter : IAgent
{
    public string AgentId => "reasoning-primary";
    public string Niche => "reasoning";
    public string Description => "Deep reasoning and chain-of-thought agent";
    public bool IsActive { get; set; }

    public Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct)
        => Task.FromResult("reasoning");

    public Task ActivateAsync(CancellationToken ct) { IsActive = true; return Task.CompletedTask; }
    public Task DeactivateAsync(CancellationToken ct) { IsActive = false; return Task.CompletedTask; }
}
