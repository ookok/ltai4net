using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Messaging;

public sealed class AIToolRegistry
{
    private readonly ConcurrentDictionary<string, AITool> _tools = new();
    private readonly ILogger<AIToolRegistry> _logger;

    public AIToolRegistry(ILogger<AIToolRegistry> logger)
    {
        _logger = logger;
    }

    public Task RegisterAsync(string toolName, Func<Dictionary<string, object?>, Task<object?>> handler, CancellationToken cancellationToken = default)
    {
        var aiFunc = AIFunctionFactory.Create(
            (IReadOnlyList<KeyValuePair<string, object?>> parameters, CancellationToken ct) =>
            {
                var dict = new Dictionary<string, object?>();
                foreach (var kv in parameters)
                    dict[kv.Key] = kv.Value;
                return handler(dict);
            }, toolName);

        _tools[toolName] = aiFunc;
        _logger.LogInformation("Registered tool: {Tool}", toolName);
        return Task.CompletedTask;
    }

    public void RegisterTool(string name, AITool tool)
    {
        _tools[name] = tool;
        _logger.LogInformation("Registered AITool: {Tool}", name);
    }

    public async Task<object?> InvokeAsync(string toolName, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        if (_tools.TryGetValue(toolName, out var tool) && tool is AIFunction func)
        {
            _logger.LogInformation("Invoking tool: {Tool}", toolName);
            var funcArgs = new AIFunctionArguments(
                new Dictionary<string, object?>(parameters!));
            return await func.InvokeAsync(funcArgs, cancellationToken);
        }

        _logger.LogWarning("Tool not found: {Tool}", toolName);
        return new { error = $"Tool '{toolName}' not found" };
    }

    public bool HasTool(string toolName) => _tools.ContainsKey(toolName);

    public IEnumerable<string> ListTools() => _tools.Keys;

    public IEnumerable<AITool> GetTools() => _tools.Values;

    public AITool? GetTool(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;
}
