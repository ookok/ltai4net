using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace LTAI.Agent.Tools;

/// <summary>
/// C# 脚本执行引擎 — 利用 Roslyn Scripting 在进程中直接运行 C# 代码。
/// 仅限非 AOT 环境；NativeAOT 下 Roslyn 运行时编译不可用。
/// </summary>
[Description("C# 脚本执行引擎 — 利用 Roslyn Scripting 在进程中直接运行 C# 代码")]
[RequiresDynamicCode("CSharpScriptTool 使用 Roslyn Scripting 运行时编译，NativeAOT 下不可用")]
public sealed class CSharpScriptTool
{
    // Restricted safe imports — no Process/IO/Network/Reflection
    private static readonly string[] DefaultImports =
    [
        "System", "System.Linq", "System.Text",
        "System.Text.Json", "System.Collections.Generic",
        "System.Threading", "System.Threading.Tasks",
        "System.Text.RegularExpressions",
        "System.Math",
    ];

    // Blocked patterns — code containing any of these is rejected before execution
    private static readonly string[] BlockedPatterns =
    [
        "Process.Start", "ProcessStartInfo",
        "File.Write", "File.Delete", "File.Move", "File.Copy", "File.Append",
        "Directory.Delete", "Directory.Move", "Directory.Create",
        "DllImport", "SuppressUnmanagedCodeSecurity",
        "DangerousGetInternal", "Unsafe.As",
        "Assembly.Load", "Assembly.LoadFrom", "Assembly.LoadFile",
        "ProtectedData", "CryptographicException",
        "Reflection.Emit", "DynamicMethod",
        "Runtime.InteropServices", "Runtime.Interop",
        "Microsoft.Win32",
        "Environment.Exit", "Environment.FailFast",
        "Console.WriteLine", "Console.Read",
        "ServiceController", "ManagementObject",
        "Socket", "TcpClient", "HttpListener",
        "System.IO.Compression", "System.IO.Pipes",
        "System.IO.Ports", "System.IO.MemoryMappedFiles",
    ];

    private static readonly ScriptOptions DefaultOptions = ScriptOptions.Default
        .WithImports(DefaultImports)
        .WithReferences(typeof(object).Assembly,
                        typeof(Uri).Assembly,
                        typeof(Enumerable).Assembly,
                        typeof(JsonSerializer).Assembly);

    private int _execCount;

    [Description("在进程中执行受限 C# 代码。仅允许数据处理和算法（无文件 IO / 网络 / 进程 / 反射）。无法执行前由 MAF ToolApprovalAgent 审批。")]
    public async Task<string> RunCSharp(
        [Description("要执行的 C# 代码")] string code,
        CancellationToken ct = default)
    {
        // ── Pre-flight security scan ──
        if (BlockedPatterns.Any(p => code.Contains(p, StringComparison.Ordinal)))
            return ToolResult.Error("C#Script: 代码包含被禁止的 API（文件/网络/进程/反射操作已禁用）");

        _execCount++;
        var sw = Stopwatch.StartNew();
        var id = _execCount;

        try
        {
            object? result = await CSharpScript.EvaluateAsync<object>(code, DefaultOptions, cancellationToken: ct)
                .WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

            sw.Stop();

            var elapsed = sw.ElapsedMilliseconds;

            var output = result switch
            {
                null => "(no return value)",
                string s => s,
                _ => JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
            };

            return JsonSerializer.Serialize(new
            {
                success = true,
                output = ContentTruncator.Truncate(output, 16000),
                execCount = _execCount,
                elapsedMs = elapsed,
            });
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Error($"C#Script #{id} 执行超时 (60s) 或被用户取消");
        }
        catch (CompilationErrorException e)
        {
            var errors = string.Join("\n", e.Diagnostics.Select(d => d.ToString()));
            return ToolResult.Error($"C#Script #{id} 编译错误:\n{errors}");
        }
        catch (Exception ex)
        {
            return ToolResult.FromException(ex, $"C#Script #{id} 运行异常");
        }
    }
}
