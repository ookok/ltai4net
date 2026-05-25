using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace LTAI.Agent.Tools;

[Description("Git repository operations: diff, log, blame")]
public sealed class GitTools
{
    [Description("Show git diff for the current repository. Returns changed files and their diffs.")]
    public static async Task<string> GitDiff(
        [Description("Path to the git repository")] string? repoPath = null,
        [Description("Files to diff, space-separated or empty for all")] string? files = null,
        [Description("Use --staged for staged changes")] bool staged = false,
        CancellationToken cancellationToken = default)
    {
        var args = "diff";
        if (staged) args += " --staged";
        if (!string.IsNullOrWhiteSpace(files)) args += $" -- {files}";
        return await RunGitAsync(repoPath, args, cancellationToken).ConfigureAwait(false);
    }

    [Description("Show git commit log. Returns recent commits with hash, author, date, and message.")]
    public static async Task<string> GitLog(
        [Description("Path to the git repository")] string? repoPath = null,
        [Description("Max number of commits")] int maxCount = 20,
        [Description("Format: oneline, short, medium, full")] string format = "oneline",
        CancellationToken cancellationToken = default)
    {
        var args = $"log --max-count={maxCount} --format=\"%h|%an|%ad|%s\" --date=short";
        var result = await RunGitAsync(repoPath, args, cancellationToken).ConfigureAwait(false);
        if (!result.StartsWith("error"))
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var commits = lines.Select(l =>
            {
                var parts = l.Split('|', 4);
                return new { hash = parts.ElementAtOrDefault(0), author = parts.ElementAtOrDefault(1), date = parts.ElementAtOrDefault(2), message = parts.ElementAtOrDefault(3) };
            }).ToList();
            return JsonSerializer.Serialize(new { repoPath, format, count = commits.Count, commits });
        }
        return result;
    }

    [Description("Show git blame for a file. Returns line-by-line author and commit info.")]
    public static async Task<string> GitBlame(
        [Description("Path to the file relative to repo root")] string filePath,
        [Description("Path to the git repository")] string? repoPath = null,
        CancellationToken cancellationToken = default)
    {
        var args = $"blame --line-porcelain \"{filePath}\"";
        var result = await RunGitAsync(repoPath, args, cancellationToken).ConfigureAwait(false);
        if (!result.StartsWith("error"))
        {
            var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var blamed = new List<object>();
            foreach (var line in lines)
            {
                if (line.Length < 40 || line[0] != '\t') continue;
                blamed.Add(new { content = line[1..] });
            }
            return JsonSerializer.Serialize(new { filePath, lines = blamed.Count, blame = blamed.Take(200) });
        }
        return result;
    }

    private static async Task<string> RunGitAsync(string? repoPath, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(repoPath))
            psi.WorkingDirectory = repoPath;
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return JsonSerializer.Serialize(new { error = "Git not found" });
            var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                return JsonSerializer.Serialize(new { error = stderr.Trim() });
            if (stdout.Length > 20000) stdout = stdout[..20000] + "\n... (truncated)";
            return stdout;
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }
}
