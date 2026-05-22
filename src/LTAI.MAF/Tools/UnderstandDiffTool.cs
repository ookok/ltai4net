using System.Diagnostics;

namespace LTAI.MAF.Tools;

public static class UnderstandDiffTool
{
    public static async Task<string> AnalyzeImpact(string? repoPath = null)
    {
        repoPath ??= AppContext.BaseDirectory;
        var gitDir = Path.Combine(repoPath, ".git");

        if (!Directory.Exists(gitDir))
            return "No .git directory found. Impact analysis requires a git repository.";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git", Arguments = "diff --name-only HEAD~1",
                WorkingDirectory = repoPath, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return "Failed to start git process.";
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var files = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToList();
            if (files.Count == 0) return "No changes detected since last commit.";

            var affectedDirs = new HashSet<string>();
            var byExt = new Dictionary<string, int>();
            foreach (var f in files)
            {
                var dir = Path.GetDirectoryName(f)?.Replace('\\', '/');
                if (dir != null) affectedDirs.Add(dir);
                var ext = Path.GetExtension(f);
                byExt[ext] = byExt.GetValueOrDefault(ext) + 1;
            }

            var score = Math.Min(1.0, files.Count * 0.08 + affectedDirs.Count * 0.04);
            var riskLevel = score > 0.7 ? "HIGH" : score > 0.3 ? "MEDIUM" : "LOW";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## Impact Analysis: {riskLevel} Risk (score: {score:P0})");
            sb.AppendLine($"- Changed files: {files.Count}");
            sb.AppendLine($"- Affected directories: {affectedDirs.Count}");
            sb.AppendLine($"- Estimated ripple: ~{affectedDirs.Count * 3} files");
            sb.AppendLine();
            sb.AppendLine("### By file type:");
            foreach (var kv in byExt.OrderByDescending(k => k.Value))
                sb.AppendLine($"- {kv.Key}: {kv.Value}");
            sb.AppendLine();
            sb.AppendLine("### Changed files:");
            foreach (var f in files.Take(15))
                sb.AppendLine($"- {f}");
            if (files.Count > 15)
                sb.AppendLine($"- ... and {files.Count - 15} more");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Impact analysis failed: {ex.Message}";
        }
    }
}
