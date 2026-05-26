using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Tools.Tools;

internal static class MdToolBridge
{
    private static IServiceProvider? _sp;
    private static ILogger _logger = NullLogger.Instance;
    private static Type? _toolServiceType;
    private static MethodInfo? _executeMethod;
    private static object? _cachedService;
    private static readonly ConcurrentDictionary<string, bool> _knownMissingTools = new();
    private static readonly ConcurrentDictionary<string, bool> _knownExistingTools = new();

    public static void Initialize(IServiceProvider sp)
    {
        _sp = sp;
        var loggerFactory = sp.GetService<ILoggerFactory>();
        _logger = loggerFactory?.CreateLogger("LTAI.MdToolBridge") ?? NullLogger.Instance;

        if (_toolServiceType != null) return;

        var agentAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "LTAI.Agent");
        if (agentAsm == null)
        {
            _logger.LogWarning("MdToolBridge: LTAI.Agent assembly not found, MD tools unavailable");
            return;
        }

        _toolServiceType = agentAsm.GetType("LTAI.Agent.Tools.ToolService");
        if (_toolServiceType == null)
        {
            _logger.LogWarning("MdToolBridge: ToolService type not found in LTAI.Agent");
            return;
        }

        _executeMethod = _toolServiceType.GetMethod("ExecuteAsync",
            new[] { typeof(string), typeof(Dictionary<string, object?>), typeof(CancellationToken) });
        if (_executeMethod == null)
            _logger.LogWarning("MdToolBridge: ExecuteAsync method not found on ToolService");
    }

    public static async Task<object?> TryExecuteAsync(string toolName, Dictionary<string, object?> args)
    {
        if (_sp == null || _toolServiceType == null || _executeMethod == null)
        {
            if (_knownMissingTools.TryAdd(toolName, true))
                _logger.LogDebug("MdToolBridge: LTAI.Agent not loaded, falling back to C# handler for '{Tool}'", toolName);
            return null;
        }

        if (_knownMissingTools.ContainsKey(toolName) && !_knownExistingTools.ContainsKey(toolName))
            return null;

        if (_cachedService == null)
        {
            _cachedService = _sp.GetService(_toolServiceType);
            if (_cachedService == null)
            {
                _logger.LogWarning("MdToolBridge: ToolService could not be resolved from DI");
                return null;
            }
        }

        try
        {
            var result = _executeMethod.Invoke(_cachedService, new object?[] { toolName, args, CancellationToken.None });

            if (result == null)
            {
                _knownMissingTools.TryAdd(toolName, true);
                _logger.LogDebug("MdToolBridge: No MD tool '{Tool}', falling back to C# handler", toolName);
                return null;
            }

            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var resultProp = task.GetType().GetProperty("Result");
                result = resultProp?.GetValue(task);
            }

            if (result == null)
            {
                _knownMissingTools.TryAdd(toolName, true);
                _logger.LogDebug("MdToolBridge: MD tool '{Tool}' returned null, falling back to C# handler", toolName);
                return null;
            }

            _knownExistingTools.TryAdd(toolName, true);
            return result;
        }
        catch (TargetInvocationException ex)
        {
            _logger.LogWarning(ex.InnerException, "MdToolBridge: MD tool '{Tool}' execution failed, falling back to C# handler", toolName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MdToolBridge: Unexpected error for '{Tool}', falling back to C# handler", toolName);
            return null;
        }
    }
}
