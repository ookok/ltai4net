using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LTAI.AI;
using LTAI.Core;
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
    /// <summary>Fallback PATH for sandboxed process execution. Set from config at startup.</summary>
    public static string SystemPathFallback { get; set; } = @"C:\Windows\system32;C:\Windows";
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
        [Description("超时秒数：编译类建议 300，简单命令 60")] int timeoutSec = 60)
    {

        // ⚠️ 安全：禁止危险命令（token 级白名单 + 模式匹配双重防护）
        var cmdLower = command.ToLowerInvariant();

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var executable = parts.Length > 0 ? parts[0].Trim() : "";
        var executableName = Path.GetFileName(executable.AsSpan()).ToString();

        // 1. 按可执行文件名阻止（token 级，无假阳性）
        var blockedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sudo", "su", "chmod", "chown", "mkfs", "fdisk",
            "dd", "shutdown", "reboot", "init", "halt", "poweroff",
            "passwd", "useradd", "usermod", "groupadd", "fuser", "kill",
            "mount", "umount", "iptables", "ufw", "systemctl",
            "cmd", "cmd.exe", "certutil", "bitsadmin", "mshta", "cscript", "wmic",
            "reg", "schtasks", "diskpart", "bcdedit", "regsvr32", "rundll32",
            "attrib", "cacls", "takeown", "icacls",
        };
        if (blockedExes.Contains(executableName))
            return "❌ 命令包含危险操作，已阻止";

        // 2. 按命令全文模式匹配（捕获复合危险命令）
        var dangerousPatterns = new[]
        {
            "rm -rf /", "rm -rf ~", "rm -rf --no-preserve-root",
            ":(){ :|:& };:", "eval ", "exec ",
            "> /dev/", "dd if=", "wget -O - | sh", "curl .* | sh",
            "wget .* -O ", "certutil .* -urlcache", "bitsadmin .* /transfer",
        };
        if (dangerousPatterns.Any(p => cmdLower.Contains(p)))
            return "❌ 命令包含危险操作，已阻止";

        // 2b. 按可执行文件名检测代码执行（覆盖 bash/python/perl/pwsh 等所有变体）
        var codeExecNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bash", "sh", "zsh", "dash", "ksh", "fish",
            "python", "python2", "python3", "py",
            "perl", "perl5",
            "ruby", "rake",
            "php",
            "lua", "luajit",
            "tclsh", "wish",
            "powershell", "pwsh", "powershell.exe", "pwsh.exe",
        };
        var codeExecArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "-c", "-command", "-e", "-i",
        };
        if (parts.Length >= 2 && codeExecNames.Contains(parts[0]) && codeExecArgs.Contains(parts[1]))
            return "❌ 命令包含代码执行操作 (-c/-e/-command)，已阻止";

        // Block command separator chars that bypass first-token checks
        foreach (var sep in new[] { " & ", " && ", " || ", " | ", "; " })
        {
            if (cmdLower.Contains(sep))
            {
                var partsChk = command.Split(new[] { "&&", "||", "|", "&", ";" }, StringSplitOptions.TrimEntries);
                foreach (var part in partsChk)
                {
                    var partExec = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (partExec.Length == 0) continue;
                    var pn = Path.GetFileName(partExec[0].AsSpan()).ToString();
                    if (blockedExes.Contains(pn) || codeExecNames.Contains(pn))
                        return $"❌ 命令包含危险操作（{pn}），已阻止";
                }
                break;
            }
        }

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
            Arguments = isWindows ? $"/c \"{command}\"" : $"-c \"{command}\"",
            WorkingDirectory = fullCwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Restrict PATH to prevent LOLBin abuse and env-based injection
        psi.EnvironmentVariables["PATH"] = isWindows
            ? SystemPathFallback
            : "/usr/bin:/bin";
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
            process.Start();

            // 并读 stdout + stderr（直接 async，不 Task.Run）
            var buf = new char[4096];
            var stdoutTask = ReadStreamAsync(process.StandardOutput, output, buf);
            var stderrTask = ReadStreamAsync(process.StandardError, error, buf);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            try { await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                return $"⏱️ 命令超时 ({timeoutSec}s)，已终止。\n"
                     + $"部分输出:\n{Truncate(output.ToString(), 2000)}";
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

            return Truncate(sb.Length > 0 ? sb.ToString().TrimEnd() : "✅ 命令执行成功（无输出）", 6000);
        }
        catch (Exception ex)
        {
            return $"❌ 执行失败: {ex.Message}";
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static async Task ReadStreamAsync(StreamReader reader, StringBuilder sb, char[] buffer)
    {
        int len;
        while ((len = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            sb.Append(buffer, 0, len);
    }
}
