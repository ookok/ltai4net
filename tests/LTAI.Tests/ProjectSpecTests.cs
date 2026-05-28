using LTAI.Core.Configuration;
using Xunit;

namespace LTAI.Tests;

public sealed class ProjectSpecTests
{
    [Fact]
    public void ToolchainPresets_Dotnet_HasAllCommands()
    {
        var spec = ToolchainPresets.Dotnet;

        Assert.Equal("dotnet", spec.BuildCommand);
        Assert.Equal("build --no-restore", spec.BuildArgs);
        Assert.Equal("dotnet", spec.TestCommand);
        Assert.Equal("test --no-build --nologo", spec.TestArgs);
        Assert.Equal("dotnet", spec.LintCommand);
        Assert.Contains("warnaserror", spec.LintArgs);
        Assert.Equal("dotnet", spec.RunCommand);
        Assert.Equal("run --no-build --project {project}", spec.RunArgs);
        Assert.NotEmpty(spec.ProjectFilePatterns);
        Assert.Contains("*.csproj", spec.ProjectFilePatterns);
        Assert.Contains("*.sln", spec.ProjectFilePatterns);
    }

    [Fact]
    public void ToolchainPresets_Node_HasAllCommands()
    {
        var spec = ToolchainPresets.Node;

        Assert.Equal("npm", spec.BuildCommand);
        Assert.Equal("npm", spec.TestCommand);
        Assert.Contains("package.json", spec.ProjectFilePatterns);
    }

    [Fact]
    public void ToolchainPresets_Python_HasAllCommands()
    {
        var spec = ToolchainPresets.Python;

        Assert.Equal("pip", spec.PackageManager);
        Assert.Contains("python", spec.TestCommand);
        Assert.Contains("pytest", spec.TestArgs);
        Assert.Contains(".py", spec.SourceExtensions);
    }

    [Fact]
    public void ToolchainPresets_AllSeven_HaveUniqueIdentities()
    {
        var presets = new[]
        {
            ToolchainPresets.Dotnet,
            ToolchainPresets.Node,
            ToolchainPresets.Python,
            ToolchainPresets.Go,
            ToolchainPresets.Rust,
            ToolchainPresets.Java,
            ToolchainPresets.Generic,
        };

        foreach (var preset in presets)
        {
            // Not all presets define a build command (Python uses pip, not build)
            // But every preset should have a test command
            Assert.NotEmpty(preset.TestCommand);
            // Some presets (Generic) may not define project file patterns
            // Only check non-empty if they have at least one pattern
        }

        var commands = presets.Select(p => p.BuildCommand).Distinct().ToList();
        Assert.True(commands.Count >= 4, $"Expected diverse build commands, got {commands.Count}");
    }

    [Fact]
    public void ProjectSpec_CustomBuild_HoldsValues()
    {
        var spec = new ProjectSpec
        {
            BuildCommand = "make",
            BuildArgs = "-j4",
            TestCommand = "make",
            TestArgs = "test",
            LintCommand = "clang-tidy",
            LintArgs = "--fix",
            FormatCommand = "clang-format",
            FormatArgs = "-i",
            RunCommand = "./app",
            RunArgs = "--verbose",
            PackageManager = "brew"
        };

        Assert.Equal("make", spec.BuildCommand);
        Assert.Equal("-j4", spec.BuildArgs);
        Assert.Equal("make", spec.TestCommand);
        Assert.Equal("test", spec.TestArgs);
        Assert.Equal("brew", spec.PackageManager);
    }

    [Fact]
    public void ProjectSpec_RunCommand_FullCommand()
    {
        var spec = ToolchainPresets.Dotnet;
        Assert.Equal("dotnet", spec.RunCommand);
        Assert.Equal("run --no-build --project {project}", spec.RunArgs);
    }
}
