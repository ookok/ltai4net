using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.System;

public sealed record DaemonConfig
{
    public string ServiceName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string ExecPath { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string RestartPolicy { get; init; } = "always";
}

public sealed record DaemonResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string Platform { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public string Output { get; init; } = "";
}

public sealed class DaemonManager
{
    private readonly ILogger<DaemonManager> _logger;
    private readonly ServiceManager _serviceManager;
    private readonly Lazy<string> _platform;

    public DaemonManager(ILogger<DaemonManager>? logger = null, ServiceManager? serviceManager = null)
    {
        _logger = logger ?? NullLogger<DaemonManager>.Instance;
        _serviceManager = serviceManager ?? new ServiceManager();
        _platform = new Lazy<string>(DetectPlatform);
    }

    public string Platform => _platform.Value;

    public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        return "unknown";
    }

    public bool IsAvailable()
    {
        if (IsLinux) return File.Exists("/usr/bin/systemctl") || File.Exists("/bin/systemctl");
        if (IsMacOS) return File.Exists("/bin/launchctl");
        return IsWindows;
    }

    public async Task<DaemonResult> InstallAsync(DaemonConfig config)
    {
        if (!IsAvailable())
            return Fail(config.ServiceName, "Daemon manager not available on this platform");

        try
        {
            if (IsLinux) return await InstallSystemd(config).ConfigureAwait(false);
            if (IsMacOS) return await InstallLaunchd(config).ConfigureAwait(false);
            if (IsWindows) return await InstallWindowsService(config).ConfigureAwait(false);

            return Fail(config.ServiceName, "Unsupported platform");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install daemon {ServiceName}", config.ServiceName);
            return Fail(config.ServiceName, ex.Message);
        }
    }

    public async Task<DaemonResult> UninstallAsync(string serviceName)
    {
        if (!IsAvailable())
            return Fail(serviceName, "Daemon manager not available on this platform");

        try
        {
            if (IsLinux) return await RunSystemCtl($"disable {serviceName}", serviceName);
            if (IsMacOS) return await RunLaunchCtl($"unload ~/Library/LaunchAgents/{serviceName}.plist", serviceName);

            if (IsWindows)
            {
                var result = await _serviceManager.UninstallAsync().ConfigureAwait(false);
                return new DaemonResult
                {
                    Success = result.Success,
                    Message = result.Message,
                    Platform = Platform,
                    ServiceName = serviceName,
                    Output = result.Output
                };
            }

            return Fail(serviceName, "Unsupported platform");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall daemon {ServiceName}", serviceName);
            return Fail(serviceName, ex.Message);
        }
    }

    public async Task<DaemonResult> StartAsync(string serviceName)
    {
        if (!IsAvailable())
            return Fail(serviceName, "Daemon manager not available on this platform");

        try
        {
            if (IsLinux) return await RunSystemCtl($"start {serviceName}", serviceName);
            if (IsMacOS) return await RunLaunchCtl($"start ~/Library/LaunchAgents/{serviceName}.plist", serviceName);

            if (IsWindows)
            {
                var result = await _serviceManager.StartAsync().ConfigureAwait(false);
                return new DaemonResult
                {
                    Success = result.Success,
                    Message = result.Message,
                    Platform = Platform,
                    ServiceName = serviceName,
                    Output = result.Output
                };
            }

            return Fail(serviceName, "Unsupported platform");
        }
        catch (Exception ex)
        {
            return Fail(serviceName, ex.Message);
        }
    }

    public async Task<DaemonResult> StopAsync(string serviceName)
    {
        if (!IsAvailable())
            return Fail(serviceName, "Daemon manager not available on this platform");

        try
        {
            if (IsLinux) return await RunSystemCtl($"stop {serviceName}", serviceName);
            if (IsMacOS) return await RunLaunchCtl($"stop ~/Library/LaunchAgents/{serviceName}.plist", serviceName);

            if (IsWindows)
            {
                var result = await _serviceManager.StopAsync().ConfigureAwait(false);
                return new DaemonResult
                {
                    Success = result.Success,
                    Message = result.Message,
                    Platform = Platform,
                    ServiceName = serviceName,
                    Output = result.Output
                };
            }

            return Fail(serviceName, "Unsupported platform");
        }
        catch (Exception ex)
        {
            return Fail(serviceName, ex.Message);
        }
    }

