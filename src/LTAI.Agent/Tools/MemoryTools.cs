using System.ComponentModel;

namespace LTAI.Agent.Tools;

/// <summary>
/// Persistent memory tools: remember, forget, recall.
/// Ported from DeepSeek-Reasonix memory.ts.
/// Stores memories as .md files in .livingtree/memories/.
/// </summary>
public sealed class MemoryTools
{
    private readonly string _memDir;

    public MemoryTools(string ws)
    {
        _memDir = Path.Combine(ws, ".livingtree", "memories");
        Directory.CreateDirectory(_memDir);
    }

    [Description("Save a memory for future sessions")]
    public async Task<string> Remember(
        [Description("Memory name (3-40 chars)")] string name,
        [Description("Content body")] string content,
        [Description("Priority: low, medium, high")] string priority = "medium",
        [Description("Scope: global or project")] string scope = "project")
    {
        if (name.Length < 2 || name.Length > 60)
            return "Name must be 2-60 chars";

        var filename = SanitizeName(name) + ".md";
        var filePath = Path.Combine(_memDir, filename);

        var header = $"---\nname: {name}\npriority: {priority}\nscope: {scope}\n---\n\n";
        await File.WriteAllTextAsync(filePath, header + content);

        return $"✅ Remembered '{name}' ({priority} priority, {scope} scope)";
    }

    [Description("Delete a saved memory")]
    public string Forget(
        [Description("Memory name to delete")] string name)
    {
        var filePath = FindMemoryFile(name);
        if (filePath == null) return $"Memory '{name}' not found";

        File.Delete(filePath);
        return $"🗑️ Forgotten '{name}'";
    }

    [Description("Recall the content of a saved memory")]
    public async Task<string> RecallMemory(
        [Description("Memory name")] string name)
    {
        var filePath = FindMemoryFile(name);
        if (filePath == null) return $"Memory '{name}' not found";

        var content = await File.ReadAllTextAsync(filePath);
        return content;
    }

    [Description("List all saved memories")]
    public string ListMemories()
    {
        if (!Directory.Exists(_memDir))
            return "No memories stored yet.";

        var files = Directory.GetFiles(_memDir, "*.md");
        if (files.Length == 0) return "No memories stored yet.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Stored Memories\n");
        foreach (var f in files.OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var fi = new FileInfo(f);
            var preview = File.ReadAllText(f).Split('\n').FirstOrDefault(l => !l.StartsWith("---"))?.Trim();
            if (preview?.Length > 80) preview = preview[..80] + "...";
            sb.AppendLine($"- **{name}** ({FormatSize(fi.Length)}) — {preview ?? ""}");
        }
        return sb.ToString();
    }

    private string? FindMemoryFile(string name)
    {
        var exact = Path.Combine(_memDir, SanitizeName(name) + ".md");
        if (File.Exists(exact)) return exact;

        // Fuzzy match
        return Directory.GetFiles(_memDir, "*.md")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                .Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:F1} KB";
}
