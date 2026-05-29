using System.ComponentModel;

namespace LTAI.Agent.Tools;

/// <summary>
/// Extended filesystem operations: copy, move, delete, get_file_info.
/// Complements Tools.cs (read/write/list).
/// </summary>
public sealed class FileTools
{
    private readonly string _ws;

    public FileTools(string ws) => _ws = ws;

    [Description("Copy a file or directory")]
    public string CopyFile(
        [Description("Source path")] string source,
        [Description("Destination path")] string destination)
    {
        var src = Resolve(source);
        var dst = Resolve(destination);
        if (src == null || dst == null) return "Error: Path escape";
        if (!File.Exists(src) && !Directory.Exists(src)) return "Source not found";
        if (File.Exists(dst) || Directory.Exists(dst)) return "Destination already exists";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (Directory.Exists(src))
                CopyDirectoryRecursive(src, dst);
            else
                File.Copy(src, dst);
            return $"Copied {Path.GetFileName(src)} → {Path.GetFileName(dst)}";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Move/rename a file or directory")]
    public string MoveFile(
        [Description("Source path")] string source,
        [Description("Destination path")] string destination)
    {
        var src = Resolve(source);
        var dst = Resolve(destination);
        if (src == null || dst == null) return "Error: Path escape";
        if (!File.Exists(src) && !Directory.Exists(src)) return "Source not found";
        if (File.Exists(dst) || Directory.Exists(dst)) return "Destination already exists";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (Directory.Exists(src))
                Directory.Move(src, dst);
            else
                File.Move(src, dst);
            return $"Moved {Path.GetFileName(src)} → {Path.GetFileName(dst)}";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Delete a file")]
    public string DeleteFile(
        [Description("Path to file")] string path)
    {
        var fp = Resolve(path);
        if (fp == null) return "Error: Path escape";
        if (!File.Exists(fp)) return "File not found";
        try { File.Delete(fp); return $"Deleted {Path.GetFileName(fp)}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Recursively delete a directory")]
    public string DeleteDirectory(
        [Description("Path to directory")] string path,
        [Description("Allow deleting non-empty")] bool recursive = true)
    {
        var fp = Resolve(path);
        if (fp == null) return "Error: Path escape";
        if (!Directory.Exists(fp)) return "Directory not found";
        try
        {
            if (!recursive && Directory.GetFileSystemEntries(fp).Length > 0)
                return "Directory not empty. Use recursive:true";
            Directory.Delete(fp, recursive);
            return $"Deleted directory {Path.GetFileName(fp)}";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Get file or directory metadata (size, modified, type)")]
    public string GetFileInfo(
        [Description("Path")] string path)
    {
        var fp = Resolve(path);
        if (fp == null) return "Error: Path escape";

        if (File.Exists(fp))
        {
            var fi = new FileInfo(fp);
            return $"**{fi.Name}** — file\n- Size: {FormatSize(fi.Length)}\n- Modified: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n- Created: {fi.CreationTime:yyyy-MM-dd HH:mm:ss}\n- Extension: {fi.Extension}";
        }
        if (Directory.Exists(fp))
        {
            var diName = new DirectoryInfo(fp).Name;
            var count = Directory.GetFileSystemEntries(fp).Length;
            var modTime = Directory.GetLastWriteTime(fp);
            var creTime = Directory.GetCreationTime(fp);
            return $"**{diName}** — directory\n- Items: {count}\n- Modified: {modTime:yyyy-MM-dd HH:mm:ss}\n- Created: {creTime:yyyy-MM-dd HH:mm:ss}";
        }
        return "Path not found";
    }

    private string? Resolve(string path) => LTAI.Core.PathUtils.SafeResolvePath(_ws, path);

    private static void CopyDirectoryRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectoryRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1048576.0:F1} MB"
    };
}
