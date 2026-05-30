using System.ComponentModel;
using LTAI.Core;

namespace LTAI.Agent.Tools;

/// <summary>
/// File system tools for agents: reading, writing, and listing files
/// with path-escape security validation.
/// </summary>
public sealed class FileSystemTools
{
    private readonly string _ws;
    public FileSystemTools(string ws) => _ws = ws;

    [Description("Read a file. Supports workspace paths and pre-authorized external paths.")]
    public async Task<string> ReadFileContent([Description("Path")] string path)
    {
        try
        {
            var (fp, denied) = PathUtils.TryResolveWithPermission(_ws, path, confirm: true);
            if (denied != null)
                return $"⚠️ 路径 '{denied}' 在工作区之外且未授权。请在用户确认后重试。";
            if (fp == null) return "Error: path escape";
            var sizeError = PathUtils.CheckFileSize(fp);
            if (sizeError != null) return sizeError;
            var content = await File.ReadAllTextAsync(fp);
            if (content.Length > 10000)
                return $"[file: {fp}, {content.Length} chars (showing first 10000)]\n{content[..10000]}";
            return $"[file: {fp}, {content.Length} chars]\n{content}";
        }
        catch (Exception ex)
        {
            return $"Error reading '{path}': {ex.GetType().Name}: {ex.Message}";
        }
    }

    [Description("Write a file")]
    public async Task<string> WriteFile([Description("Path")] string path, [Description("Content")] string content)
    {
        var fp = PathUtils.SafeResolvePath(_ws, path);
        if (fp == null) return "Error: path escape";
        Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
        await File.WriteAllTextAsync(fp, content);
        return $"Written {content.Length} bytes";
    }

    [Description("List directory")]
    public string[] ListFiles([Description("Path")] string path)
    {
        var fp = PathUtils.SafeResolvePath(_ws, path);
        if (fp == null) return ["Error: path escape"];
        return Directory.Exists(fp) ? Directory.GetFileSystemEntries(fp).Select(Path.GetFileName).OfType<string>().ToArray() : [];
    }

    [Description("列出当前可用的所有工具及其用途说明。")]
    public string ListTools()
    {
        return @"## 可用工具列表

### 📁 文件操作（推荐）
ReadFileContent — 【推荐】读取文件内容的首选工具
WriteFile — 写入/创建文件
ListFiles — 列出目录内容
EditFile — SEARCH/REPLACE 编辑文件
CopyFile / MoveFile — 复制/移动文件或目录
DeleteFile / DeleteDirectory — 删除文件或目录
GetFileInfo — 获取文件/目录元数据

### 🔍 搜索
SearchContent — 按内容搜索文件（grep）
SearchFiles — 按文件名搜索
Glob — 按 glob 模式搜索
DirectoryTree — 递归列出目录树

### 🌐 网络
WebSearch — 搜索网页（无需 API key）
WebFetch — 获取网页内容（仅 http/https）

### 🖥️ Shell
RunCommand — 执行 shell 命令（需用户确认）

### 🧰 其他
GetSymbols / FindInCode — 代码符号分析
ImageInfo / MediaInfo — 图片/媒体信息
ExcelRead / ExcelWrite — Excel 读写
WordRead / WordWrite — Word 读写
WebSearch — 网络搜索
Subagent — 启动子 agent 执行独立任务";
    }
}
