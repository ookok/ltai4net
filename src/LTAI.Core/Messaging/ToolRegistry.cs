using System.Collections.Concurrent;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Messaging;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, Func<Dictionary<string, object?>, Task<object?>>> _tools = new();
    private readonly ConcurrentDictionary<string, AIFunction> _aiTools = new();
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(ILogger<ToolRegistry> logger)
    {
        _logger = logger;
    }

    public Task RegisterAsync(string toolName, Func<Dictionary<string, object?>, Task<object?>> handler, CancellationToken cancellationToken = default)
    {
        _tools[toolName] = handler;
        _aiTools[toolName] = AIFunctionFactory.Create(
            (IReadOnlyList<KeyValuePair<string, object?>> parameters, CancellationToken ct) =>
            {
                var dict = new Dictionary<string, object?>();
                foreach (var kv in parameters)
                    dict[kv.Key] = kv.Value;
                var result = handler(dict).GetAwaiter().GetResult();
                return Task.FromResult<object?>(result);
            }, toolName);
        _logger.LogInformation("Registered tool: {Tool}", toolName);
        return Task.CompletedTask;
    }

    public async Task<object?> InvokeAsync(string toolName, Dictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        if (_tools.TryGetValue(toolName, out var handler))
        {
            _logger.LogInformation("Invoking tool: {Tool}", toolName);
            return await handler(parameters);
        }

        _logger.LogWarning("Tool not found: {Tool}", toolName);
        return new { error = $"Tool '{toolName}' not found" };
    }

    public bool HasTool(string toolName) => _tools.ContainsKey(toolName);
    public IEnumerable<string> ListTools() => _tools.Keys;
    public IEnumerable<AITool> GetAITools() => _aiTools.Values.Cast<AITool>();
}
