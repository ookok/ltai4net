using LTAI.Core.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace LTAI.Agent.Agents;

public sealed class CodeAgentAdapter : IAgent
{
    private readonly IServiceProvider _sp;
    private CodeAgent? _codeAgent;

    public string AgentId => "code-primary";
    public string Niche => "code";
    public string Description => "Code generation, review, and refactoring agent";
    public bool IsActive { get; private set; }

    public CodeAgentAdapter(IServiceProvider sp)
    {
        _sp = sp;
    }

    public async Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct)
    {
        _codeAgent ??= _sp.GetRequiredService<CodeAgent>();
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        var response = await _codeAgent.RunAsync(messages, null, null, ct).ConfigureAwait(false);
        return response?.Text ?? "";
    }

    public Task ActivateAsync(CancellationToken ct) { IsActive = true; return Task.CompletedTask; }
    public Task DeactivateAsync(CancellationToken ct) { IsActive = false; return Task.CompletedTask; }
}

public sealed class EIAAgentAdapter : IAgent
{
    private readonly IServiceProvider _sp;
    private EIAAgent? _eiaAgent;

    public string AgentId => "eia-primary";
    public string Niche => "eia";
    public string Description => "Environmental Impact Assessment agent";
    public bool IsActive { get; set; }

    public EIAAgentAdapter(IServiceProvider sp)
    {
        _sp = sp;
    }

    public async Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct)
    {
        _eiaAgent ??= _sp.GetRequiredService<EIAAgent>();
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        var response = await _eiaAgent.RunAsync(messages, null, null, ct).ConfigureAwait(false);
        return response?.Text ?? "";
    }

    public Task ActivateAsync(CancellationToken ct) { IsActive = true; return Task.CompletedTask; }
    public Task DeactivateAsync(CancellationToken ct) { IsActive = false; return Task.CompletedTask; }
}

public sealed class ChatAgentAdapter : IAgent
{
    private readonly IServiceProvider _sp;
    private ChatAgent? _chatAgent;

    public string AgentId => "chat-primary";
    public string Niche => "chat";
    public string Description => "General conversation agent";
    public bool IsActive { get; set; }

    public ChatAgentAdapter(IServiceProvider sp)
    {
        _sp = sp;
    }

    public async Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct)
    {
        _chatAgent ??= _sp.GetRequiredService<ChatAgent>();
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        var response = await _chatAgent.RunAsync(messages, null, null, ct).ConfigureAwait(false);
        return response?.Text ?? "";
    }

    public Task ActivateAsync(CancellationToken ct) { IsActive = true; return Task.CompletedTask; }
    public Task DeactivateAsync(CancellationToken ct) { IsActive = false; return Task.CompletedTask; }
}

public sealed class ReasoningAgentAdapter : IAgent
{
    private readonly IServiceProvider _sp;
    private ReasoningAgent? _reasoningAgent;

    public string AgentId => "reasoning-primary";
    public string Niche => "reasoning";
    public string Description => "Deep reasoning and chain-of-thought agent";
    public bool IsActive { get; set; }

    public ReasoningAgentAdapter(IServiceProvider sp)
    {
        _sp = sp;
    }

    public async Task<string> HandleAsync(string query, Dictionary<string, object> context, CancellationToken ct)
    {
        _reasoningAgent ??= _sp.GetRequiredService<ReasoningAgent>();
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        var response = await _reasoningAgent.RunAsync(messages, null, null, ct).ConfigureAwait(false);
        return response?.Text ?? "";
    }

    public Task ActivateAsync(CancellationToken ct) { IsActive = true; return Task.CompletedTask; }
    public Task DeactivateAsync(CancellationToken ct) { IsActive = false; return Task.CompletedTask; }
}
