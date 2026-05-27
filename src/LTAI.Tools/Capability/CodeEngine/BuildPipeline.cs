using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LTAI.Core.Governors;

namespace LTAI.Tools.CodeEngine;

public sealed class BuildResult
{
    public bool Success { get; init; }
    public string BuildSystem { get; init; } = "";
    public string Command { get; init; } = "";
    public int ExitCode { get; init; }
    public double DurationMs { get; init; }
    public List<BuildError> Errors { get; init; } = new();
    public List<BuildError> Warnings { get; init; } = new();
    public string RawOutput { get; init; } = "";
    public int ErrorCount => Errors.Count;
    public int WarningCount => Warnings.Count;
}

public sealed class BuildError
{
    public string File { get; init; } = "";
    public int Line { get; init; }
    public int Column { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string Severity { get; init; } = "";
    public string Project { get; init; } = "";
}

public sealed class BuildFixAttempt
{
    public int Attempt { get; init; }
    public BuildResult Before { get; init; } = null!;
    public BuildResult? After { get; init; }
    public bool Fixed { get; init; }
    public string FixDescription { get; init; } = "";
    public List<string> FilesChanged { get; init; } = new();
}

public sealed class BuildPipeline
{
    private readonly ILogger<BuildPipeline> _logger;
    private readonly IMicroKernel? _kernel;

