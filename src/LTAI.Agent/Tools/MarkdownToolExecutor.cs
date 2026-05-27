using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.Governors;
using LTAI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Tools;

public sealed class MarkdownToolExecutor
{
    private readonly ILogger<MarkdownToolExecutor> _logger;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IChatClient? _chatClient;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IMicroKernel? _kernel;
    private Func<string, MkTool?>? _toolResolver;

    private static readonly Regex VariablePattern = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);
    private static readonly Regex IfPattern = new(@"\{\{#if\s+(\w+)\}\}(.*?)\{\{/if\}\}", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly HashSet<string> DangerousCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm -rf /", "rm -rf /*", "del /f /s C:\\", "format", "shutdown /s",
        "shutdown -h", ":(){ :|:& };:", "mkfs", "dd if=/dev/zero", "> /dev/sda"
    };

    public MarkdownToolExecutor(
        ILogger<MarkdownToolExecutor> logger,
        IHttpClientFactory? httpClientFactory = null,
        IChatClient? chatClient = null,
        IServiceProvider? serviceProvider = null,
        IMicroKernel? kernel = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _chatClient = chatClient;
        _serviceProvider = serviceProvider;
        _kernel = kernel;
    }

    public void SetToolResolver(Func<string, MkTool?> resolver)
    {
        _toolResolver = resolver;
    }

    public async Task<object?> ExecuteAsync(MkTool tool, Dictionary<string, object?> args)
    {
        try
        {
            var result = tool.Type switch
            {
                MkToolType.Shell => await ExecuteShellAsync(tool, args).ConfigureAwait(false),
                MkToolType.Http => await ExecuteHttpAsync(tool, args).ConfigureAwait(false),
                MkToolType.Compose => await ExecuteComposeAsync(tool, args).ConfigureAwait(false),
                MkToolType.Prompt => await ExecutePromptAsync(tool, args).ConfigureAwait(false),
                MkToolType.Service => await ExecuteServiceAsync(tool, args).ConfigureAwait(false),
                _ => new { error = $"Unknown tool type: {tool.Type}" }
            };

            tool.Evolution.RecordSuccess();
            return result;
        }
        catch (Exception ex)
        {
            tool.Evolution.RecordFailure();
            _logger.LogError(ex, "Tool {ToolName} execution failed", tool.Name);
            return new { error = ex.Message, tool = tool.Name };
        }
    }

    private async Task<object> ExecuteShellAsync(MkTool tool, Dictionary<string, object?> args)
    {
        var command = FillTemplate(tool.Template, args);

        foreach (var dangerous in DangerousCommands)
        {
            if (command.Contains(dangerous, StringComparison.OrdinalIgnoreCase))
                return new { error = $"Blocked dangerous command pattern: {dangerous}", blocked = true };
        }

        string shellExe;
        string[] shellArgs;
        if (OperatingSystem.IsWindows())
        {
            shellExe = "pwsh";
            shellArgs = new[] { "-NoProfile", "-NonInteractive", "-Command", "-" };
        }
        else
        {
            shellExe = "/bin/bash";
            shellArgs = new[] { "--noprofile", "--norc" };
        }

        if (_kernel != null)
        {
            var result = await _kernel.ExecuteAsync(new KernelOp
            {
                Command = shellExe,
                Arguments = string.Join(" ", shellArgs),
                Stdin = command,
                Timeout = TimeSpan.FromSeconds(tool.TimeoutSec)
            }, CancellationToken.None).ConfigureAwait(false);

            return new
            {
                exitCode = result.Success ? 0 : 1,
                stdout = Truncate(result.Data ?? "", tool.MaxOutputLines),
                stderr = Truncate(result.Error ?? "", tool.MaxOutputLines),
                command
            };
        }

        var psi = new ProcessStartInfo
        {
            FileName = shellExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in shellArgs)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        await process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(tool.TimeoutSec));
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var completed = await Task.Run(() => process.WaitForExit(tool.TimeoutSec * 1000), cts.Token)
            .ConfigureAwait(false);

        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            return new { error = $"Command timed out after {tool.TimeoutSec}s", timeout = true };
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new
        {
            exitCode = process.ExitCode,
            stdout = Truncate(stdout, tool.MaxOutputLines),
            stderr = Truncate(stderr, tool.MaxOutputLines),
            command
        };
    }

    private async Task<object> ExecuteHttpAsync(MkTool tool, Dictionary<string, object?> args)
    {
        var url = FillTemplate(tool.Template, args);
        var client = _httpClientFactory?.CreateClient("LTAI") ?? new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(tool.TimeoutSec);

        var request = new HttpRequestMessage(new HttpMethod(tool.HttpMethod), url);

        foreach (var headerLine in tool.HttpHeaders)
        {
            var parts = headerLine.Split(':', 2);
            if (parts.Length == 2)
                request.Headers.TryAddWithoutValidation(parts[0].Trim(), parts[1].Trim());
        }

        if (tool.HttpBody != null && tool.HttpMethod is "POST" or "PUT" or "PATCH")
        {
            var body = FillTemplate(tool.HttpBody, args);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var response = await client.SendAsync(request).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        return new
        {
            status = (int)response.StatusCode,
            headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
            body = Truncate(responseBody, tool.MaxOutputLines)
        };
    }

    private async Task<object> ExecuteComposeAsync(MkTool tool, Dictionary<string, object?> args)
    {
        if (_toolResolver == null)
            return new { error = "Tool resolver not configured for compose execution" };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(tool.TimeoutSec));

        var results = new Dictionary<string, object?>();
        var stepResults = new List<object>();
        var parallelTasks = new List<Task<object?>>();
        var parallelNames = new List<string>();

        foreach (var step in tool.Steps)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();

            if (step.Parallel)
            {
                if (step.ToolRef != null)
                {
                    var resolved = _toolResolver(step.ToolRef);
                    if (resolved == null)
                    {
                        stepResults.Add(new { step = step.Name, error = $"Tool not found: {step.ToolRef}" });
                        continue;
                    }
                    var stepArgs = BuildStepArgs(step, args, results);
                    parallelTasks.Add(ExecuteWithTimeout(resolved, stepArgs, resolved.TimeoutSec));
                    parallelNames.Add(step.Name);
                }
                continue;
            }

            if (parallelTasks.Count > 0)
            {
                var parallelResults = await Task.WhenAll(parallelTasks).ConfigureAwait(false);
                for (int j = 0; j < parallelNames.Count; j++)
                {
                    results[parallelNames[j]] = parallelResults[j];
                    stepResults.Add(new { step = parallelNames[j], output = parallelResults[j] });
                }
                parallelTasks.Clear();
                parallelNames.Clear();
            }

            var stepOutput = await ExecuteStepWithTimeoutAsync(step, args, results, tool.TimeoutSec, timeoutCts.Token)
                .ConfigureAwait(false);
            results[step.Name] = stepOutput;
            stepResults.Add(new { step = step.Name, output = stepOutput });
        }

        if (parallelTasks.Count > 0)
        {
            var parallelResults = await Task.WhenAll(parallelTasks).ConfigureAwait(false);
            for (int j = 0; j < parallelNames.Count; j++)
            {
                results[parallelNames[j]] = parallelResults[j];
                stepResults.Add(new { step = parallelNames[j], output = parallelResults[j] });
            }
        }

        return new { results = stepResults };
    }

    private async Task<object?> ExecuteWithTimeout(MkTool tool, Dictionary<string, object?> args, int timeoutSec)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        var task = ExecuteAsync(tool, args);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSec), cts.Token))
            .ConfigureAwait(false);
        if (completed == task)
            return await task;
        return new { error = $"Step timed out after {timeoutSec}s", timeout = true };
    }

    private async Task<object?> ExecuteStepWithTimeoutAsync(
        ComposeStep step,
        Dictionary<string, object?> globalArgs,
        Dictionary<string, object?> prevResults,
        int timeoutSec,
        CancellationToken ct)
    {
        if (step.ToolRef == null) return new { step = step.Name, status = "skipped", reason = "no tool reference" };

        var resolved = _toolResolver(step.ToolRef);
        if (resolved == null) return new { step = step.Name, error = $"Tool not found: {step.ToolRef}" };

        var stepArgs = BuildStepArgs(step, globalArgs, prevResults);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(timeoutSec, resolved.TimeoutSec)));

        var task = ExecuteAsync(resolved, stepArgs);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSec), CancellationToken.None))
            .ConfigureAwait(false);
        if (completed == task)
            return await task;
        return new { error = $"Compose step '{step.Name}' timed out after {timeoutSec}s", timeout = true };
    }

    private Dictionary<string, object?> BuildStepArgs(
        ComposeStep step,
        Dictionary<string, object?> globalArgs,
        Dictionary<string, object?> prevResults)
    {
        var stepArgs = new Dictionary<string, object?>();
        foreach (var kv in globalArgs)
            stepArgs[kv.Key] = kv.Value;

        if (step.Inputs.Count > 0)
        {
            foreach (var input in step.Inputs)
                stepArgs[input.Key] = input.Value.StartsWith("$", StringComparison.Ordinal)
                    ? prevResults.GetValueOrDefault(input.Value[1..])
                    : input.Value;
        }
        else
        {
            foreach (var kv in prevResults)
                if (!stepArgs.ContainsKey(kv.Key))
                    stepArgs[kv.Key] = kv.Value;
        }

        return stepArgs;
    }

    private async Task<object> ExecuteServiceAsync(MkTool tool, Dictionary<string, object?> args)
    {
        if (_serviceProvider == null)
            return new { error = "No service provider available for service tool execution" };

        if (string.IsNullOrEmpty(tool.ServiceName) || string.IsNullOrEmpty(tool.ServiceMethod))
            return new { error = "ServiceName and ServiceMethod must be specified for service tools" };

        var serviceType = FindServiceType(tool.ServiceName);
        if (serviceType == null)
            return new { error = $"Service type '{tool.ServiceName}' not found in DI container" };

        var method = FindMethod(serviceType, tool.ServiceMethod, args);
        if (method == null)
            return new { error = $"Method '{tool.ServiceMethod}' not found on {serviceType.Name}" };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(tool.TimeoutSec));
        var resultTask = Task.Run(async () =>
        {
            try
            {
                object? instance = null;
                if (!method.IsStatic)
                {
                    instance = _serviceProvider.GetService(serviceType);
                    instance ??= ResolveSingletonInstance(serviceType);
                }

                if (!method.IsStatic && instance == null)
                    return new { error = $"Service instance '{tool.ServiceName}' could not be resolved" };

                var methodArgs = BuildMethodArgs(method, args);
                var result = method.Invoke(instance, methodArgs);

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
                return new { error = ex.InnerException?.Message ?? ex.Message };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }, timeoutCts.Token);

        var completed = await Task.WhenAny(resultTask, Task.Delay(TimeSpan.FromSeconds(tool.TimeoutSec), CancellationToken.None))
            .ConfigureAwait(false);
        if (completed != resultTask)
            return new { error = $"Service tool timed out after {tool.TimeoutSec}s", timeout = true };

        return await resultTask;
    }

    private object? ResolveSingletonInstance(Type type)
    {
        var instanceProp = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        if (instanceProp != null)
            return instanceProp.GetValue(null);

        var currentProp = type.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
        if (currentProp != null)
            return currentProp.GetValue(null);

        return null;
    }

    private Type? FindServiceType(string serviceName)
    {
        if (_serviceProvider == null) return null;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsInterface || type.IsAbstract) continue;
                if (type.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase) ||
                    type.FullName != null && type.FullName.Equals(serviceName, StringComparison.OrdinalIgnoreCase))
                    return type;
            }
        }

        return null;
    }

    private MethodInfo? FindMethod(Type serviceType, string methodName, Dictionary<string, object?> args)
    {
        var methods = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        return methods.FirstOrDefault(m =>
            m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase) &&
            !m.IsSpecialName);
    }

    private object?[] BuildMethodArgs(MethodInfo method, Dictionary<string, object?> args)
    {
        var parameters = method.GetParameters();
        var methodArgs = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (args.TryGetValue(p.Name!, out var val) && val != null)
            {
                methodArgs[i] = Convert.ChangeType(val, p.ParameterType);
            }
            else if (p.HasDefaultValue)
            {
                methodArgs[i] = p.DefaultValue;
            }
        }

        return methodArgs;
    }

    private async Task<object> ExecutePromptAsync(MkTool tool, Dictionary<string, object?> args)
    {
        if (_chatClient == null)
            return new { error = "No chat client available for prompt tool execution" };

        var prompt = FillTemplate(tool.Template, args);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(tool.TimeoutSec));
        try
        {
            var response = await _chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                cancellationToken: cts.Token).ConfigureAwait(false);

            return new
            {
                response = response.Text ?? "",
                responseId = response.ResponseId ?? "",
                usage = response.Usage != null
                    ? new { inputTokens = response.Usage.InputTokenCount, outputTokens = response.Usage.OutputTokenCount }
                    : null as object
            };
        }
        catch (OperationCanceledException)
        {
            return new { error = $"Prompt tool timed out after {tool.TimeoutSec}s", timeout = true };
        }
    }

    public static string FillTemplate(string template, Dictionary<string, object?> vars)
    {
        template = IfPattern.Replace(template, match =>
        {
            var flag = match.Groups[1].Value;
            var content = match.Groups[2].Value;
            if (vars.TryGetValue(flag, out var val))
            {
                var isTruthy = val switch
                {
                    bool b => b,
                    string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Length > 0,
                    int i => i != 0,
                    double d => d != 0,
                    _ => val != null
                };
                return isTruthy ? content : "";
            }
            return "";
        });

        return VariablePattern.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            return vars.TryGetValue(name, out var val) && val != null
                ? val.ToString() ?? ""
                : match.Value;
        });
    }

    public static Dictionary<string, object?> ValidateAndFillArgs(
        MkTool tool,
        Dictionary<string, object?> rawArgs)
    {
        var filled = new Dictionary<string, object?>();

        foreach (var param in tool.Parameters)
        {
            if (rawArgs.TryGetValue(param.Name, out var val) && val != null)
            {
                filled[param.Name] = ConvertParam(param, val);
            }
            else if (param.Required)
            {
                throw new InvalidOperationException(
                    $"Missing required parameter '{param.Name}' for tool '{tool.Name}'");
            }
            else if (param.Default != null)
            {
                filled[param.Name] = ConvertParam(param, param.Default);
            }
        }

        foreach (var kv in rawArgs)
        {
            if (!filled.ContainsKey(kv.Key))
                filled[kv.Key] = kv.Value;
        }

        return filled;
    }

    private static object? ConvertParam(ToolParam param, object val)
    {
        var str = val.ToString() ?? "";
        return param.Type.ToLowerInvariant() switch
        {
            "int" or "integer" => int.TryParse(str, out var i) ? i : str,
            "float" or "double" or "number" => double.TryParse(str, out var d) ? d : str,
            "bool" or "boolean" => bool.TryParse(str, out var b) ? b : str,
            _ => str
        };
    }

    private static string Truncate(string text, int maxLines)
    {
        var lines = text.Split('\n');
        if (lines.Length <= maxLines) return text;
        return string.Join("\n", lines.Take(maxLines)) + $"\n... (truncated, {lines.Length - maxLines} more lines)";
    }
}
