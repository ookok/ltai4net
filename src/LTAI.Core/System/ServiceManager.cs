using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed class ServiceResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Output { get; init; } = "";
}

public sealed class ServiceManager
{
    private readonly ILogger<ServiceManager> _logger;
    private const string ServiceName = "LTAIService";
    private const string DisplayName = "LivingTree AI Agent Service";

    public ServiceManager(ILogger<ServiceManager>? logger = null)
    {
        _logger = logger ?? NullLogger<ServiceManager>.Instance;
    }

    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public async Task<ServiceResult> InstallAsync(string? exePath = null)
    {
        if (!IsWindows)
            return new ServiceResult { Success = false, Message = "Windows Service only supports Windows" };

        exePath ??= Environment.ProcessPath ?? "dotnet";
        var hostDll = Path.Combine(AppContext.BaseDirectory, "LTAI.Host.dll");

        if (!File.Exists(hostDll) && !exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var alt = Path.Combine(AppContext.BaseDirectory, "LTAI.Host.exe");
            if (File.Exists(alt)) exePath = alt;
            else if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "..", "LTAI.Host")))
                hostDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "LTAI.Host", "LTAI.Host.dll"));
        }

        var binPath = File.Exists(hostDll)
            ? $"dotnet \"{hostDll}\""
            : $"\"{exePath}\"";

        var result = await RunScAsync($"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{DisplayName}\"");
        if (result.Success)
        {
            await RunScAsync($"description {ServiceName} \"LivingTree AI Agent — .NET 10 background service\"");
            await RunScAsync($"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");
            _logger.LogInformation("Service installed: {ServiceName}", ServiceName);
            return new ServiceResult { Success = true, Message = $"Service '{DisplayName}' installed. Use `sc start {ServiceName}` to start." };
        }

        return result;
    }

    public async Task<ServiceResult> UninstallAsync()
    {
        if (!IsWindows)
            return new ServiceResult { Success = false, Message = "Windows Service only supports Windows" };

        await StopAsync();
        await Task.Delay(1000);
        var result = await RunScAsync($"delete {ServiceName}");
        return result;
    }

    public async Task<ServiceResult> StartAsync()
    {
        if (!IsWindows)
            return new ServiceResult { Success = false, Message = "Windows Service only supports Windows" };
        return await RunScAsync($"start {ServiceName}");
    }

    public async Task<ServiceResult> StopAsync()
    {
        if (!IsWindows)
            return new ServiceResult { Success = false, Message = "Windows Service only supports Windows" };
        return await RunScAsync($"stop {ServiceName}");
    }

    public async Task<ServiceResult> RestartAsync()
    {
        var stop = await StopAsync();
        await Task.Delay(2000);
        var start = await StartAsync();
        return new ServiceResult
        {
            Success = start.Success,
            Message = start.Success ? "Service restarted" : $"Restart failed. Stop: {stop.Success}, Start: {start.Success}",
            Output = $"Stop: {stop.Output}\nStart: {start.Output}"
        };
    }

    public async Task<ServiceResult> StatusAsync()
    {
        if (!IsWindows)
            return new ServiceResult { Success = false, Message = "Not on Windows" };

        return await RunScAsync($"query {ServiceName}");
    }

    private async Task<ServiceResult> RunScAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return new ServiceResult { Success = false, Message = "Failed to start sc.exe" };

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var combined = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return new ServiceResult
            {
                Success = proc.ExitCode == 0,
                Message = proc.ExitCode == 0 ? "Success" : $"sc.exe exited with code {proc.ExitCode}",
                Output = combined
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "sc.exe failed: {Args}", arguments);
            return new ServiceResult { Success = false, Message = ex.Message };
        }
    }
}
