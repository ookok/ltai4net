using System.ComponentModel;

namespace LTAI.Tools.General;

[Obsolete("Use LTAI.Agent.Tools.FileSystemTools instead. This class is deprecated and will be removed.")]
public static class FileSystemTools
{
    [Description("Reads the contents of a file from the local filesystem")]
    public static async Task<string> ReadFileAsync(
        [Description("Absolute path to the file")] string path,
        CancellationToken ct = default)
    {
        return await File.ReadAllTextAsync(path, ct);
    }

    [Description("Writes content to a file, creating directories if needed")]
    public static async Task WriteFileAsync(
        [Description("Absolute path to the file")] string path,
        [Description("Content to write to the file")] string content,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content, ct);
    }

    [Description("Lists files and directories at the given path")]
    public static string ListDirectory(
        [Description("Path to the directory")] string path,
        [Description("Search pattern (e.g. *.cs)")] string? pattern = null)
    {
        if (!Directory.Exists(path))
            return $"Directory not found: {path}";

        var entries = string.IsNullOrEmpty(pattern)
            ? Directory.GetFileSystemEntries(path)
            : Directory.GetFileSystemEntries(path, pattern);

        var items = entries.Select(e =>
        {
            var isDir = Directory.Exists(e);
            var name = Path.GetFileName(e);
            return isDir ? $"[DIR]  {name}/" : $"[FILE] {name}";
        });

        return string.Join("\n", items.Take(500));
    }

    [Description("Checks if a file or directory exists")]
    public static bool Exists(
        [Description("Path to check")] string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    [Description("Deletes a file")]
    public static void DeleteFile(
        [Description("Path to the file to delete")] string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    [Description("Gets file or directory metadata (size, dates)")]
    public static string GetMetadata(
        [Description("Path to the file or directory")] string path)
    {
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return $"File: {fi.Name}\nSize: {fi.Length:N0} bytes\nCreated: {fi.CreationTime:O}\nModified: {fi.LastWriteTime:O}";
        }
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            return $"Directory: {di.Name}\nCreated: {di.CreationTime:O}\nModified: {di.LastWriteTime:O}";
        }
        return $"Not found: {path}";
    }
}
