using System.Text.Json;
using System.Text.RegularExpressions;
using LTAI.Core.Governors;

namespace LTAI.Tools.Evolution;

public record SelfDocument(string Title, DateTime GeneratedAt, List<DocSection> Sections, string FullMarkdown);

public record DocSection(string Title, string Content, string Category);

public sealed class SelfDocumenter
{
    public static IMicroKernel? Kernel { get; set; }
    private readonly string _workspace;
    private readonly string _outputDir;

    public SelfDocumenter(string? workspace = null)
    {
        _workspace = workspace ?? Directory.GetCurrentDirectory();
        _outputDir = Path.Combine(_workspace, ".livingtree", "self_docs");
        Directory.CreateDirectory(_outputDir);
    }

    public async Task<SelfDocument> GenerateAsync(Func<string, string, Task<string>>? chatFn = null)
    {
        var title = $"System Report - {DateTime.Now:yyyy-MM-dd HH:mm}";
        var sections = new List<DocSection>();

        sections.Add(new DocSection("Project Overview",
            GatherProjectOverview(), "overview"));

        sections.Add(new DocSection("Architecture",
            GatherArchitecture(), "architecture"));

        sections.Add(new DocSection("Tools Inventory",
            GatherTools(), "tools"));

        sections.Add(new DocSection("Git History",
            GatherGitHistory(), "history"));

        if (chatFn != null)
        {
            var secTasks = new Dictionary<string, string>
            {
                ["security"] = "Analyze security aspects of this project structure and identify potential issues",
                ["recommendations"] = "Suggest improvements based on the project structure and code patterns"
            };

            foreach (var (category, prompt) in secTasks)
            {
                try
                {
                    var content = await chatFn($"doc_{category}",
                        $"{prompt}\n\nProject: {_workspace}\nFiles: {GatherProjectOverview()}");
                    sections.Add(new DocSection(category == "security" ? "Security Analysis" : "Recommendations",
                        content, category));
                }
                catch { /* non-fatal */ }
            }
        }

        var markdown = BuildMarkdown(title, sections);
        var filePath = Path.Combine(_outputDir, $"report_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        File.WriteAllText(filePath, markdown);

        return new SelfDocument(title, DateTime.UtcNow, sections, markdown);
    }

    private string GatherProjectOverview()
    {
        var csFiles = Directory.GetFiles(_workspace, "*.cs", SearchOption.AllDirectories)
            .Count(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"));
        var dirs = Directory.GetDirectories(_workspace)
            .Where(d => !new[] { ".git", ".livingtree", "obj", "bin", "node_modules", ".venv" }
                .Contains(Path.GetFileName(d)))
            .Select(d => $"  - {Path.GetFileName(d)} ({Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories).Length} files)")
            .ToList();

        return $@"Working directory: {_workspace}
Total .cs files: {csFiles}
Directories:
{string.Join("\n", dirs)}";
    }

    private string GatherArchitecture()
    {
        var projects = Directory.GetFiles(_workspace, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return $"### {name}\nPath: `{Path.GetRelativePath(_workspace, f)}`";
            })
            .ToList();
        return string.Join("\n\n", projects);
    }

    private string GatherTools()
    {
        var toolFiles = Directory.GetFiles(_workspace, "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains("Tool") || f.Contains("Engine") || f.Contains("Service") || f.Contains("Analyzer"))
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .Select(f => $"  - {Path.GetFileNameWithoutExtension(f)}")
            .Distinct()
            .ToList();
        return $"{toolFiles.Count} capability files:\n{string.Join("\n", toolFiles)}";
    }

    private string GatherGitHistory()
    {
        try
        {
            if (Kernel != null)
            {
                var result = Kernel.GitOpAsync("log", "--oneline -10", CancellationToken.None).GetAwaiter().GetResult();
                if (result.Success && !string.IsNullOrEmpty(result.Data)) return result.Data;
            }

            var psi = new System.Diagnostics.ProcessStartInfo("git", "log --oneline -10")
            {
                WorkingDirectory = _workspace, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "Git unavailable";
            return proc.StandardOutput.ReadToEnd();
        }
        catch { return "Git unavailable"; }
    }

    private static string BuildMarkdown(string title, List<DocSection> sections)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {title}\n");
        sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        sb.AppendLine("---\n");
        foreach (var section in sections)
        {
            sb.AppendLine($"## {section.Title}");
            sb.AppendLine(section.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
