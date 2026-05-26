using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class ServiceDispatcher
{
    private static readonly HashSet<string> _allowedTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LTAIToolRegistry", "UnifiedMapService", "MessageGateway", "SmsGateway",
        "TranslateService", "ImageSearchService", "WeatherService", "AutoUpdater",
        "PkgManager", "ModelManager", "ServiceManager", "DaemonManager",
        "ResourceGuard", "Wsl2Manager", "CodeEditTools", "CodeGraphEnhanced",
        "BuildPipeline", "TestHarness", "ApiToolCatalog"
    };

    private static readonly HashSet<string> _blockedNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.IO", "System.Diagnostics", "System.Net", "System.Reflection",
        "System.Runtime", "System.Threading", "Microsoft.Win32"
    };

    private readonly IServiceProvider _sp;
    private readonly ILogger<ServiceDispatcher> _logger;

    public ServiceDispatcher(IServiceProvider sp, ILogger<ServiceDispatcher> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    public async Task<object?> Invoke(string service_name, string method_name, string? args_json = null)
    {
        var type = FindType(service_name);
        if (type == null)
            return new { error = $"Type '{service_name}' not found in any loaded assembly" };

        if (!_allowedTypeNames.Contains(type.Name))
        {
            _logger.LogWarning("ServiceDispatcher: Blocked invocation of unlisted type '{TypeName}'", type.Name);
            return new { error = $"Type '{type.Name}' is not in the allowed service whitelist" };
        }

        foreach (var ns in _blockedNamespaces)
        {
            if (type.Namespace?.StartsWith(ns, StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogWarning("ServiceDispatcher: Blocked invocation of type '{TypeName}' in blocked namespace '{Namespace}'", type.Name, ns);
                return new { error = $"Type '{type.Name}' is in a blocked namespace" };
            }
        }

        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(m => m.Name.Equals(method_name, StringComparison.OrdinalIgnoreCase) && !m.IsSpecialName);
        if (method == null)
            return new { error = $"Method '{method_name}' not found on {type.Name}" };

        object? instance = null;
        if (!method.IsStatic)
        {
            instance = _sp.GetService(type);
            instance ??= ResolveSingleton(type);
        }

        if (!method.IsStatic && instance == null)
            return new { error = $"Instance of '{type.Name}' could not be resolved (not in DI, no Instance singleton)" };

        var parsedArgs = ParseArgs(method, args_json);
        try
        {
            var result = method.Invoke(instance, parsedArgs);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var resultProp = task.GetType().GetProperty("Result");
                result = resultProp?.GetValue(task);
            }
            return result ?? new { status = "completed" };
        }
        catch (TargetInvocationException ex)
        {
            var innerMsg = ex.InnerException?.Message ?? ex.Message;
            _logger.LogError(ex.InnerException ?? ex, "ServiceDispatcher: Invocation of {Type}.{Method} failed: {Error}", type.Name, method_name, innerMsg);
            return new { error = innerMsg };
        }
    }

    private Type? FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var t in asm.GetTypes())
            {
                if (t.IsAbstract || t.IsInterface) continue;
                if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    (t.FullName != null && t.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return t;
            }
        }
        return null;
    }

    private static object? ResolveSingleton(Type type)
    {
        var prop = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                ?? type.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
        return prop?.GetValue(null);
    }

    private static object?[] ParseArgs(MethodInfo method, string? json)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];

        if (string.IsNullOrEmpty(json))
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].HasDefaultValue)
                    args[i] = parameters[i].DefaultValue;
                else if (parameters[i].ParameterType == typeof(string))
                    args[i] = "";
                else if (parameters[i].ParameterType == typeof(int))
                    args[i] = 0;
            }
            return args;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (root.TryGetProperty(p.Name!, out var element))
                    {
                        args[i] = JsonSerializer.Deserialize(element.GetRawText(), p.ParameterType);
                    }
                    else if (p.HasDefaultValue)
                    {
                        args[i] = p.DefaultValue;
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                var arr = root.EnumerateArray().ToArray();
                for (int i = 0; i < Math.Min(parameters.Length, arr.Length); i++)
                {
                    args[i] = JsonSerializer.Deserialize(arr[i].GetRawText(), parameters[i].ParameterType);
                }
                for (int i = arr.Length; i < parameters.Length; i++)
                {
                    if (parameters[i].HasDefaultValue)
                        args[i] = parameters[i].DefaultValue;
                }
            }
        }
        catch (JsonException)
        {
            for (int i = 0; i < parameters.Length; i++)
                args[i] = json;
        }

        return args;
    }
}
