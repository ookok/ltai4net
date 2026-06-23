using LTAI.Agent.Caching;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using LTAI.AI;
using LTAI.Core.Configuration;
using LTAI.Agent.Utils;
using LTAI.Core;

namespace LTAI.Agent.Tools;

[ToolDomain("filesystem")]
public sealed class FileSystemTools
{
    private const int MaxChildren = 50;

    private readonly string _ws;
    private readonly MmapFileProvider? _mmap;
    private readonly WriteBuffer? _writeBuf;
    public FileSystemTools(string ws, MmapFileProvider? mmap = null, WriteBuffer? writeBuf = null)
    {
        _ws = ws;
        _mmap = mmap;
        _writeBuf = writeBuf;
    }

    // ========== READ / WRITE / LIST ==========

    [Description("读取文件内容。支持项目内路径和已授权的外部路径。\n"
        + "适用场景：查看源代码、阅读配置文件、检查日志文件、查看文档内容。\n"
        + "不适用场景：搜索文件内容（请用 SearchContent）、获取文件属性（请用 GetFileInfo）。\n"
        + "关键参数：path — 文件路径（相对于项目根目录或绝对路径）；"
        + "startLine/endLine — 可选，仅读取指定行范围（高效，不加载整个文件）；"
        + "maxChars — 可选，最大读取字符数（默认 10000，设为 0 表示不限制）。")]
    public async Task<string> ReadFileContent(
        string path,
        [Description("起始行号（从 1 开始，默认 1）")] int startLine = 1,
        [Description("结束行号（默认文件末尾）")] int? endLine = null,
        [Description("最大读取字符数（0=不限，默认 10000）")] int maxChars = 10000,
        CancellationToken ct = default)
    {
        try
        {
            var (fp, denied) = PathUtils.TryResolveWithPermission(_ws, path);
            if (denied != null)
                return ToolResult.Error($"Path '{denied}' is outside workspace.");
            if (fp == null) return ToolResult.Error("path escape");
 
            var fi = new FileInfo(fp);
            var totalSizeKb = fi.Length / 1024;

            // Range read: stream only requested lines, skip the rest
            if (startLine > 1 || endLine.HasValue)
            {
                var sb = new StringBuilder();
                using var fs = File.OpenRead(fp);
                using var sr = new StreamReader(fs);
                int lineNum = 1;
                int targetEnd = endLine ?? int.MaxValue;
                int charCount = 0;
                while (lineNum < startLine && await sr.ReadLineAsync(ct).ConfigureAwait(false) != null)
                    lineNum++;
                while (lineNum <= targetEnd)
                {
                    var line = await sr.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null) break;
                    sb.AppendLine(line);
                    charCount += line.Length + Environment.NewLine.Length;
                    if (maxChars > 0 && charCount > maxChars)
                    {
                        sb.AppendLine($"... [truncated at {maxChars} chars, file is {totalSizeKb}KB]");
                        break;
                    }
                    lineNum++;
                }
                var result = sb.ToString();
                return $"[file: {fp}, lines {startLine}–{lineNum - 1}, ~{totalSizeKb}KB total]\n{result}";
            }

            // Progressive read: stream the first portion for large files
            if (totalSizeKb > 1024) // files larger than 1MB
            {
                var sb = new StringBuilder();
                using var fs = File.OpenRead(fp);
                using var sr = new StreamReader(fs);
                int lineNum = 1;
                int charCount = 0;
                var readLimit = maxChars > 0 ? Math.Min(maxChars, 100000) : 100000;
                string? lineContent = null;
                while (charCount < readLimit)
                {
                    lineContent = await sr.ReadLineAsync(ct).ConfigureAwait(false);
                    if (lineContent == null) break;
                    sb.AppendLine(lineContent);
                    charCount += lineContent.Length + Environment.NewLine.Length;
                    lineNum++;
                }
                var hasMore = lineContent != null;
                var result = sb.ToString();
                var ext = fi.Extension.ToLowerInvariant();
                var summary = DescribeDoc(result, ext);
                var note = hasMore
                    ? $"\n... [progressive load: showing {charCount}/{fi.Length} chars ({lineNum} lines). Use startLine/endLine to read more.]"
                    : "";
                return $"[file: {fp}, {totalSizeKb}KB, ~{lineNum} lines — {summary}]{note}\n{result}";
            }

            var sizeError = PathUtils.CheckFileSize(fp);
            if (sizeError != null) return sizeError;
            var content = _mmap != null
                ? await _mmap.ReadAllTextAsync(fp).ConfigureAwait(false)
                : await File.ReadAllTextAsync(fp, ct).ConfigureAwait(false);
            var ext2 = fi.Extension.ToLowerInvariant();
            var summary2 = DescribeDoc(content, ext2);
            var effectiveMax = maxChars > 0 ? maxChars : int.MaxValue;
            if (content.Length > effectiveMax)
                return $"[file: {fp}, {totalSizeKb}KB, {content.Length} chars — {summary2}]\n{ContentTruncator.Truncate(content, effectiveMax)}";
            return $"[file: {fp}, {totalSizeKb}KB, {content.Length} chars — {summary2}]\n{content}";
        }
        catch (Exception ex)
        {
            return ToolResult.FromException(ex, $"reading '{path}'");
        }
    }

    private static readonly System.Text.RegularExpressions.Regex MdHeadingRx = new(@"^#{1,6}\s", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex HtmlTagRx = new(@"<(\w+)", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string DescribeDoc(string content, string ext)
    {
        if (ext is ".json" or ".jsonc")
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(content);
                var count = doc.RootElement.EnumerateObject().Count();
                var arrLen = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? doc.RootElement.GetArrayLength() : 0;
                if (arrLen > 0) return $"JSON 数组 ({arrLen} 项)";
                return $"JSON 对象 ({count} 个顶级键)";
            }
            catch { return "JSON (解析失败)"; }
        }
        var lines = content.AsSpan().Count('\n') + 1;
        if (ext is ".md" or ".markdown")
        {
            var headings = MdHeadingRx.Matches(content).Count;
            return $"Markdown ({lines} 行, {headings} 个标题)";
        }
        if (ext is ".html" or ".htm")
        {
            var tagCount = HtmlTagRx.Matches(content).Count;
            return $"HTML ({lines} 行, ~{tagCount} 标签)";
        }
        return $"{lines} 行";
    }

    [Description("写入/创建文件。用于创建新文件或覆盖已有文件内容。\n"
        + "适用场景：创建新的源代码文件、写入配置文件、生成文档、输出处理结果。\n"
        + "不适用场景：修改文件中的部分内容（请用 EditFile）、追加到已有文件。\n"
        + "关键参数：path — 文件路径；content — 文件内容。注意：使用原子写入（tmp + File.Move），防止中途崩溃导致文件损坏。")]
    public async Task<string> WriteFile(string path, string content)
    {
        var fp = PathUtils.SafeResolvePath(_ws, path);
            if (fp == null) return ToolResult.Error("path escape");

        if (_writeBuf != null)
        {
            await _writeBuf.WriteAsync(fp, content).ConfigureAwait(false);
            var isNew = !File.Exists(fp);
            EditLedger.Default.RecordEdit(fp, isNew);
            await RecordDeltaIfAvailable(fp, content, isNew).ConfigureAwait(false);
            return $"Written {content.Length} bytes to {Path.GetFileName(fp)}";
        }

        // Fallback: atomic write to disk
        Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
        var tmp = fp + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await File.WriteAllTextAsync(tmp, content).ConfigureAwait(false);
            var isNew = !File.Exists(fp);
            File.Move(tmp, fp, overwrite: true);
            EditLedger.Default.RecordEdit(fp, isNew);
            await RecordDeltaIfAvailable(fp, content, isNew).ConfigureAwait(false);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
        return $"Written {content.Length} bytes to {Path.GetFileName(fp)}";
    }

    [Description("列出目录内容。返回目录下的文件和子目录名列表。\n"
        + "适用场景：浏览项目目录结构、查看目录下有哪些文件、确认文件是否存在。\n"
        + "不适用场景：递归列出目录树（请用 DirectoryTree）、搜索文件（请用 SearchFiles）。\n"
        + "关键参数：path — 要列出的目录路径。")]
    public string[] ListFiles(string path)
    {
        var fp = PathUtils.SafeResolvePath(_ws, path);
        if (fp == null) return ["Error: path escape"];
        if (!Directory.Exists(fp)) return [];
        var entries = Directory.GetFileSystemEntries(fp).Select(Path.GetFileName).OfType<string>().ToArray();
        const int maxEntries = 5000;
        if (entries.Length > maxEntries)
            return entries.Take(maxEntries).Append($"... ({entries.Length - maxEntries} more entries in directory)").ToArray();
        return entries;
    }

    [Description("列出当前可用的所有工具及其用途说明。")]
    public string ListTools() => "Use the specific tool you need. Available domains: filesystem (read/write/list/copy/move/delete/search), text (edit/regex/diff), documents (word/excel/ppt/pdf), web, data, git, system, code analysis, multimedia, and more.";

    // ═══════════════════════════════════════════════════════
    //  DeltaStore integration
    // ═══════════════════════════════════════════════════════

    private static Delta.DeltaStore? s_deltaStore;

    public static void SetDeltaStore(Delta.DeltaStore? store) => s_deltaStore = store;

    private static async Task RecordDeltaIfAvailable(string filePath, string content, bool isNew,
        [System.Runtime.CompilerServices.CallerMemberName] string toolName = "")
    {
        if (s_deltaStore == null) return;
        try
        {
            var lines = content.Split('\n');
            await s_deltaStore.CreateDeltaForEditAsync(
                filePath, startLine: 1, endLine: lines.Length,
                diffContent: null, toolName: toolName,
                conversationId: "filesystem", messageId: Guid.NewGuid().ToString("N"),
                agentId: "FileSystemTools", isNewFile: isNew).ConfigureAwait(false);
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════
    //  coreutils-style aliases — well-known names for LLM
    // ═══════════════════════════════════════════════════════

    [Description("coreutils `cat` — 读取文件内容。支持行范围。\n"
        + "适用场景：查看文件全部或部分内容、拼接多个文件内容。\n"
        + "参数：path — 文件路径；startLine/endLine — 可选行范围。")]
    public Task<string> cat(string path,
        [Description("起始行号（从 1 开始）")] int startLine = 1,
        [Description("结束行号")] int? endLine = null)
        => ReadFileContent(path, startLine, endLine);

    [Description("coreutils `head` — 读取文件前 N 行。\n"
        + "适用场景：快速查看文件头部、预览日志开头。\n"
        + "参数：path — 文件路径；n — 行数（默认 10）。")]
    public Task<string> head(string path,
        [Description("行数（默认 10）")] int n = 10)
        => ReadFileContent(path, 1, Math.Max(1, n));

    [Description("coreutils `mkdir` — 创建目录（含父目录）。\n"
        + "适用场景：创建新目录、确保目录存在后写入文件。")]
    public string mkdir(
        [Description("目录路径")] string path,
        [Description("是否递归创建父目录")] bool parents = true)
    {
        var fp = PathUtils.SafeResolvePath(_ws, path);
            if (fp == null) return ToolResult.Error("path escape");
        try
        {
            if (parents) Directory.CreateDirectory(fp);
            else
            {
                var parent = Path.GetDirectoryName(fp);
                if (parent != null && !Directory.Exists(parent))
                    return ToolResult.Error($"Parent directory '{parent}' does not exist. Use parents:true");
                Directory.CreateDirectory(fp);
            }
            return $"Created directory: {fp}";
        }
        catch (Exception ex) { return ToolResult.Error(ex.Message); }
    }

    [Description("coreutils `touch` — 更新文件时间戳或创建空文件。\n"
        + "适用场景：创建空文件、更新文件修改时间触发重新编译。")]
    public string touch(string path)
    {
        var fp = PathUtils.SafeResolvePath(_ws, path);
            if (fp == null) return ToolResult.Error("path escape");
        try
        {
            if (File.Exists(fp))
            {
                File.SetLastWriteTimeUtc(fp, DateTime.UtcNow);
                return $"Updated timestamp: {Path.GetFileName(fp)}";
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
            File.WriteAllBytes(fp, []);
            return $"Created empty file: {Path.GetFileName(fp)}";
        }
        catch (Exception ex) { return ToolResult.Error(ex.Message); }
    }

    [Description("coreutils `find` — 按 glob 模式搜索文件。\n"
        + "适用场景：查找所有 .cs/.ts/.py 文件、定位最近的日志文件。\n"
        + "参数：pattern — glob 模式如 src/**/*.cs。")]
    public string[] find(
        [Description("Glob 模式，如 src/**/*.cs")] string pattern,
        [Description("搜索起始目录")] string path = ".",
        [Description("排序方式：mtime/name")] string sortBy = "mtime",
        [Description("最大结果数")] int limit = 200)
        => Glob(pattern, path, sortBy, limit);

    [Description("coreutils `ls` — 列出目录内容。\n"
        + "适用场景：浏览目录中的文件和子目录。\n"
        + "参数：path — 目录路径。")]
    public string[] ls(string path = ".") => ListFiles(path);

    [Description("coreutils `stat` — 获取文件/目录详细元信息。\n"
        + "适用场景：查看文件大小、修改时间、类型、权限。")]
    public string stat(string path) => GetFileInfo(path);

    // ========== COPY / MOVE / DELETE / INFO ==========

    [Description("coreutils `cp` — 复制文件或目录（同 CopyFile）。\n"
        + "适用场景：复制代码文件、备份配置文件。\n"
        + "参数：source — 源路径；destination — 目标路径。")]
    public string cp(string source, string destination) => CopyFile(source, destination);

    [Description("复制文件或目录。用于在项目内复制代码文件、配置文件、资源文件等。\n"
        + "适用场景：复制代码文件到新位置、备份配置文件、复制目录结构。\n"
        + "不适用场景：移动文件（请用 MoveFile）、下载文件（请用 DownloadFile）。\n"
        + "关键参数：source — 源路径；destination — 目标路径。")]
    public string CopyFile(string source, string destination)
    {
        var src = Resolve(source, out var denied);
        if (src == null) return $"Source outside workspace: '{denied}'. Set confirm=true after user approval.";
        var dst = Resolve(destination, out denied);
        if (dst == null) return $"Destination outside workspace: '{denied}'. Set confirm=true after user approval.";
        if (!File.Exists(src) && !Directory.Exists(src)) return ToolResult.Error("Source not found");
        if (File.Exists(dst) || Directory.Exists(dst)) return ToolResult.Error("Destination already exists");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (Directory.Exists(src)) CopyDirRecursive(src, dst);
            else File.Copy(src, dst);
            return $"Copied {Path.GetFileName(src)} -> {Path.GetFileName(dst)}";
        }
        catch (Exception ex) { return ToolResult.Error(ex.Message); }
    }

    [Description("coreutils `mv` — 移动或重命名文件/目录（同 MoveFile）。")]
    public string mv(string source, string destination) => MoveFile(source, destination);

    [Description("移动或重命名文件/目录。用于整理项目文件结构。\n"
        + "适用场景：重命名代码文件、将文件移到子目录、整理项目目录结构。\n"
        + "不适用场景：复制文件（请用 CopyFile）、跨磁盘移动会导致复制+删除。\n"
        + "关键参数：source — 源路径；destination — 目标路径。")]
    public string MoveFile(string source, string destination)
    {
        var src = Resolve(source, out var denied);
        if (src == null) return $"Source outside workspace: '{denied}'. Set confirm=true after user approval.";
        var dst = Resolve(destination, out denied);
        if (dst == null) return $"Destination outside workspace: '{denied}'. Set confirm=true after user approval.";
        if (!File.Exists(src) && !Directory.Exists(src)) return ToolResult.Error("Source not found");
        if (File.Exists(dst) || Directory.Exists(dst)) return ToolResult.Error("Destination already exists");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (Directory.Exists(src)) Directory.Move(src, dst);
            else File.Move(src, dst);
            return $"Moved {Path.GetFileName(src)} -> {Path.GetFileName(dst)}";
        }
        catch (Exception ex) { return ToolResult.Error(ex.Message); }
    }

    [Description("coreutils `rm` — 删除文件或目录。自动检测文件/目录类型。\n"
        + "适用场景：删除不需要的文件或目录。\n"
        + "参数：path — 路径；recursive — 递归删除目录（默认 false，目录必须为 true）。")]
    public string rm(string path,
        [Description("递归删除目录")] bool recursive = false)
    {
        var fp = Resolve(path, out var denied);
            if (fp == null) return ToolResult.Error($"Path outside workspace: {denied}");
        if (File.Exists(fp)) return DeleteFile(path);
        if (Directory.Exists(fp)) return DeleteDirectory(path, recursive);
        return ToolResult.Error("Path not found");
    }

    [Description("coreutils `rmdir` — 删除空目录（同 DeleteDirectory recursive:false）。")]
    public string rmdir(string path) => DeleteDirectory(path, false);

    [Description("删除文件。用于清理项目中不再需要的文件。\n"
        + "适用场景：删除临时文件、清理旧的日志文件、删除废弃的代码文件。\n"
        + "关键参数：path — 要删除的文件路径。")]
    public string DeleteFile(string path)
    {
        var fp = Resolve(path, out var denied);
            if (fp == null) return ToolResult.Error($"Path outside workspace: {denied}");
        if (!File.Exists(fp)) return ToolResult.Error("File not found");
        try { File.Delete(fp); return $"Deleted {Path.GetFileName(fp)}"; }
        catch (Exception ex) { return ToolResult.Error(ex.Message); }
    }

    [Description("递归删除目录及其所有内容。用于清理整个目录树。\n"
        + "适用场景：删除 node_modules 目录、清理构建输出目录(obj/bin)、删除整个项目文件夹。\n"
        + "不适用场景：删除单个文件（请用 DeleteFile）。\n"
        + "关键参数：path — 目录路径；recursive — 是否递归删除非空目录。")]
    public string DeleteDirectory(string path, bool recursive = true)
    {
        var fp = Resolve(path, out var denied);
            if (fp == null) return ToolResult.Error($"Path outside workspace: {denied}");
        if (!Directory.Exists(fp)) return ToolResult.Error("Directory not found");
        try
        {
            if (!recursive && Directory.GetFileSystemEntries(fp).Length > 0)
                return ToolResult.Error("Directory not empty. Use recursive:true");
            Directory.Delete(fp, recursive);
            return $"Deleted directory {Path.GetFileName(fp)}";
        }
        catch (Exception ex) { return ToolResult.Error(ex.Message); }
    }

    [Description("获取文件或目录元信息：大小、修改时间、类型、扩展名。用于检查文件详情。\n"
        + "适用场景：查看文件大小、确认文件修改时间、检查文件类型和扩展名、统计目录条目数。\n"
        + "不适用场景：读取文件内容（请用 ReadFileContent）、列出目录文件列表（请用 ListFiles）。\n"
        + "关键参数：path — 要查询的文件或目录路径。")]
    public string GetFileInfo(string path)
    {
        var fp = Resolve(path, out var denied);
            if (fp == null) return ToolResult.Error($"Path outside workspace: {denied}");
        if (File.Exists(fp))
        {
            var fi = new FileInfo(fp);
            return $"**{fi.Name}** -- file\n- Size: {FormatSize(fi.Length)}\n- Modified: {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC\n- Created: {fi.CreationTimeUtc:yyyy-MM-dd HH:mm:ss} UTC\n- Extension: {fi.Extension}";
        }
        if (Directory.Exists(fp))
        {
            var dirName = Path.GetFileName(fp.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return $"**{dirName}** -- directory\n- Items: {Directory.GetFileSystemEntries(fp).Length}\n- Modified: {Directory.GetLastWriteTimeUtc(fp):yyyy-MM-dd HH:mm:ss} UTC\n- Created: {Directory.GetCreationTimeUtc(fp):yyyy-MM-dd HH:mm:ss} UTC";
        }
        return ToolResult.Error("Path not found");
    }

    // ========== GLOB / SEARCH FILES / DIRECTORY TREE ==========

    [Description("按 glob 模式搜索文件，按修改时间排序（最新在前）。\n"
        + "适用场景：找所有 .cs 文件、搜索所有测试文件、找最近修改的配置文件、按模式批量匹配文件。\n"
        + "不适用场景：搜索文件内容（请用 SearchContent）、按文件名子串搜索（请用 SearchFiles）、浏览目录树（请用 DirectoryTree）。\n"
        + "关键参数：pattern — glob 模式如 src/**/*.cs；sortBy — 排序方式 mtime/name；limit — 最大结果数。")]
    public string[] Glob(string pattern, string path = ".", string sortBy = "mtime", int limit = 200, bool includeDeps = false)
    {
        var root = SafePath(path);
        if (root == null || !Directory.Exists(root)) return [ToolResult.Error("invalid path")];
        limit = Math.Clamp(limit, 1, 1000);

        var dirPart = Path.GetDirectoryName(pattern)?.Replace('\\', '/') ?? ".";
        var filePattern = Path.GetFileName(pattern);
        var searchOption = dirPart.Contains("**") || pattern.StartsWith("**") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var regex = GlobToRegex(filePattern);
        var results = new List<(string path, DateTime mtime)>();

        var searchRoot = dirPart.Contains("**") ? root :
            Directory.Exists(Path.Combine(root, dirPart)) ? Path.GetFullPath(Path.Combine(root, dirPart)) : root;

        try
        {
            foreach (var file in Directory.EnumerateFiles(searchRoot, "*", searchOption))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (!includeDeps)
                {
                    var parts = rel.Split('/');
                    if (parts.Take(parts.Length - 1).Any(p => DirectoryWalker.DefaultSkipDirs.Contains(p))) continue;
                }
                if (regex.IsMatch(Path.GetFileName(file)))
                {
                    try { results.Add((rel, File.GetLastWriteTimeUtc(file))); }
                    catch { results.Add((rel, DateTime.MinValue)); }
                }
            }
        }
        catch (DirectoryNotFoundException) { return [ToolResult.Error($"Directory not found: {searchRoot}")]; }

        if (sortBy == "name") results.Sort((a, b) => string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase));
        else results.Sort((a, b) => b.mtime.CompareTo(a.mtime));
        return results.Take(limit).Select(r => r.path).ToArray();
    }

    [Description("递归列出目录结构树。自动折叠大型子目录，默认跳过 .git/node_modules 等依赖目录。\n"
        + "适用场景：浏览项目整体目录结构、了解代码组织方式、查看大型项目的目录层次。\n"
        + "不适用场景：列出单层目录内容（请用 ListFiles）、搜索特定文件（请用 SearchFiles 或 Glob）。\n"
        + "关键参数：path — 根路径；maxDepth — 递归深度(1-5)；includeDeps — 是否包含依赖目录。")]
    public async Task<string> DirectoryTree(string path = ".", int maxDepth = 2, bool includeDeps = false)
    {
        var (root, denied) = PathUtils.TryResolveWithPermission(_ws, path);
        if (root == null) return $"Path outside workspace: '{denied}'. Set confirm=true after user approval.";
            if (!Directory.Exists(root)) return ToolResult.Error("Directory not found");
        maxDepth = Math.Clamp(maxDepth, 1, 5);
        var sb = new StringBuilder();
        await BuildTreeAsync(root, "", 0, maxDepth, includeDeps, sb).ConfigureAwait(false);
        return sb.ToString();
    }

    // ========== PRIVATE HELPERS ==========

    private string? Resolve(string path, out string? denied)
    {
        var (fp, d) = PathUtils.TryResolveWithPermission(_ws, path);
        denied = d;
        return fp;
    }
    private string? SafePath(string path) => PathUtils.SafeResolvePath(_ws, path);

    private static void CopyDirRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDirRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1048576.0:F1} MB"
    };

    private static Regex GlobToRegex(string glob) => GlobUtils.ToRegex(glob);

    private async Task BuildTreeAsync(string dir, string prefix, int depth, int maxDepth, bool includeDeps, StringBuilder sb)
    {
        if (depth > maxDepth) return;
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dir); }
        catch (UnauthorizedAccessException) { sb.AppendLine($"{prefix}[access denied]"); return; }

        var visible = includeDeps ? entries.ToList() : entries.Where(e => !DirectoryWalker.DefaultSkipDirs.Contains(Path.GetFileName(e))).ToList();
        if (visible.Count > MaxChildren)
        {
            sb.AppendLine($"{prefix}[{visible.Count} entries]");
            var subdirs = visible.Where(Directory.Exists).ToList();
            foreach (var entry in subdirs.Take(10))
                sb.AppendLine($"{prefix}  {Path.GetFileName(entry)}/");
            if (subdirs.Count > 10) sb.AppendLine($"{prefix}  ... and {subdirs.Count - 10} more subdirectories");
            return;
        }

        for (int i = 0; i < visible.Count; i++)
        {
            var entry = visible[i];
            var name = Path.GetFileName(entry);
            var isLast = i == visible.Count - 1;
            var connector = isLast ? "\u2514\u2500\u2500 " : "\u251C\u2500\u2500 ";
            var childPrefix = isLast ? "    " : "\u2502   ";
            if (Directory.Exists(entry))
            {
                sb.AppendLine($"{prefix}{connector}{name}/");
                await BuildTreeAsync(entry, prefix + childPrefix, depth + 1, maxDepth, includeDeps, sb).ConfigureAwait(false);
            }
            else sb.AppendLine($"{prefix}{connector}{name}");
        }
    }
}