    private static readonly Regex s_msbuildPattern = new(
        @"^(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>error|warning)\s+(?<code>[A-Z]{2}\d+):\s+(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_tsPattern = new(
        @"^(?<file>[^(]+)\((?<line>\d+),(?<col>\d+)\):\s+error\s+(?<code>TS\d+):\s+(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_esPattern = new(
        @"^\s*(?<file>.+):\s+line\s+(?<line>\d+),\s+col\s+(?<col>\d+),\s+(?<severity>Error|Warning)\s*[-–]\s*(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_cargoBuildPattern = new(
        @"^\s*-->\s*(?<file>[^:]+):(?<line>\d+):(?<col>\d+)\s*$(?:[\s\S]*?)^\s*\d+\s*\|\s*(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex s_genericBuildPattern = new(
        @"^(?<file>[^:]+):(?<line>\d+):(?<col>\d+)?\s*(?<severity>error|warning|Error|Warning)[:\s]+(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public BuildPipeline(ILogger<BuildPipeline>? logger = null, IMicroKernel? kernel = null)
    {
        _logger = logger ?? NullLogger<BuildPipeline>.Instance;
        _kernel = kernel;
    }

    public async Task<BuildResult> BuildAsync(string? rootPath = null, string? configuration = null)
    {
        rootPath ??= Directory.GetCurrentDirectory();
        configuration ??= "Debug";

        var buildSystem = DetectBuildSystem(rootPath);
        var (command, args) = GetBuildCommand(buildSystem, configuration);

        var sw = Stopwatch.StartNew();
        var (exitCode, output) = await RunProcessAsync(command, args, rootPath).ConfigureAwait(false);
        sw.Stop();

        var result = new BuildResult
        {
            Success = exitCode == 0,
            BuildSystem = buildSystem,
            Command = $"{command} {args}",
            ExitCode = exitCode,
            DurationMs = sw.ElapsedMilliseconds,
            RawOutput = output.Length > 10000 ? output[..10000] + "\n... (truncated)" : output,
        };

        var (errors, warnings) = ParseBuildOutput(output, buildSystem);
        result.Errors.AddRange(errors);
        result.Warnings.AddRange(warnings);

        _logger.LogInformation("Build {System}: {Success} ({Errors} errors, {Warnings} warnings) in {Duration}ms",
            buildSystem, result.Success ? "PASS" : "FAIL", result.ErrorCount, result.WarningCount, result.DurationMs);

        return result;
    }

    public async Task<List<BuildFixAttempt>> AutoFixLoopAsync(
        string rootPath,
        Func<List<BuildError>, Task<string>> fixFn,
        int maxIterations = 5,
        CancellationToken cancellationToken = default)
    {
        var attempts = new List<BuildFixAttempt>();

        for (var i = 0; i < maxIterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var before = await BuildAsync(rootPath).ConfigureAwait(false);
            if (before.Success)
            {
                attempts.Add(new BuildFixAttempt
                {
                    Attempt = i + 1, Before = before, After = before, Fixed = true,
                    FixDescription = i == 0 ? "No errors to fix" : "All errors resolved",
                });
                break;
            }

            var fixDescription = await fixFn(before.Errors).ConfigureAwait(false);
            var after = await BuildAsync(rootPath).ConfigureAwait(false);

            attempts.Add(new BuildFixAttempt
            {
                Attempt = i + 1,
                Before = before,
                After = after,
                Fixed = after.Success,
                FixDescription = fixDescription,
            });

            if (after.Success) break;
        }

        return attempts;
    }

    public async Task<Dictionary<string, List<BuildError>>> ErrorsByFileAsync(string rootPath)
    {
        var result = await BuildAsync(rootPath).ConfigureAwait(false);
        return result.Errors
            .GroupBy(e => e.File)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public static string DetectBuildSystem(string rootPath)
    {
        if (File.Exists(Path.Combine(rootPath, ".sln")) ||
            Directory.GetFiles(rootPath, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
            return "dotnet";

        if (Directory.GetFiles(rootPath, "*.sln", SearchOption.AllDirectories).Length > 0 ||
            Directory.GetFiles(rootPath, "*.csproj", SearchOption.AllDirectories).Length > 0)
            return "dotnet";

        if (File.Exists(Path.Combine(rootPath, "package.json")))
            return "npm";

        if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
            return "cargo";

        if (File.Exists(Path.Combine(rootPath, "Makefile")))
            return "make";

        if (File.Exists(Path.Combine(rootPath, "go.mod")))
            return "go";

        if (File.Exists(Path.Combine(rootPath, "pom.xml")) ||
            File.Exists(Path.Combine(rootPath, "build.gradle")))
            return "java";

        return "unknown";
    }

    private static (string command, string args) GetBuildCommand(string buildSystem, string configuration)
    {
        return buildSystem switch
        {
            "dotnet" => ("dotnet", $"build -c {configuration} --nologo"),
            "npm" => ("npm", "run build"),
            "cargo" => ("cargo", "build"),
            "make" => ("make", ""),
            "go" => ("go", "build ./..."),
            "java" => ("mvn", "compile -q"),
            _ => ("dotnet", $"build -c {configuration}")
        };
    }

    private (List<BuildError> errors, List<BuildError> warnings) ParseBuildOutput(string output, string buildSystem)
    {
        var errors = new List<BuildError>();
        var warnings = new List<BuildError>();

        switch (buildSystem)
        {
            case "dotnet":
                ParseDotNetOutput(output, errors, warnings);
                break;
            case "npm":
                ParseNpmOutput(output, errors, warnings);
                break;
            case "cargo":
                ParseCargoOutput(output, errors, warnings);
                break;
            default:
                ParseGenericOutput(output, errors, warnings);
                break;
        }

        return (errors, warnings);
    }

    private static void ParseDotNetOutput(string output, List<BuildError> errors, List<BuildError> warnings)
    {

        foreach (Match m in s_msbuildPattern.Matches(output))
        {
            var error = new BuildError
            {
                File = m.Groups["file"].Value.Trim(),
                Line = int.Parse(m.Groups["line"].Value),
                Column = int.Parse(m.Groups["col"].Value),
                Code = m.Groups["code"].Value,
                Message = m.Groups["message"].Value.Trim(),
                Severity = m.Groups["severity"].Value,
                Project = "dotnet",
            };

            if (error.Severity == "error")
                errors.Add(error);
            else
                warnings.Add(error);
        }
    }

    private static void ParseNpmOutput(string output, List<BuildError> errors, List<BuildError> warnings)
    {

        foreach (Match m in s_tsPattern.Matches(output))
        {
            errors.Add(new BuildError
            {
                File = m.Groups["file"].Value.Trim(),
                Line = int.Parse(m.Groups["line"].Value),
                Column = int.Parse(m.Groups["col"].Value),
                Code = m.Groups["code"].Value,
                Message = m.Groups["message"].Value.Trim(),
                Severity = "error",
                Project = "npm",
            });
        }

        foreach (Match m in s_esPattern.Matches(output))
        {
            var error = new BuildError
            {
                File = m.Groups["file"].Value.Trim(),
                Line = int.Parse(m.Groups["line"].Value),
                Column = int.Parse(m.Groups["col"].Value),
                Code = "",
                Message = m.Groups["message"].Value.Trim(),
                Severity = m.Groups["severity"].Value.ToLowerInvariant(),
                Project = "npm",
            };

            if (error.Severity == "error") errors.Add(error);
            else warnings.Add(error);
        }
    }

    private static void ParseCargoOutput(string output, List<BuildError> errors, List<BuildError> warnings)
    {

        foreach (Match m in s_cargoBuildPattern.Matches(output))
        {
            var msg = m.Groups["message"].Value.Trim();
            if (msg.Contains("error[", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("error:", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new BuildError
                {
                    File = m.Groups["file"].Value.Trim(),
                    Line = int.Parse(m.Groups["line"].Value),
                    Column = int.Parse(m.Groups["col"].Value),
                    Code = "rustc",
                    Message = msg,
                    Severity = "error",
                    Project = "cargo",
                });
            }
            else
            {
                warnings.Add(new BuildError
                {
                    File = m.Groups["file"].Value.Trim(),
                    Line = int.Parse(m.Groups["line"].Value),
                    Column = int.Parse(m.Groups["col"].Value),
                    Code = "rustc",
                    Message = msg,
                    Severity = "warning",
                    Project = "cargo",
                });
            }
        }
    }

    private static void ParseGenericOutput(string output, List<BuildError> errors, List<BuildError> warnings)
    {

        foreach (Match m in s_genericBuildPattern.Matches(output))
        {
            var severity = m.Groups["severity"].Value.ToLowerInvariant();
            var error = new BuildError
            {
                File = m.Groups["file"].Value.Trim(),
                Line = int.Parse(m.Groups["line"].Value),
                Column = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 0,
                Code = "",
                Message = m.Groups["message"].Value.Trim(),
                Severity = severity,
                Project = "generic",
            };

            if (severity.Contains("error")) errors.Add(error);
            else warnings.Add(error);
        }
    }

    private async Task<(int exitCode, string output)> RunProcessAsync(
        string command, string args, string workingDir, int timeoutMs = 120000)
    {
        if (_kernel != null)
        {
            var kResult = await _kernel.ExecuteAsync(new KernelOp
            {
                Command = command,
                Arguments = args,
                WorkingDirectory = workingDir,
                Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            }).ConfigureAwait(false);

            var combinedOutput = (kResult.Data ?? "") + "\n" + (kResult.Error ?? "");

            if (kResult.Success)
                return (0, combinedOutput);

            if (kResult.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true)
                return (-1, combinedOutput + "\n[BUILD TIMED OUT]\n");

            return (-1, combinedOutput);
        }

        var psi = new ProcessStartInfo(command, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };

        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) error.AppendLine(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var completed = await Task.Run(() => proc.WaitForExit(timeoutMs)).ConfigureAwait(false);
        if (!completed)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, output + "\n[BUILD TIMED OUT]\n" + error);
        }

        return (proc.ExitCode, output + "\n" + error);
    }
}
