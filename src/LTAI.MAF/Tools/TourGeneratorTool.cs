namespace LTAI.MAF.Tools;

public static class TourGeneratorTool
{
    public static Task<string> GenerateTour(string? repoPath = null)
    {
        repoPath ??= AppContext.BaseDirectory;

        string[] priorityExts = { ".sln", ".csproj", ".json", ".cs", ".java", ".go", ".rs", ".cpp", ".py", ".js", ".ts", ".jsx", ".tsx", ".md", ".html", ".css", ".xml", ".yaml", ".yml" };

        var entries = Directory.GetFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(repoPath, f).Replace('\\', '/');
                return !rel.StartsWith(".git/") && !rel.StartsWith("bin/") &&
                       !rel.StartsWith("obj/") && !rel.StartsWith(".livingtree/") &&
                       !rel.StartsWith("node_modules/");
            })
            .Select(f => new { Path = f, Rel = Path.GetRelativePath(repoPath, f).Replace('\\', '/'), Ext = Path.GetExtension(f) })
            .OrderBy(f => Array.IndexOf(priorityExts, f.Ext) is int i && i >= 0 ? i : 99)
            .ThenBy(f => f.Rel.Count(c => c == '/'))
            .Take(50)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Guided Architecture Tour");
        sb.AppendLine();
        sb.AppendLine("Follow this path to understand the codebase layer by layer:");
        sb.AppendLine();

        int order = 0;
        foreach (var entry in entries)
        {
            order++;
            var depth = entry.Rel.Count(c => c == '/');
            var indent = new string(' ', Math.Min(depth, 5) * 2);
            var description = entry.Ext switch
            {
                ".sln" => "[STRUCTURE] Solution file — project definition",
                ".csproj" => "[CONFIG] Project file — dependencies, framework, build",
                ".json" => entry.Rel.Contains("appsettings") ? "[CONFIG] Application configuration" : "[DATA] Data/configuration",
                ".cs" => "[CODE] C# source",
                ".java" => "[CODE] Java source",
                ".go" => "[CODE] Go source",
                ".rs" => "[CODE] Rust source",
                ".cpp" or ".c" or ".h" => "[CODE] C/C++ source",
                ".py" => "[SCRIPT] Python script",
                ".js" or ".jsx" => "[CODE] JavaScript module",
                ".ts" or ".tsx" => "[CODE] TypeScript module",
                ".md" => "[DOC] Documentation",
                ".html" => "[UI] HTML template",
                ".css" => "[UI] Stylesheet",
                ".xml" or ".yaml" or ".yml" => "[CONFIG] Configuration file",
                _ => "[FILE] Project file"
            };
            sb.AppendLine($"  {order,3}. {indent}{description}");
            sb.AppendLine($"       {entry.Rel}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine($"**Tour complete:** {order} files across the dependency chain.");
        sb.AppendLine("Start from the project root and follow dependencies layer by layer.");

        return Task.FromResult(sb.ToString());
    }
}
