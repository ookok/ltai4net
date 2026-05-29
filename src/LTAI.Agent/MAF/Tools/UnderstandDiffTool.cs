using System.Diagnostics;
using LTAI.Core.Governors;
using LTAI.Tools.CodeGraph;

namespace LTAI.Agent.Tools;

[System.ComponentModel.Description("Understand code changes and their impact")]
public static class UnderstandDiffTool
{
    /// <summary>
    /// Simple git-based impact analysis (backward-compatible overload).
    /// </summary>
    public static async Task<string> AnalyzeImpact(string? repoPath = null)
        => await AnalyzeImpact(codeGraph: null, repoPath).ConfigureAwait(false);

    /// <summary>
    /// Enhanced impact analysis using CodeGraph for transitive call graph analysis.
    /// </summary>
    public static async Task<string> AnalyzeImpact(CodeGraphEnhanced? codeGraph, string? repoPath = null)
    {
        repoPath ??= AppContext.BaseDirectory;
        var gitDir = Path.Combine(repoPath, ".git");
        if (!Directory.Exists(gitDir))
            return "No .git directory found. Impact analysis requires a git repository.";

        try
        {
            var files = await GetChangedFilesAsync(repoPath).ConfigureAwait(false);
            if (files.Count == 0) return "No changes detected since last commit.";

            var sb = new System.Text.StringBuilder();
            var affectedDirs = files.Select(f => Path.GetDirectoryName(f)?.Replace('\\', '/')).Where(d => d != null).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
            var byExt = files.GroupBy(Path.GetExtension).ToDictionary(g => g.Key, g => g.Count());
            var riskScore = Math.Min(1.0, files.Count * 0.08 + affectedDirs.Count * 0.04);
            var riskLevel = riskScore > 0.7 ? "HIGH" : riskScore > 0.3 ? "MEDIUM" : "LOW";

            sb.AppendLine($"## Impact Analysis: {riskLevel} Risk");
            sb.AppendLine($"- Changed files: {files.Count}");
            sb.AppendLine($"- Affected directories: {affectedDirs.Count}");

            // Code graph impact: find all callers of changed symbols
            if (codeGraph != null)
            {
                var totalDirect = 0;
                var totalTransitive = 0;
                var allNodes = codeGraph.GetAllNodes();
                foreach (var file in files)
                {
                    var changedSymbols = allNodes
                        .Where(n => n.File.Contains(file, StringComparison.OrdinalIgnoreCase))
                        .Select(n => n.Id).ToHashSet();
                    foreach (var symId in changedSymbols)
                    {
                        var impact = codeGraph.GetImpactRadius(symId, maxDepth: 2);
                        totalDirect += impact.DirectCallers;
                        totalTransitive += impact.TransitiveCallers;
                    }
                }
                sb.AppendLine($"- Call graph: {totalDirect} direct + {totalTransitive} transitive callers");
            }

            foreach (var kv in byExt.OrderByDescending(k => k.Value))
                sb.AppendLine($"- {kv.Key}: {kv.Value}");

            sb.AppendLine();
            sb.AppendLine("### Changed files:");
            foreach (var f in files.Take(10))
                sb.AppendLine($"- {f}");
            if (files.Count > 10)
                sb.AppendLine($"- ... and {files.Count - 10} more");

            return sb.ToString();
        }
        catch (Exception ex) { return $"Impact analysis failed: {ex.Message}"; }
    }

    private static async Task<List<string>> GetChangedFilesAsync(string repoPath)
    {
        if (MicroKernel.Default != null)
        {
            var kResult = await MicroKernel.Default.GitOpAsync("diff", "--name-only HEAD~1", System.Threading.CancellationToken.None).ConfigureAwait(false);
            if (kResult.Success)
                return kResult.Data.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToList();
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git", Arguments = "diff --name-only HEAD~1",
            WorkingDirectory = repoPath, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process == null) return new List<string>();
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToList();
    }
}
