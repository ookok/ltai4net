using System.ComponentModel;

namespace LTAI.Agent.Tools;

/// <summary>
/// File system tools for agents: reading, writing, and listing files
/// with path-escape security validation.
/// </summary>
public sealed class FileSystemTools
{
    private readonly string _ws;
    public FileSystemTools(string ws) => _ws = ws;

    [Description("Read a file")]
    public async Task<string> ReadFile([Description("Path")] string path)
    {
        var fp = Path.GetFullPath(Path.Combine(_ws, path));
        if (!fp.StartsWith(_ws, StringComparison.OrdinalIgnoreCase)) return "Error: path escape";
        return await File.ReadAllTextAsync(fp);
    }

    [Description("Write a file")]
    public async Task<string> WriteFile([Description("Path")] string path, [Description("Content")] string content)
    {
        var fp = Path.GetFullPath(Path.Combine(_ws, path));
        if (!fp.StartsWith(_ws, StringComparison.OrdinalIgnoreCase)) return "Error: path escape";
        Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
        await File.WriteAllTextAsync(fp, content);
        return $"Written {content.Length} bytes";
    }

    [Description("List directory")]
    public string[] ListFiles([Description("Path")] string path)
    {
        var fp = Path.GetFullPath(Path.Combine(_ws, path));
        if (!fp.StartsWith(_ws)) return ["Error: path escape"];
        return Directory.Exists(fp) ? Directory.GetFileSystemEntries(fp).Select(Path.GetFileName).OfType<string>().ToArray() : [];
    }
}
