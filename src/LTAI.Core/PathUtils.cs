namespace LTAI.Core;

/// <summary>
/// Sandbox-safe path resolution shared across all file system tools.
/// Guards against: path traversal (../), prefix collision, and symlink escapes.
/// </summary>
public static class PathUtils
{
    /// <summary>
    /// Resolve and validate a path within workspace <paramref name="ws"/>.
    /// Returns the full resolved path if safe, or <c>null</c> if the path escapes the workspace.
    /// </summary>
    public static string? SafeResolvePath(string ws, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Normalize workspace to include trailing separator (prevents prefix collision)
        var normalizedWs = ws.EndsWith(Path.DirectorySeparatorChar) || ws.EndsWith(Path.AltDirectorySeparatorChar)
            ? ws
            : ws + Path.DirectorySeparatorChar;

        try
        {
            var fp = Path.GetFullPath(Path.Combine(ws, path));

            // Check prefix — normalized WS ensures /home/project won't match /home/project-extra
            if (!fp.StartsWith(normalizedWs, StringComparison.OrdinalIgnoreCase))
                return null;

            // Check for symlinks that could escape the sandbox
            var fileTarget = File.ResolveLinkTarget(fp, true);
            if (fileTarget != null && !fileTarget.FullName.StartsWith(normalizedWs, StringComparison.OrdinalIgnoreCase))
                return null; // Symlink points outside workspace — reject

            var dirTarget = Directory.ResolveLinkTarget(fp, true);
            if (dirTarget != null && !dirTarget.FullName.StartsWith(normalizedWs, StringComparison.OrdinalIgnoreCase))
                return null; // Symlink points outside workspace — reject

            return fp;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null; // Invalid characters in path
        }
    }

    /// <summary>
    /// Resolve and validate a path, throwing <see cref="UnauthorizedAccessException"/> if unsafe.
    /// </summary>
    public static string RequireSafePath(string ws, string path)
    {
        return SafeResolvePath(ws, path)
            ?? throw new UnauthorizedAccessException($"Path '{path}' escapes the workspace sandbox.");
    }

    /// <summary>
    /// Check that a file is below the maximum allowed size.
    /// Returns null if OK, or an error message string if too large.
    /// </summary>
    public static string? CheckFileSize(string fp, long maxBytes = 100 * 1024 * 1024)
    {
        try
        {
            var info = new FileInfo(fp);
            if (!info.Exists)
                return $"File not found: '{fp}'";
            if (info.Length > maxBytes)
                return $"File too large: {info.Length:N0} bytes (max {maxBytes:N0}). Use a more targeted approach.";
            return null;
        }
        catch (Exception ex)
        {
            return $"Cannot check file size: {ex.Message}";
        }
    }
}
