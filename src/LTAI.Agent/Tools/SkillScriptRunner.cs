#pragma warning disable MAAI001
#pragma warning disable IL2075 // NativeAOT IL — reflection-based property access in skill scripts
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace LTAI.Agent.Tools;

/// <summary>
/// 技能脚本运行器。通过 Process.Start 执行 .py/.sh/.csx 等脚本。
/// </summary>
public static class SkillScriptRunner
{
    /// <summary>Fallback PATH for sandboxed process execution. Set from config at startup.</summary>
    public static string SystemPathFallback { get; set; } = @"C:\Windows\system32;C:\Windows";
    /// <summary>供 AgentSkillsProviderBuilder.UseFileScriptRunner 使用的委托。</summary>
    public static async Task<object?> RunAsync(
        object skill,
        AgentSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        // FullPath 在 AgentFileSkillScript 上（internal 属性），通过反射获取
        var fullPath = GetScriptPath(script);
        if (fullPath == null)
            return "无法获取脚本路径";

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var args = FormatArgs(arguments);

        string exe, scriptArgs;
        switch (ext)
        {
            case ".py":  exe = "python3"; scriptArgs = $"\"{fullPath}\" {args}"; break;
            case ".js":  exe = "node";    scriptArgs = $"\"{fullPath}\" {args}"; break;
            case ".sh":  exe = "bash";    scriptArgs = $"\"{fullPath}\" {args}"; break;
            case ".ps1": exe = "powershell"; scriptArgs = $"-File \"{fullPath}\" {args}"; break;
            case ".csx": exe = "dotnet";  scriptArgs = $"script \"{fullPath}\" {args}"; break;
            case ".cs":  exe = "dotnet";  scriptArgs = $"run --project \"{fullPath}\" -- {args}"; break;
            case ".mbt": exe = "moon";    scriptArgs = $"run \"{fullPath}\" {args}"; break;
            case ".mojo": exe = "mojo";   scriptArgs = $"run \"{fullPath}\" {args}"; break;
            case ".cj":  exe = "cjc";     scriptArgs = $"run \"{fullPath}\" {args}"; break;
            default: return $"不支持: {ext}。支持的: .py .js .sh .ps1 .csx .cs .mbt .mojo .cj";
        }

        return await RunProcess(exe, scriptArgs, fullPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> RunProcess(string exe, string args, string fullPath, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? ".",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Restrict PATH to prevent environment-based injection
        psi.EnvironmentVariables["PATH"] = OperatingSystem.IsWindows()
            ? SystemPathFallback
            : "/usr/bin:/bin:/usr/local/bin";
        psi.EnvironmentVariables.Remove("LD_PRELOAD");
        psi.EnvironmentVariables.Remove("LD_LIBRARY_PATH");
        psi.EnvironmentVariables.Remove("DYLD_INSERT_LIBRARIES");
        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
            var outTask = process.StandardOutput.ReadToEndAsync();
            var errTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(60_000);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested == false)
            {
                process.Kill(entireProcessTree: true);
                return $"⏱️ 超时 (60s)";
            }

            var output = await outTask.ConfigureAwait(false);
            var error = await errTask.ConfigureAwait(false);

            if (!string.IsNullOrEmpty(output)) sb.Append(output.TrimEnd());
            if (!string.IsNullOrEmpty(error)) sb.AppendLine($"\n[stderr]\n{error.TrimEnd()}");

            return process.ExitCode == 0
                ? Truncate(sb.ToString(), 4000)
                : $"❌ 退出码 {process.ExitCode}\n{Truncate(sb.ToString(), 4000)}";
        }
        catch (Exception ex) { return $"❌ 失败: {ex.Message}"; }
    }

    private static string? GetScriptPath(AgentSkillScript script)
    {
        try
        {
            var prop = script.GetType().GetProperty("FullPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return prop?.GetValue(script) as string;
        }
        catch { return null; }
    }

    private static string FormatArgs(JsonElement? args)
    {
        if (args == null) return "";
        var items = new List<string>();
        foreach (var item in args.Value.EnumerateArray())
            items.Add($"\"{item.GetString() ?? item.GetRawText()}\"");
        return string.Join(" ", items);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"\n...(截断 {text.Length} 字符)";
}
#pragma warning restore MAAI001
