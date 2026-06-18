namespace LTAI.Desktop.Tests;

public sealed class GitServiceTests
{
    private sealed class MockRunner : IProcessRunner
    {
        private readonly Dictionary<string, string?> _responses = new(StringComparer.OrdinalIgnoreCase);
        public MockRunner With(string args, string? result) { _responses[args] = result; return this; }
        public Task<string?> RunAsync(string file, string args, string dir, int timeoutMs = 5000)
            => Task.FromResult(_responses.GetValueOrDefault(args));
        public string? Run(string file, string args, string dir, int timeoutMs = 5000)
            => _responses.GetValueOrDefault(args);
    }

    [Fact]
    public async Task GetBranch_ReturnsBranchName()
    {
        var runner = new MockRunner().With("rev-parse --abbrev-ref HEAD", "main");
        var git = new GitService(runner, ".");
        Assert.Equal("main", await git.GetBranchAsync());
    }

    [Fact]
    public async Task GetBranch_GitNotAvailable_ReturnsNull()
    {
        var runner = new MockRunner();
        var git = new GitService(runner, ".");
        Assert.Null(await git.GetBranchAsync());
    }

    [Fact]
    public void FindGitDir_NestedGit_ReturnsRoot()
    {
        using var tmp = new TempDir();
        var nested = System.IO.Path.Combine(tmp.Path, "src", "project");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(System.IO.Path.Combine(tmp.Path, ".git"));
        Assert.Equal(tmp.Path, GitService.FindGitDir(nested));
    }

    [Fact]
    public void FindGitDir_NoGit_ReturnsNull()
    {
        using var tmp = new TempDir();
        Assert.Null(GitService.FindGitDir(tmp.Path));
    }

    [Fact]
    public void ParseStatus_ModifiedFile_ParsesCorrectly()
    {
        var result = GitService.ParseStatus(" M src/test.cs\n?? newfile.txt\n");
        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.File == "src/test.cs" && e.Status == "M");
        Assert.Contains(result, e => e.File == "newfile.txt" && e.Status == "??");
    }

    [Fact]
    public void ParseStatus_EmptyString_ReturnsEmpty()
    {
        Assert.Empty(GitService.ParseStatus(""));
        Assert.Empty(GitService.ParseStatus(null!));
    }

    [Fact]
    public void ParseStatus_QuotedFilename_Unquotes()
    {
        var result = GitService.ParseStatus(" M \"src/my file.cs\"\n");
        Assert.Single(result);
        Assert.Equal("src/my file.cs", result[0].File);
    }

    [Fact]
    public async Task GetStatusAsync_ParsesOutput()
    {
        var runner = new MockRunner().With(
            "status --porcelain --untracked-files=normal",
            " M modified.cs\n?? untracked.py\n");
        var git = new GitService(runner, ".");
        var status = await git.GetStatusAsync();
        Assert.Equal(2, status.Count);
    }

    [Fact]
    public async Task CommitAsync_CallsAddAndCommit()
    {
        var runner = new MockRunner()
            .With("add -A", "")
            .With("commit -m \"test msg\"", "[main 123abc] test msg");
        var git = new GitService(runner, ".");
        var result = await git.CommitAsync("test msg");
        Assert.Contains("test msg", result);
    }

    [Fact]
    public async Task GetLogAsync_ReturnsLog()
    {
        var runner = new MockRunner().With("log --oneline -10", "abc123 fix bug\nbcd456 add feature");
        var git = new GitService(runner, ".");
        var log = await git.GetLogAsync(10);
        Assert.Contains("abc123", log);
    }

    [Fact]
    public async Task GetLogAsync_CustomCount_Works()
    {
        var runner = new MockRunner().With("log --oneline -5", "abc123 fix");
        var git = new GitService(runner, ".");
        Assert.NotNull(await git.GetLogAsync(5));
    }
}
