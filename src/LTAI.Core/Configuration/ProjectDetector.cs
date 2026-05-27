using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Core.Configuration;

public sealed class ProjectDetector
{
    private readonly ILogger<ProjectDetector> _logger;
    private readonly string _workspaceRoot;

    public ProjectDetector(string? workspaceRoot = null, ILogger<ProjectDetector>? logger = null)
    {
        _workspaceRoot = workspaceRoot ?? Directory.GetCurrentDirectory();
        _logger = logger ?? NullLogger<ProjectDetector>.Instance;
    }

    public ProjectSpec Detect()
    {
        var presets = new (string Name, ProjectSpec Spec)[]
        {
            ("dotnet", ToolchainPresets.Dotnet),
            ("node", ToolchainPresets.Node),
            ("python", ToolchainPresets.Python),
            ("go", ToolchainPresets.Go),
            ("rust", ToolchainPresets.Rust),
            ("java", ToolchainPresets.Java)
        };

        foreach (var (name, spec) in presets)
        {
            var score = ScorePreset(spec);
            if (score > 0)
            {
                var result = spec with
                {
                    DetectionScore = score,
                    PresetName = name
                };
                _logger.LogInformation("Detected toolchain '{Name}' (score={Score}, patterns={Patterns})",
                    name, score, string.Join(",", spec.ProjectFilePatterns));
                return result;
            }
        }

        _logger.LogInformation("No specific toolchain detected, using generic");
        return ToolchainPresets.Generic with { DetectionScore = 1, PresetName = "generic" };
    }

    public ProjectSpec DetectOrGet(string? presetName = null)
    {
        if (!string.IsNullOrEmpty(presetName))
        {
            var found = presetName.ToLowerInvariant() switch
            {
                "dotnet" => ToolchainPresets.Dotnet,
                "node" => ToolchainPresets.Node,
                "python" => ToolchainPresets.Python,
                "go" => ToolchainPresets.Go,
                "rust" => ToolchainPresets.Rust,
                "java" => ToolchainPresets.Java,
                "generic" => ToolchainPresets.Generic,
                _ => null
            };

            if (found != null)
            {
                var score = ScorePreset(found);
                return found with { DetectionScore = score, PresetName = presetName.ToLowerInvariant() };
            }
            _logger.LogWarning("Preset '{Name}' not found, auto-detecting", presetName);
        }

        return Detect();
    }

    private int ScorePreset(ProjectSpec spec)
    {
        var score = 0;

        foreach (var pattern in spec.ProjectFilePatterns)
        {
            try
            {
                var files = Directory.GetFiles(_workspaceRoot, pattern, SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    score += 10 + files.Length * 2;
                    _logger.LogDebug("Found {Count} files matching '{Pattern}'", files.Length, pattern);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to search for pattern '{Pattern}'", pattern);
            }
        }

        foreach (var ext in spec.SourceExtensions)
        {
            try
            {
                var files = Directory.GetFiles(_workspaceRoot, $"*{ext}", SearchOption.AllDirectories);
                if (files.Length > 0)
                    score += files.Length;
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(spec.BuildCommand) && IsCommandAvailable(spec.BuildCommand)) score += 15;
        if (!string.IsNullOrEmpty(spec.RunCommand) && IsCommandAvailable(spec.RunCommand)) score += 10;

        return score;
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var platform = Environment.OSVersion.Platform;
            var which = platform == PlatformID.Win32NT ? "where" : "which";
            using var proc = new global::System.Diagnostics.Process
            {
                StartInfo = new global::System.Diagnostics.ProcessStartInfo
                {
                    FileName = which,
                    Arguments = command,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
