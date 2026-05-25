using System.ComponentModel;
using System.Text.Json;

namespace LTAI.Agent.Tools;

[Description("File system operations: read, write, list, delete, check existence. All operations are confined to the workspace root directory.")]
public sealed class FileSystemTools
{
    private static readonly string _workspaceRoot = Path.GetFullPath(
        Environment.GetEnvironmentVariable("LTAI_WORKSPACE") ?? Directory.GetCurrentDirectory());

    private static string ResolveSafePath(string path)
    {
        var fullPath = Path.GetFullPath(path, _workspaceRoot);
        if (!fullPath.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path '{path}' is outside the workspace root '{_workspaceRoot}'. Only files within the workspace can be accessed.");
        return fullPath;
    }

    [Description("Read the content of a file at the given path (relative to workspace root). Returns the file text.")]
    public static async Task<string> ReadFile(
        [Description("File path relative to workspace root")] string path,
        CancellationToken cancellationToken = default)
    {
        var safePath = ResolveSafePath(path);
        if (!File.Exists(safePath))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });
        var content = await File.ReadAllTextAsync(safePath, cancellationToken).ConfigureAwait(false);
        var originalLength = content.Length;
        if (content.Length > 50000)
            content = content[..50000] + $"\n... (truncated, total {originalLength} chars)";
        return JsonSerializer.Serialize(new { path, content, length = originalLength });
    }

    [Description("Write text content to a file. Creates parent directories if needed.")]
    public static async Task<string> WriteFile(
        [Description("Target file path relative to workspace root")] string path,
        [Description("Text content to write")] string content,
        CancellationToken cancellationToken = default)
    {
        var safePath = ResolveSafePath(path);
        var dir = Path.GetDirectoryName(safePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(safePath, content, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { path, written = content.Length });
    }

    [Description("List files and directories in a given directory path (relative to workspace root).")]
    public static string ListDirectory(
        [Description("Directory path relative to workspace root")] string path,
        [Description("Search pattern, e.g. *.cs")] string? pattern = null)
    {
        var safePath = ResolveSafePath(path);
        if (!Directory.Exists(safePath))
            return JsonSerializer.Serialize(new { error = $"Directory not found: {path}" });
        var items = new List<object>();
        foreach (var dir in Directory.GetDirectories(safePath))
            items.Add(new { type = "dir", name = Path.GetFileName(dir), path = Path.GetRelativePath(_workspaceRoot, dir) });
        var files = string.IsNullOrEmpty(pattern)
            ? Directory.GetFiles(safePath)
            : Directory.GetFiles(safePath, pattern);
        foreach (var file in files.Take(200))
        {
            var info = new FileInfo(file);
            items.Add(new { type = "file", name = Path.GetFileName(file), path = Path.GetRelativePath(_workspaceRoot, file), size = info.Length, modified = info.LastWriteTimeUtc });
        }
        return JsonSerializer.Serialize(new { path, count = items.Count, items });
    }

    [Description("Delete a file at the given path (relative to workspace root).")]
    public static string DeleteFile(
        [Description("Path of the file to delete relative to workspace root")] string path)
    {
        var safePath = ResolveSafePath(path);
        if (!File.Exists(safePath))
            return JsonSerializer.Serialize(new { error = $"File not found: {path}" });
        File.Delete(safePath);
        return JsonSerializer.Serialize(new { path, deleted = true });
    }

    [Description("Check if a file or directory exists at the given path (relative to workspace root).")]
    public static string Exists(
        [Description("Path to check relative to workspace root")] string path)
    {
        var safePath = ResolveSafePath(path);
        var exists = File.Exists(safePath) || Directory.Exists(safePath);
        return JsonSerializer.Serialize(new { path, exists });
    }

    [Description("Search for files matching a pattern recursively in a directory (relative to workspace root).")]
    public static string SearchFiles(
        [Description("Root directory to search relative to workspace root")] string rootPath,
        [Description("Search pattern, e.g. *.json")] string pattern,
        [Description("Max number of results")] int maxResults = 50)
    {
        var safePath = ResolveSafePath(rootPath);
        if (!Directory.Exists(safePath))
            return JsonSerializer.Serialize(new { error = $"Directory not found: {rootPath}" });
        var results = Directory.GetFiles(safePath, pattern, SearchOption.AllDirectories)
            .Take(maxResults)
            .Select(f => new { name = Path.GetFileName(f), path = Path.GetRelativePath(_workspaceRoot, f), size = new FileInfo(f).Length })
            .ToList();
        return JsonSerializer.Serialize(new { root = rootPath, pattern, count = results.Count, results });
    }
}
