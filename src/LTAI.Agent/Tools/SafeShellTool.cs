using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// 增强型 Shell 执行工具。包装 LocalShellExecutor 添加：
/// - 命令白名单（可选）
/// - 超时区分（编译 300s，其他 60s）
/// - 路径安全（限制在工作目录内）
/// - 大输出分页
/// - 详细错误信息
/// </summary>
[ToolDomain("shell")]
public sealed class SafeShellTool
{
    /// <summary>Commands with POSIX-specific semantics on Windows.
    /// Returns a warning + suggested alternative, but lets the command proceed (may fail).</summary>
    internal static Dictionary<string, string> PlatformUnsupportedWarnings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chmod"] = "Windows 使用 ACL 而非 POSIX 权限位。考虑使用 icacls。",
        ["chown"] = "Windows 使用 ACL 而非 POSIX 权限位。考虑使用 icacls。",
        ["chgrp"] = "Windows 使用 ACL 而非 POSIX 权限位。考虑使用 icacls。",
        ["chroot"] = "Windows 无 chroot 等效机制。",
        ["mkfifo"] = "Windows 不支持命名管道设备节点。",
        ["mknod"] = "Windows 不支持设备节点。",
        ["kill"] = "Windows 无 POSIX 信号机制。使用 taskkill /F /PID <id>。",
        ["nohup"] = "Windows 无 nohup 等效。",
        ["nice"] = "Windows 无进程优先级命令。",
        ["ln"] = "Windows 无符号链接。考虑 mklink。",
        ["id"] = "Windows 使用不同的用户管理 API。尝试 whoami。",
        ["groups"] = "Windows 使用不同的用户管理 API。",
        ["who"] = "Windows 使用不同的用户管理 API。",
        ["logname"] = "Windows 使用不同的用户管理 API。",
        ["sync"] = "Windows 自动同步文件系统缓存。",
        ["shred"] = "Windows 无安全删除命令。",
        ["stty"] = "Windows 无终端设置命令。",
        ["tty"] = "Windows 无终端名称命令。",
    };

    /// <summary>PowerShell alias conflicts: prefix with .exe to force coreutils binary.</summary>
    internal static Dictionary<string, string> PowerShellAliasConflicts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ls"] = "ls.exe", ["cat"] = "cat.exe", ["cp"] = "cp.exe",
        ["mv"] = "mv.exe", ["rm"] = "rm.exe", ["pwd"] = "pwd.exe",
        ["sort"] = "sort.exe", ["tee"] = "tee.exe", ["uptime"] = "uptime.exe",
        ["mkdir"] = "mkdir.exe", ["rmdir"] = "rmdir.exe", ["sleep"] = "sleep.exe",
    };

    /// <summary>Apply user config overlays from appsettings.json.</summary>
    internal static void ApplyConfig(LTAI.Core.Configuration.ShellSecurityConfig config)
    {
        if (config.PlatformUnsupportedWarnings.Count > 0)
        {
            foreach (var (k, v) in config.PlatformUnsupportedWarnings)
                PlatformUnsupportedWarnings[k] = v;
        }
        if (config.PowerShellAliasConflicts.Count > 0)
        {
            foreach (var (k, v) in config.PowerShellAliasConflicts)
                PowerShellAliasConflicts[k] = v;
        }
        if (!string.IsNullOrEmpty(config.SystemPathFallback))
            SystemPathFallback = config.SystemPathFallback;
    }

    /// <summary>
    /// Resolve PowerShell alias conflict: rewrite `ls -la` → `ls.exe -la`.
    /// Only applies on Windows when coreutils IS installed.
    /// Without coreutils, keep the original command so PowerShell falls through
    /// to its built-in aliases (Get-ChildItem, Get-Content, etc.).
    /// </summary>
    internal static string ResolvePowerShellConflict(string command)
    {
        if (!OperatingSystem.IsWindows()) return command;
        // No coreutils installed → don't force .exe, let PowerShell handle it
        if (!CoreUtilsDetector.IsAvailable) return command;
        var trimmed = command.TrimStart();
        var firstSpace = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var cmdName = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
        if (PowerShellAliasConflicts.TryGetValue(cmdName, out var exe))
            return exe + trimmed[cmdName.Length..];
        return command;
    }

    /// <summary>Check if the first command has platform-specific limitations.
    /// Returns a warning if so (the command will still execute, may fail).</summary>
    internal static string? CheckPlatformUnsupported(string command)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var trimmed = command.TrimStart();
        var firstSpace = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var cmdName = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
        if (PlatformUnsupportedWarnings.TryGetValue(cmdName, out var reason))
            return $"[SafeShell] `{cmdName}` — {reason} 将尝试执行（可能失败）。";
        return null;
    }

    public static string SystemPathFallback { get; set; } = @"C:\Windows\system32;C:\Windows";
    private static readonly int _shellConcurrency = LTAI.Core.Configuration.EnvironmentConfig.ShellConcurrency;
    private static readonly SemaphoreSlim _concurrencyGate = new(_shellConcurrency, _shellConcurrency);
    private readonly string _ws;
    private readonly HashSet<string>? _allowList;
    private readonly IHttpClientFactory? _httpFactory;

    /// <param name="ws">工作目录，所有命令在此执行。</param>
    /// <param name="allowList">可选白名单，null=允许所有命令。</param>
    /// <param name="httpFactory">可选，用于检测网络。</param>
    public SafeShellTool(string ws, HashSet<string>? allowList = null,
        IHttpClientFactory? httpFactory = null)
    {
        _ws = ws;
        _allowList = allowList;
        _httpFactory = httpFactory;
    }

    [Description("执行 shell 命令。用于运行编译、构建、测试、文件操作等命令行任务。\n"
        + "适用场景：编译项目(dotnet build/npm run)、运行测试(dotnet test)、执行 git 操作、安装包、运行脚本、文件操作、查看进程。\n"
        + "不适用场景：需要交互的命令(如 vim/nano)、图形界面程序、sudo 提权操作、长时间运行的服务进程。\n"
        + "关键参数：command — 要执行的命令；cwd — 工作目录；timeoutSec — 超时秒数。")]
    [ToolExample("编译这个项目")]
    [ToolExample("运行测试")]
    [ToolExample("执行 git push")]
    [ToolExample("安装 npm 包")]
    [ToolExample("查看当前目录文件列表")]
    public async Task<string> RunCommand(
        [Description("要执行的 shell 命令")] string command,
        [Description("工作目录（相对于项目根，默认 .）")] string cwd = ".",
        [Description("超时秒数：编译类建议 300，简单命令 60")] int timeoutSec = 60,
        CancellationToken ct = default)
    {

        // ⚠️ 安全：ShellSecurity 统一禁止危险命令
        if (ShellSecurity.IsBlocked(command))
            return "❌ 命令包含危险操作，已阻止";

        // 平台不适用命令检查（coreutils 启发）
        var unsupported = CheckPlatformUnsupported(command);
        if (unsupported != null) return unsupported;

        // PowerShell 别名冲突解析（coreutils 启发）
        command = ResolvePowerShellConflict(command);

        // 白名单检查
        if (_allowList != null)
        {
            var cmdName = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";
            if (!_allowList.Contains(cmdName) && !_allowList.Contains("*"))
                return $"⚠️ 命令 '{cmdName}' 不在白名单中。允许的命令: {string.Join(", ", _allowList)}";
        }

        // 路径安全
        var fullCwd = PathUtils.SafeResolvePath(_ws, cwd);
        if (fullCwd == null)
            return "Error: 工作目录逃逸";

        if (!Directory.Exists(fullCwd))
            return $"Error: 目录不存在: {fullCwd}";

        // 设置超时
        timeoutSec = Math.Clamp(timeoutSec, 5, 600);

        var sb = new StringBuilder();
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows ? $"/c \"{ShellSecurity.EscapeCmdArg(command)}\"" : $"-c \"{ShellSecurity.EscapeBashArg(command)}\"",
            WorkingDirectory = fullCwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Inherit full environment, then restrict PATH and remove dangerous vars
        foreach (DictionaryEntry env in Environment.GetEnvironmentVariables())
            psi.EnvironmentVariables[env.Key.ToString()!] = env.Value?.ToString() ?? "";
        var basePath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var safePath = isWindows
            ? $"{basePath};{SystemPathFallback}"
            : basePath;
        psi.EnvironmentVariables["PATH"] = safePath;
        psi.EnvironmentVariables.Remove("LD_PRELOAD");
        psi.EnvironmentVariables.Remove("LD_LIBRARY_PATH");
        psi.EnvironmentVariables.Remove("DYLD_INSERT_LIBRARIES");
        psi.EnvironmentVariables.Remove("COR_ENABLE_PROFILING");
        psi.EnvironmentVariables.Remove("COR_PROFILER");
        using var process = new Process { StartInfo = psi };

        var output = new StringBuilder();
        var error = new StringBuilder();

        try
        {
            await _concurrencyGate.WaitAsync().ConfigureAwait(false);
            try
            {
            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            var ctk = timeoutCts.Token;

            // 并读 stdout + stderr（直接 async，不 Task.Run）
            var outBuf = new char[4096];
            var errBuf = new char[4096];
            var stdoutTask = ReadStreamAsync(process.StandardOutput, output, outBuf, ctk);
            var stderrTask = ReadStreamAsync(process.StandardError, error, errBuf, ctk);
            try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return $"⏱️ 命令超时 ({timeoutSec}s)，已终止。\n"
                     + $"部分输出:\n{ContentTruncator.Truncate(output.ToString(), 2000)}";
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            var exitCode = process.ExitCode;
            var outText = output.ToString();
            var errText = error.ToString();

            // 格式化输出
            if (!string.IsNullOrEmpty(outText))
                sb.AppendLine(outText.TrimEnd());

            if (!string.IsNullOrEmpty(errText))
                sb.AppendLine($"[stderr]\n{errText.TrimEnd()}");

            if (exitCode != 0)
                return $"❌ 退出码 {exitCode}\n{sb}";

            return ContentTruncator.Truncate(sb.Length > 0 ? sb.ToString().TrimEnd() : "✅ 命令执行成功（无输出）", 6000);
            }
            finally { _concurrencyGate.Release(); }
        }
        catch (Exception ex)
        {
            return $"❌ 执行失败: {ex.Message}";
        }
    }

    private static async Task ReadStreamAsync(StreamReader reader, StringBuilder sb, char[] buffer, CancellationToken ct = default)
    {
        int len;
        while ((len = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            sb.Append(buffer, 0, len);
    }
}
