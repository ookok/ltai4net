using System.Diagnostics;

namespace LTAI.Desktop;

/// <summary>Pure git operations extracted from TextPadView.Git.
/// All methods are testable with a fake git process or by mocking IProcessRunner.</summary>
public interface IProcessRunner
{
    Task<string?> RunAsync(string fileName, string args, string workingDir, int timeoutMs = 5000);
    string? Run(string fileName, string args, string workingDir, int timeoutMs = 5000);
}

/// <summary>Default implementation using System.Diagnostics.Process.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<string?> RunAsync(string fileName, string args, string workingDir, int timeoutMs = 5000)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch { return null; }
    }

    public string? Run(string fileName, string args, string workingDir, int timeoutMs = 5000)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            process.Start();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(timeoutMs);
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}

/// <summary>Pure git service for repository operations.
/// Can be constructed with a mock IProcessRunner for testing.</summary>
public sealed class GitService
{
    private readonly IProcessRunner _runner;
    private readonly string _workingDir;

    public GitService(IProcessRunner runner, string workingDir)
    {
        _runner = runner;
        _workingDir = workingDir;
    }

    /// <summary>Find the nearest parent directory containing a .git folder.</summary>
    public static string? FindGitDir(string dir)
    {
        var d = new DirectoryInfo(dir);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }

    /// <summary>Get the current branch name.</summary>
    public async Task<string?> GetBranchAsync()
    {
        return await _runner.RunAsync("git", "rev-parse --abbrev-ref HEAD", _workingDir);
    }

    /// <summary>Get git status as parsed file entries.</summary>
    public async Task<List<GitStatusEntry>> GetStatusAsync()
    {
        var output = await _runner.RunAsync("git", "status --porcelain --untracked-files=normal", _workingDir);
        if (output == null) return new();
        return ParseStatus(output);
    }

    /// <summary>Parse `git status --porcelain` output.</summary>
    public static List<GitStatusEntry> ParseStatus(string statusOutput)
    {
        var result = new List<GitStatusEntry>();
        if (string.IsNullOrEmpty(statusOutput)) return result;
        foreach (var line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            var state = line[..2].Trim();
            var file = line[3..].Trim();
            if (file.StartsWith('"') && file.EndsWith('"')) file = file[1..^1];
            result.Add(new GitStatusEntry(file, state));
        }
        return result;
    }

    /// <summary>Get git log (one line per commit).</summary>
    public async Task<string?> GetLogAsync(int count = 10)
    {
        return await _runner.RunAsync("git", $"log --oneline -{count}", _workingDir);
    }

    /// <summary>Get blame info for a file.</summary>
    public async Task<string?> GetBlameAsync(string filePath)
    {
        return await _runner.RunAsync("git", $"blame --line-porcelain \"{filePath}\"", _workingDir, 15000);
    }

    /// <summary>Get diff for a file.</summary>
    public async Task<string?> GetDiffAsync(string? filePath = null)
    {
        var args = filePath != null ? $"diff \"{filePath}\"" : "diff";
        return await _runner.RunAsync("git", args, _workingDir);
    }

    /// <summary>Stage all changes and commit.</summary>
    public async Task<string?> CommitAsync(string message)
    {
        await _runner.RunAsync("git", "add -A", _workingDir);
        return await _runner.RunAsync("git", $"commit -m \"{message.Replace("\"", "\\\"")}\"", _workingDir);
    }
}

/// <summary>A parsed git status entry.</summary>
public sealed record GitStatusEntry(string File, string Status);
