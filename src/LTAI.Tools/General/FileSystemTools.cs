using System.ComponentModel;
using Microsoft.Agents.AI;

namespace LTAI.Tools.General;

public static class FileSystemTools
{
    [AIFunction("Reads the contents of a file from the local filesystem")]
    public static async Task<string> ReadFileAsync(
        [AIFunctionParameter("Absolute path to the file", Required = true)]
        string path,
        CancellationToken ct = default)
    {
        return await File.ReadAllTextAsync(path, ct);
    }

    [AIFunction("Writes content to a file, creating directories if needed")]
    public static async Task WriteFileAsync(
        [AIFunctionParameter("Absolute path to the file", Required = true)]
        string path,
        [AIFunctionParameter("Content to write to the file", Required = true)]
        string content,
        CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content, ct);
    }

    [AIFunction("Lists files and directories at the given path")]
    public static string ListDirectory(
        [AIFunctionParameter("Path to the directory", Required = true)]
        string path,
        [AIFunctionParameter("Search pattern (e.g. *.cs)")]
        string? pattern = null)
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

    [AIFunction("Checks if a file or directory exists")]
    public static bool Exists(
        [AIFunctionParameter("Path to check", Required = true)]
        string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    [AIFunction("Deletes a file")]
    public static void DeleteFile(
        [AIFunctionParameter("Path to the file to delete", Required = true)]
        string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    [AIFunction("Gets file or directory metadata (size, dates)")]
    public static string GetMetadata(
        [AIFunctionParameter("Path to the file or directory", Required = true)]
        string path)
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
