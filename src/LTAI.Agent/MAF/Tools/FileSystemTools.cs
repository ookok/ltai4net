using System.ComponentModel;
using System.Text.Json;

namespace LTAI.Agent.Tools;

[Description("File system operations: read, write, list, delete, check existence")]
public sealed class FileSystemTools
{
    [Description("Read the content of a file at the given path. Returns the file text.")]
    public static async Task<string> ReadFile(
        [Description("Absolute or relative file path")] string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (content.Length > 50000)
            content = content[..50000] + $"\n... (truncated, total {content.Length} chars)";
        return JsonSerializer.Serialize(new { path, content, length = content.Length });
    }

    [Description("Write text content to a file. Creates parent directories if needed.")]
    public static async Task<string> WriteFile(
        [Description("Target file path")] string path,
        [Description("Text content to write")] string content,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { path, written = content.Length });
    }

    [Description("List files and directories in a given directory path.")]
    public static string ListDirectory(
        [Description("Directory path to list")] string path,
        [Description("Search pattern, e.g. *.cs")] string? pattern = null)
    {
        if (!Directory.Exists(path))
            return JsonSerializer.Serialize(new { error = $"Directory not found: {path}" });
        var items = new List<object>();
        foreach (var dir in Directory.GetDirectories(path))
            items.Add(new { type = "dir", name = Path.GetFileName(dir), path = dir });
        var files = string.IsNullOrEmpty(pattern)
            ? Directory.GetFiles(path)
            : Directory.GetFiles(path, pattern);
        foreach (var file in files.Take(200))
        {
            var info = new FileInfo(file);
            items.Add(new { type = "file", name = Path.GetFileName(file), path = file, size = info.Length, modified = info.LastWriteTimeUtc });
        }
        return JsonSerializer.Serialize(new { path, count = items.Count, items });
    }

    [Description("Delete a file at the given path.")]
    public static string DeleteFile(
        [Description("Path of the file to delete")] string path)
    {
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });
        File.Delete(path);
        return JsonSerializer.Serialize(new { path, deleted = true });
    }

    [Description("Check if a file or directory exists at the given path.")]
    public static string Exists(
        [Description("Path to check")] string path)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        return JsonSerializer.Serialize(new { path, exists });
    }

    [Description("Search for files matching a pattern recursively in a directory.")]
    public static string SearchFiles(
        [Description("Root directory to search")] string rootPath,
        [Description("Search pattern, e.g. *.json")] string pattern,
        [Description("Max number of results")] int maxResults = 50)
    {
        if (!Directory.Exists(rootPath))
            return JsonSerializer.Serialize(new { error = $"Directory not found: {rootPath}" });
        var results = Directory.GetFiles(rootPath, pattern, SearchOption.AllDirectories)
            .Take(maxResults)
            .Select(f => new { name = Path.GetFileName(f), path = f, size = new FileInfo(f).Length })
            .ToList();
        return JsonSerializer.Serialize(new { root = rootPath, pattern, count = results.Count, results });
    }
}
