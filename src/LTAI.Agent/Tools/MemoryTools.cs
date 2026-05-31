using System.ComponentModel;
using LTAI.AI;
using LTAI.Core;

namespace LTAI.Agent.Tools;

/// <summary>
/// Persistent memory tools: remember, forget, recall.
/// Ported from DeepSeek-Reasonix memory.ts.
/// Stores memories as .md files in .livingtree/memories/.
/// </summary>
[ToolDomain("memory")]
public sealed class MemoryTools
{
    private const int MaxMemories = 500;
    private static readonly TimeSpan MemoryTtl = TimeSpan.FromDays(365);

    private readonly string _memDir;
    private readonly string _ws;

    public MemoryTools(string ws, string? dataDir = null)
    {
        _ws = ws;
        _memDir = dataDir ?? Path.Combine(ws, ".livingtree", "memories");
        Directory.CreateDirectory(_memDir);
    }

    [Description("保存一条记忆供未来会话使用。记忆会在下次对话开始时自动加载到上下文中。\n"
        + "适用场景：记住用户偏好设置、保存项目相关的重要决策、记录需要长期保留的信息。\n"
        + "不适用场景：临时会话数据（不需要持久化）、文件内容（请用 WriteFile）。\n"
        + "关键参数：name — 记忆名称(3-40字符)；content — 记忆内容；priority — 优先级；scope — 作用域。")]
    [ToolExample("记住我喜欢的代码风格")]
    [ToolExample("保存这个项目的关键决策")]
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

        // Path traversal guard
        if (PathUtils.SafeResolvePath(_ws, Path.Combine(".livingtree", "memories", filename)) == null)
            return "Error: Invalid memory name";

        var header = $"---\nname: {name}\npriority: {priority}\nscope: {scope}\n---\n\n";
        await File.WriteAllTextAsync(filePath, header + content);

        // Prune oldest if over limit
        try
        {
            var allFiles = Directory.GetFiles(_memDir, "*.md");
            if (allFiles.Length > MaxMemories)
            {
                var toDelete = allFiles.OrderBy(f => File.GetLastWriteTime(f)).Take(allFiles.Length - MaxMemories).ToArray();
                foreach (var f in toDelete)
                    try { File.Delete(f); } catch { }
            }
        }
        catch { }

        return $"✅ Remembered '{name}' ({priority} priority, {scope} scope)";
    }

    [Description("删除一条已保存的记忆。\n"
        + "适用场景：清理过时的记忆、删除错误的记录、重置已保存的偏好设置。\n"
        + "关键参数：name — 要删除的记忆名称。")]
    [ToolExample("删除之前保存的那条记忆")]
    public string Forget(
        [Description("Memory name to delete")] string name)
    {
        var filePath = FindMemoryFile(name);
        if (filePath == null) return $"Memory '{name}' not found";

        // Path traversal guard
        if (PathUtils.SafeResolvePath(_ws, Path.Combine(".livingtree", "memories", Path.GetFileName(filePath))) == null)
            return "Error: Invalid memory name";

        File.Delete(filePath);
        return $"🗑️ Forgotten '{name}'";
    }

    [Description("读取一条已保存记忆的完整内容。\n"
        + "适用场景：查看之前保存的关键信息、回忆项目决策依据。\n"
        + "关键参数：name — 要读取的记忆名称。")]
    [ToolExample("看看我之前保存了什么")]
    public async Task<string> RecallMemory(
        [Description("Memory name")] string name)
    {
        var filePath = FindMemoryFile(name);
        if (filePath != null)
        {
            // Skip if beyond TTL
            var lastWrite = File.GetLastWriteTime(filePath);
            if (DateTime.UtcNow - lastWrite > MemoryTtl)
            {
                try { File.Delete(filePath); } catch { }
                filePath = null;
            }
        }

        if (filePath != null)
        {
            var sizeError = PathUtils.CheckFileSize(filePath, maxBytes: 10 * 1024 * 1024);
            if (sizeError != null) return sizeError;

            var content = await File.ReadAllTextAsync(filePath);
            return content;
        }

        // Content-based fallback when no name match found
        var terms = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return $"Memory '{name}' not found";

        var results = new List<(string title, string preview, int score)>();
        foreach (var f in Directory.GetFiles(_memDir, "*.md"))
        {
            try
            {
                var content = File.ReadAllText(f);
                var fileName = Path.GetFileNameWithoutExtension(f);
                var score = terms.Sum(t => content.Count(c => char.ToLowerInvariant(c) == char.ToLowerInvariant(t[0])));
                if (score > 0)
                {
                    var preview = content.Length > 500 ? content[..500] + "..." : content;
                    results.Add(($"📄 {fileName}", preview, score));
                }
            }
            catch { }
        }

        if (results.Count == 0) return $"Memory '{name}' not found";

        results = results.OrderByDescending(r => r.score).Take(5).ToList();
        return string.Join("\n\n", results.Select(r => $"{r.title} (relevance: {r.score})\n{r.preview}"));
    }

    [Description("列出所有已保存的记忆列表。\n"
        + "适用场景：查看有哪些记忆可用、确认记忆名称。\n"
        + "不适用场景：读取单个记忆内容（请用 RecallMemory）。")]
    [ToolExample("我有哪些保存的记忆")]
    public async Task<string> ListMemories()
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

            if (DateTime.UtcNow - fi.LastWriteTimeUtc > MemoryTtl) continue;

            // Read only first few lines to build preview (avoids loading large files)
            string? preview = null;
            try
            {
                using var reader = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true));
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!line.StartsWith("---"))
                    {
                        preview = line.Trim();
                        break;
                    }
                }
            }
            catch { /* skip unreadable files */ }

            if (preview?.Length > 80) preview = preview[..80] + "...";
            sb.AppendLine($"- **{name}** ({FormatSize(fi.Length)}) — {preview ?? ""}");
        }
        return sb.ToString();
    }

    private string? FindMemoryFile(string name)
    {
        var safeName = SanitizeName(name);
        var exact = Path.Combine(_memDir, safeName + ".md");
        if (File.Exists(exact)) return exact;

        // Fuzzy match — only if exact not found
        return Directory.GetFiles(_memDir, "*.md")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                .Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeName(string name)
    {
        // Remove path separators AND dots to prevent path traversal
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Select(c => invalid.Contains(c) || c == '.' ? '_' : c));
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:F1} KB";
}