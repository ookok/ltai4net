namespace LTAI.Core;

/// <summary>
/// Sandbox-safe path resolution shared across ALL file system tools.
/// Guards against: path traversal (../), prefix collision, and symlink escapes.
/// Called by 11 tool classes: <see cref="LTAI.Agent.Tools.CodeAnalysisTools"/>,
/// DirectoryTreeTools, EditFileTools, FileTools, GlobTools, MemoryTools,
/// MultiEditTools, MultimediaTools, OfficeTools, SearchTools, Tools.
/// </summary>
public static class PathUtils
{
    /// <summary>
    /// Resolve and validate a path within workspace <paramref name="ws"/>.
    /// Returns the full resolved path if safe, or <c>null</c> if the path escapes.
    /// <b>Callers:</b> All 11 file-system tool classes listed above — every tool that
    /// touches a file path must route through this method for sandbox enforcement.
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

            // Check prefix — normalized WS ensures /home/project won't match /home/project-extra.
            // Also allow fp == ws (root directory itself, not just children).
            if (fp != ws && !fp.StartsWith(normalizedWs, StringComparison.OrdinalIgnoreCase))
                return null;

            // Check for symlinks that could escape the sandbox
            // Only check existing files — non-existent paths are not symlinks
            if (File.Exists(fp) || Directory.Exists(fp))
            {
                var fileTarget = File.ResolveLinkTarget(fp, true);
                if (fileTarget != null && !fileTarget.FullName.StartsWith(normalizedWs, StringComparison.OrdinalIgnoreCase))
                    return null; // Symlink points outside workspace — reject

                var dirTarget = Directory.ResolveLinkTarget(fp, true);
                if (dirTarget != null && !dirTarget.FullName.StartsWith(normalizedWs, StringComparison.OrdinalIgnoreCase))
                    return null; // Symlink points outside workspace — reject
            }

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
        catch (System.IO.IOException)
        {
            return null; // Symlink resolution or file not found (ResolveLinkTarget throws)
        }
    }

    /// <summary>
    /// Resolve and validate a path, throwing <see cref="UnauthorizedAccessException"/> if unsafe.
    /// <b>Callers:</b> EditFileTools, Tools — convenience wrapper that throws instead of returning null.
    /// </summary>
    public static string RequireSafePath(string ws, string path)
    {
        return SafeResolvePath(ws, path)
            ?? throw new UnauthorizedAccessException($"Path '{path}' escapes the workspace sandbox.");
    }

    /// <summary>
    /// Check that a file is below the maximum allowed size.
    /// Returns null if OK, or an error message string if too large.
    /// Default max: 100 MiB. Called before read_file / write_file to prevent OOM.
    /// <b>Callers:</b> FileTools, MultimediaTools, OfficeTools, SearchTools.
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

    /// <summary>
    /// 尝试解析路径，支持跨沙箱权限确认。
    /// 优先走 SafeResolvePath，越界时检查 PathPermissionStore。
    /// 返回 (解析路径, 越界全路径)。若 resolvedPath != null 可直接使用；
    /// 若 resolvedPath == null 且 deniedFullPath != null，说明需要用户确认。
    /// </summary>
    public static (string? resolvedPath, string? deniedFullPath) TryResolveWithPermission(
        string ws, string path, bool confirm = false)
    {
        var fp = SafeResolvePath(ws, path);
        if (fp != null) return (fp, null);

        // 越界：获取完整路径供确认提示
        string fullPath;
        try { fullPath = Path.GetFullPath(Path.Combine(ws, path)); }
        catch { return (null, null); }

        // 检查是否已授权
        if (PathPermissionStore.IsGranted(fullPath))
            return (fullPath, null);

        if (confirm)
        {
            PathPermissionStore.Grant(fullPath);
            return (fullPath, null);
        }

        return (null, fullPath);
    }

    /// <summary>
    /// 会话级路径权限存储。跨沙箱文件访问需要用户确认后放行。
    /// 调用 <see cref="Grant"/> 授予权限，<see cref="IsGranted"/> 检查权限。
    /// </summary>
    public static class PathPermissionStore
    {
        // Bounded session permission cache — grants are ephemeral (per user session).
        // Max 512 entries; beyond that, evict oldest by clearing.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _grants = new(4, 512);
        private const int GrantMax = 512;
        private static int _grantCount;

        /// <summary>授予对指定路径的跨沙箱访问权限。</summary>
        public static void Grant(string path)
        {
            try
            {
                var fp = Path.GetFullPath(path.Trim().Trim('"', '\''));
                if (Interlocked.Increment(ref _grantCount) > GrantMax) { _grants.Clear(); Interlocked.Exchange(ref _grantCount, 0); }
                _grants[fp] = true;
            }
            catch { }
        }

        /// <summary>检查路径是否已被授予跨沙箱访问权限。</summary>
        public static bool IsGranted(string path)
        {
            try
            {
                var fp = Path.GetFullPath(path.Trim().Trim('"', '\''));
                return _grants.TryGetValue(fp, out var ok) && ok;
            }
            catch { return false; }
        }

        /// <summary>撤销对指定路径的权限。</summary>
        public static void Revoke(string path)
        {
            try { _grants.Remove(Path.GetFullPath(path), out _); }
            catch { }
        }

        /// <summary>清空所有已授予的权限。</summary>
        public static void Clear() => _grants.Clear();
    }
}
