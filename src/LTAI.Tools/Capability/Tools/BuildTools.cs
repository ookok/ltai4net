using System.ComponentModel;
using System.Text.Json;
using LTAI.Tools.CodeEngine;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.Tools;

[Description("Build pipeline operations: detect, build, parse errors, auto-fix")]
public sealed class BuildTools
{
    private readonly BuildPipeline _pipeline;
    private readonly ILogger<BuildTools> _logger;

    public BuildTools(BuildPipeline pipeline, ILogger<BuildTools>? logger = null)
    {
        _pipeline = pipeline;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BuildTools>.Instance;
    }

    [Description("Run the project build. Auto-detects build system (dotnet/npm/cargo/make/go). Returns structured errors.")]
    public async Task<string> Build(
        [Description("Root path of the project (default: current directory)")] string? path = null,
        [Description("Build configuration (Debug/Release)")] string? configuration = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _pipeline.BuildAsync(path, configuration).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            result.Success,
            buildSystem = result.BuildSystem,
            command = result.Command,
            exitCode = result.ExitCode,
            durationMs = result.DurationMs,
            errorCount = result.ErrorCount,
            warningCount = result.WarningCount,
            errors = result.Errors.Take(20).Select(e => new
            {
                e.File, e.Line, e.Column, e.Code, e.Message, e.Severity, e.Project,
            }).ToList(),
            warnings = result.Warnings.Take(10).Select(w => new
            {
                w.File, w.Line, w.Code, w.Message,
            }).ToList(),
        });
    }

    [Description("Detect the build system used by a project (dotnet, npm, cargo, make, go, java).")]
    public string BuildDetect(
        [Description("Root path of the project")] string? path = null)
    {
        path ??= Directory.GetCurrentDirectory();
        var system = BuildPipeline.DetectBuildSystem(path);

        var indicators = new List<string>();
        if (File.Exists(System.IO.Path.Combine(path, ".sln")))
            indicators.Add("Solution file (.sln)");
        if (Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
            indicators.Add("C# project (.csproj)");
        if (File.Exists(System.IO.Path.Combine(path, "package.json")))
            indicators.Add("package.json");
        if (File.Exists(System.IO.Path.Combine(path, "Cargo.toml")))
            indicators.Add("Cargo.toml");
        if (File.Exists(System.IO.Path.Combine(path, "Makefile")))
            indicators.Add("Makefile");

        return JsonSerializer.Serialize(new { system, indicators, path });
    }

    [Description("Parse build output text for structured errors. Useful when build is run externally.")]
    public string BuildParseErrors(
        [Description("Raw build output text")] string buildOutput,
        [Description("Build system type")] string? buildSystem = null)
    {
        buildSystem ??= "dotnet";

        var (errors, warnings) = ParseOutputDirect(buildOutput, buildSystem);

        return JsonSerializer.Serialize(new
        {
            errorCount = errors.Count,
            warningCount = warnings.Count,
            errors = errors.Select(e => new
            {
                e.File, e.Line, e.Column, e.Code, e.Message,
            }).ToList(),
            warnings = warnings.Select(w => new
            {
                w.File, w.Line, w.Code, w.Message,
            }).ToList(),
        });
    }

    [Description("List all build errors grouped by file. Useful for prioritizing fixes by file.")]
    public async Task<string> BuildErrorsByFile(
        [Description("Root path of the project")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        var errorsByFile = await _pipeline.ErrorsByFileAsync(path ?? Directory.GetCurrentDirectory()).ConfigureAwait(false);
        return JsonSerializer.Serialize(errorsByFile.Select(kvp => new
        {
            file = kvp.Key,
            errorCount = kvp.Value.Count,
            errors = kvp.Value.Take(5).Select(e => new { e.Line, e.Column, e.Code, e.Message }),
        }));
    }

    private static (List<BuildError> errors, List<BuildError> warnings) ParseOutputDirect(
        string output, string buildSystem)
    {
        var pipeline = new BuildPipeline();
        var method = typeof(BuildPipeline).GetMethod("ParseBuildOutput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (method != null)
        {
            var result = method.Invoke(pipeline, new object[] { output, buildSystem });
            if (result is ValueTuple<List<BuildError>, List<BuildError>> tuple)
                return (tuple.Item1, tuple.Item2);
        }
        return (new(), new());
    }
}
