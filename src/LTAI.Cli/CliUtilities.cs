using System.IO;
using System.Text;
using LTAI.Cli.Debug;

namespace LTAI.Cli;

public static class CliUtilities
{
    public static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";
    }

    public static string? FindRootDirectory(string startDir, string markerDir)
    {
        var current = startDir;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, markerDir)))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    public static string GetDocsDir(string subDir)
    {
        var baseDir = AppContext.BaseDirectory;
        var rootDir = FindRootDirectory(baseDir, "docs");
        if (rootDir != null)
            return Path.Combine(rootDir, "docs", subDir);
        return Path.Combine(baseDir, "docs", subDir);
    }
}
