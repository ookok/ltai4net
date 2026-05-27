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
        Assert.Equal("build", spec.BuildArgs);
        Assert.Equal("dotnet", spec.TestCommand);
        Assert.Equal("test", spec.TestArgs);
        Assert.Equal("dotnet", spec.LintCommand);
        Assert.Contains("format", spec.LintArgs);
        Assert.Equal("dotnet", spec.RunCommand);
        Assert.Equal("run", spec.RunArgs);
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
        Assert.Contains("pytest", spec.TestCommand);
        Assert.Contains("*.py", spec.SourceExtensions);
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
            Assert.NotEmpty(preset.BuildCommand);
            Assert.NotEmpty(preset.TestCommand);
            Assert.NotEmpty(preset.ProjectFilePatterns);
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
        Assert.Equal("run", spec.RunArgs);
    }
}
