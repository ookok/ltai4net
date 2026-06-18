namespace LTAI.Desktop.Tests;

public sealed class FileAnalyzerTests
{
    [Theory]
    [InlineData("file.cs", true)]
    [InlineData("file.py", true)]
    [InlineData("file.go", true)]
    [InlineData("file.txt", false)]
    [InlineData("file.md", false)]
    [InlineData("file.json", false)]
    public void IsCodeFile_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileAnalyzer.IsCodeFile(path));
    }

    [Theory]
    [InlineData("file.cs", true)]
    [InlineData("file.txt", true)]
    [InlineData("file.json", true)]
    [InlineData("file.yaml", true)]
    [InlineData("file.dll", false)]
    [InlineData("file.exe", false)]
    [InlineData("file.png", false)]
    public void IsTextFile_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileAnalyzer.IsTextFile(path));
    }

    [Theory]
    [InlineData("project.sln", true)]
    [InlineData("project.csproj", true)]
    [InlineData("project.props", true)]
    [InlineData("project.targets", true)]
    [InlineData("file.cs", false)]
    [InlineData("file.txt", false)]
    public void IsProjectFile_ReturnsExpected(string path, bool expected)
    {
        Assert.Equal(expected, FileAnalyzer.IsProjectFile(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\nonexistent\\path")]
    public void DetectProjectType_UnknownDir_ReturnsUnknown(string dir)
    {
        Assert.Equal("unknown", FileAnalyzer.DetectProjectType(dir));
    }

    [Fact]
    public void DetectProjectType_DotnetSln_ReturnsDotnet()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "test.sln"), "");
        Assert.Equal("dotnet", FileAnalyzer.DetectProjectType(tmp.Path));
    }

    [Fact]
    public void DetectProjectType_NodePackageJson_ReturnsNode()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "package.json"), "{}");
        Assert.Equal("node", FileAnalyzer.DetectProjectType(tmp.Path));
    }

    [Fact]
    public void DetectProjectType_CargoToml_ReturnsRust()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "Cargo.toml"), "");
        Assert.Equal("rust", FileAnalyzer.DetectProjectType(tmp.Path));
    }
}

/// <summary>Temp directory helper for file system tests.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ltai-test", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
