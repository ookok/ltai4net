using System.Collections.Concurrent;
using LTAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LTAI.Core.Messaging;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, Func<Dictionary<string, object?>, Task<object?>>> _tools = new();
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(ILogger<ToolRegistry> logger)
    {
        _logger = logger;
    }

    public Task RegisterAsync(string toolName, Func<Dictionary<string, object?>, Task<object?>> handler, CancellationToken cancellationToken = default)
    {
        _tools[toolName] = handler;
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
}