    public async Task<DaemonResult> RestartAsync(string serviceName)
    {
        if (!IsAvailable())
            return Fail(serviceName, "Daemon manager not available on this platform");

        try
        {
            if (IsLinux) return await RunSystemCtl($"restart {serviceName}", serviceName);
            if (IsMacOS)
            {
                var stop = await RunLaunchCtl($"stop ~/Library/LaunchAgents/{serviceName}.plist", serviceName);
                await Task.Delay(1000).ConfigureAwait(false);
                var start = await RunLaunchCtl($"start ~/Library/LaunchAgents/{serviceName}.plist", serviceName);
                return new DaemonResult
                {
                    Success = start.Success,
                    Message = start.Success ? "Service restarted" : $"Restart failed",
                    Platform = Platform,
                    ServiceName = serviceName,
                    Output = $"Stop: {stop.Output}\nStart: {start.Output}"
                };
            }

            if (IsWindows)
            {
                var result = await _serviceManager.RestartAsync().ConfigureAwait(false);
                return new DaemonResult
                {
                    Success = result.Success,
                    Message = result.Message,
                    Platform = Platform,
                    ServiceName = serviceName,
                    Output = result.Output
                };
            }

            return Fail(serviceName, "Unsupported platform");
        }
        catch (Exception ex)
        {
            return Fail(serviceName, ex.Message);
        }
    }

    public async Task<DaemonResult> StatusAsync(string serviceName)
    {
        if (!IsAvailable())
            return Fail(serviceName, "Daemon manager not available on this platform");

        try
        {
            if (IsLinux) return await RunSystemCtl($"status {serviceName}", serviceName);
            if (IsMacOS) return await RunLaunchCtl($"list | grep {serviceName}", serviceName);

            if (IsWindows)
            {
                var result = await _serviceManager.StatusAsync().ConfigureAwait(false);
                return new DaemonResult
                {
                    Success = result.Success,
                    Message = result.Message,
                    Platform = Platform,
                    ServiceName = serviceName,
                    Output = result.Output
                };
            }

            return Fail(serviceName, "Unsupported platform");
        }
        catch (Exception ex)
        {
            return Fail(serviceName, ex.Message);
        }
    }

    public async Task<DaemonResult> EnableAutoStart(string serviceName)
    {
        if (!IsAvailable())
            return Fail(serviceName, "Daemon manager not available on this platform");

        try
        {
            if (IsLinux) return await RunSystemCtl($"enable {serviceName}", serviceName);

            if (IsMacOS)
                return new DaemonResult
                {
                    Success = true,
                    Message = "Launchd plist loads automatically on login",
                    Platform = Platform,
                    ServiceName = serviceName
                };

            return Fail(serviceName, "Auto-start only supported on Linux");
        }
        catch (Exception ex)
        {
            return Fail(serviceName, ex.Message);
        }
    }

    public async Task<DaemonResult> DisableAutoStart(string serviceName)
    {
        if (!IsAvailable())
            return Fail(serviceName, "Daemon manager not available on this platform");

        try
        {
            if (IsLinux) return await RunSystemCtl($"disable {serviceName}", serviceName);

            if (IsMacOS)
                return new DaemonResult
                {
                    Success = true,
                    Message = "Use 'launchctl unload' to disable",
                    Platform = Platform,
                    ServiceName = serviceName
                };

            return Fail(serviceName, "Auto-start only supported on Linux");
        }
        catch (Exception ex)
        {
            return Fail(serviceName, ex.Message);
        }
    }

