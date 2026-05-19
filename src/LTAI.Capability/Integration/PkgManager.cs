using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Capability.Integration;

public record PackageResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("installed_at")] DateTime InstalledAt);

public sealed class PkgManager
{
    public static readonly Lazy<PkgManager> Instance = new(() => new PkgManager());

    private readonly ILogger<PkgManager> _logger;

    private PkgManager(ILogger<PkgManager>? logger = null)
    {
        _logger = logger ?? NullLogger<PkgManager>.Instance;
    }

    public async Task<PackageResult> InstallNuGetAsync(string packageId, string? version = null, string? source = null)
    {
        if (string.IsNullOrEmpty(packageId))
            return new PackageResult(packageId, version, false, "nuget", "Package ID is required", DateTime.UtcNow);

        try
        {
            var args = $"add package {packageId}";
            if (!string.IsNullOrEmpty(version))
                args += $" --version {version}";
            if (!string.IsNullOrEmpty(source))
                args += $" --source {source}";

            var (code, output) = await RunDotnetAsync(args);

            var installed = code == 0;
            var installedVersion = version;
            if (installed && string.IsNullOrEmpty(installedVersion))
            {
                installedVersion = ExtractVersion(output, packageId);
            }

            return new PackageResult(
                packageId,
                installedVersion ?? version,
                installed,
                "nuget",
                installed ? null : $"Exit code {code}: {output[..Math.Min(output.Length, 200)]}",
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NuGet install failed: {PackageId}", packageId);
            return new PackageResult(packageId, version, false, "nuget", ex.Message, DateTime.UtcNow);
        }
    }

    public async Task<PackageResult> InstallDotnetToolAsync(string toolName, string? version = null)
    {
        if (string.IsNullOrEmpty(toolName))
            return new PackageResult(toolName, version, false, "dotnet-tool", "Tool name is required", DateTime.UtcNow);

        try
        {
            var args = $"tool install {toolName} -g";
            if (!string.IsNullOrEmpty(version))
                args += $" --version {version}";

            var (code, output) = await RunDotnetAsync(args);

            var success = code == 0;

            return new PackageResult(
                toolName,
                version,
                success,
                "dotnet-tool",
                success ? null : $"Exit code {code}: {output[..Math.Min(output.Length, 200)]}",
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dotnet tool install failed: {ToolName}", toolName);
            return new PackageResult(toolName, version, false, "dotnet-tool", ex.Message, DateTime.UtcNow);
        }
    }

    public async Task<PackageResult> RestoreAsync(string? projectPath = null)
    {
        try
        {
            var path = projectPath ?? ".";
            var args = $"restore \"{path}\"";
            var (code, output) = await RunDotnetAsync(args);

            return new PackageResult(
                path,
                null,
                code == 0,
                "dotnet-restore",
                code == 0 ? null : $"Exit code {code}: {output[..Math.Min(output.Length, 200)]}",
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dotnet restore failed");
            return new PackageResult("restore", null, false, "dotnet-restore", ex.Message, DateTime.UtcNow);
        }
    }

    public async Task<PackageResult> EnsureSdkAsync(string? version = null)
    {
        try
        {
            var (code, output) = await RunDotnetAsync("--version");
            if (code != 0)
                return new PackageResult("dotnet-sdk", null, false, "dotnet-sdk", "dotnet CLI not available", DateTime.UtcNow);

            var installedVersion = output.Trim();

            if (!string.IsNullOrEmpty(version))
            {
                var match = Regex.Match(installedVersion, @"^(\d+\.\d+)");
                var requiredMatch = Regex.Match(version, @"^\d+\.\d+");
                var ok = match.Success && requiredMatch.Success &&
                         double.TryParse(match.Groups[1].Value, out var iv) &&
                         double.TryParse(requiredMatch.Value, out var rv) &&
                         iv >= rv;

                return new PackageResult(
                    "dotnet-sdk",
                    installedVersion,
                    ok,
                    "dotnet-sdk",
                    ok ? null : $"SDK {installedVersion} does not meet requirement {version}",
                    DateTime.UtcNow);
            }

            return new PackageResult("dotnet-sdk", installedVersion, true, "dotnet-sdk", null, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SDK version check failed");
            return new PackageResult("dotnet-sdk", null, false, "dotnet-sdk", ex.Message, DateTime.UtcNow);
        }
    }

    public async Task<List<string>> GetInstalledToolsAsync()
    {
        var tools = new List<string>();
        try
        {
            var (code, output) = await RunDotnetAsync("tool list -g");
            if (code != 0) return tools;

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines.Skip(1))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                    tools.Add(parts[0]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list installed tools");
        }

        return tools;
    }

    private static string? ExtractVersion(string output, string packageId)
    {
        var pattern = $@"\b{Regex.Escape(packageId)}\b[^\d]*(\d+\.\d+\.\d+[\.\-\w]*)";
        var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) error.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        var combined = output.ToString();
        if (process.ExitCode != 0 && error.Length > 0)
            combined += "\n[STDERR]\n" + error;

        return (process.ExitCode, combined.TrimEnd('\n'));
    }
}