    private async Task<DaemonResult> InstallSystemd(DaemonConfig config)
    {
        var unitPath = $"/etc/systemd/system/{config.ServiceName}.service";
        var execPath = string.IsNullOrEmpty(config.ExecPath)
            ? Environment.ProcessPath ?? "dotnet"
            : config.ExecPath;

        var unitFile = $"""
            [Unit]
            Description={config.Description}
            After=network.target

            [Service]
            Type=simple
            ExecStart={execPath}
            WorkingDirectory={config.WorkingDirectory}
            Restart={config.RestartPolicy}
            RestartSec=10
            StandardOutput=journal
            StandardError=journal

            [Install]
            WantedBy=multi-user.target
            """;

        try
        {
            await File.WriteAllTextAsync(unitPath, unitFile).ConfigureAwait(false);
            _logger.LogInformation("Written systemd unit file: {Path}", unitPath);

            await RunSystemCtl("daemon-reload", config.ServiceName);
            var enableResult = await RunSystemCtl($"enable {config.ServiceName}", config.ServiceName);
            if (!enableResult.Success)
                return enableResult;

            var startResult = await RunSystemCtl($"start {config.ServiceName}", config.ServiceName);
            return new DaemonResult
            {
                Success = true,
                Message = $"Service '{config.DisplayName}' installed and started",
                Platform = Platform,
                ServiceName = config.ServiceName,
                Output = $"{enableResult.Output}\n{startResult.Output}"
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(config.ServiceName, "Root privileges required to write systemd unit file");
        }
    }

    private async Task<DaemonResult> InstallLaunchd(DaemonConfig config)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var launchAgentsDir = Path.Combine(home, "Library", "LaunchAgents");
        Directory.CreateDirectory(launchAgentsDir);

        var plistPath = Path.Combine(launchAgentsDir, $"{config.ServiceName}.plist");
        var execPath = string.IsNullOrEmpty(config.ExecPath)
            ? Environment.ProcessPath ?? "dotnet"
            : config.ExecPath;

        var plistContent = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{EscapeXml(config.ServiceName)}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{EscapeXml(execPath)}</string>
                </array>
                <key>WorkingDirectory</key>
                <string>{EscapeXml(config.WorkingDirectory)}</string>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>StandardOutPath</key>
                <string>{EscapeXml(Path.Combine(home, "Library", "Logs", $"{config.ServiceName}.log"))}</string>
            </dict>
            </plist>
            """;

        await File.WriteAllTextAsync(plistPath, plistContent).ConfigureAwait(false);
        _logger.LogInformation("Written launchd plist: {Path}", plistPath);

        var loadResult = await RunLaunchCtl($"load {plistPath}", config.ServiceName);
        return new DaemonResult
        {
            Success = loadResult.Success,
            Message = loadResult.Success ? $"LaunchAgent '{config.DisplayName}' installed" : loadResult.Message,
            Platform = Platform,
            ServiceName = config.ServiceName,
            Output = loadResult.Output
        };
    }

    private async Task<DaemonResult> InstallWindowsService(DaemonConfig config)
    {
        var result = await _serviceManager.InstallAsync(config.ExecPath).ConfigureAwait(false);

        return new DaemonResult
        {
            Success = result.Success,
            Message = result.Message,
            Platform = Platform,
            ServiceName = config.ServiceName,
            Output = result.Output
        };
    }

    private async Task<DaemonResult> RunSystemCtl(string args, string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return Fail(serviceName, "Failed to start systemctl");

            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            var combined = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return new DaemonResult
            {
                Success = proc.ExitCode == 0,
                Message = proc.ExitCode == 0 ? "Success" : $"systemctl exited with code {proc.ExitCode}",
                Platform = Platform,
                ServiceName = serviceName,
                Output = combined
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "systemctl failed: {Args}", args);
            return Fail(serviceName, ex.Message);
        }
    }

    private async Task<DaemonResult> RunLaunchCtl(string args, string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("launchctl", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return Fail(serviceName, "Failed to start launchctl");

            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            var combined = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

            return new DaemonResult
            {
                Success = proc.ExitCode == 0,
                Message = proc.ExitCode == 0 ? "Success" : $"launchctl exited with code {proc.ExitCode}",
                Platform = Platform,
                ServiceName = serviceName,
                Output = combined
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "launchctl failed: {Args}", args);
            return Fail(serviceName, ex.Message);
        }
    }

    private DaemonResult Fail(string serviceName, string message) =>
        new()
        {
            Success = false,
            Message = message,
            Platform = Platform,
            ServiceName = serviceName
        };

    private static string EscapeXml(string value) =>
        WebUtility.HtmlEncode(value ?? "");
}
