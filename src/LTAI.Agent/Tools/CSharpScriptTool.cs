using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
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
    private static readonly string[] DefaultImports =
    [
        "System", "System.IO", "System.Linq", "System.Text",
        "System.Text.Json", "System.Collections.Generic",
        "System.Threading", "System.Threading.Tasks",
        "System.Net.Http", "System.Diagnostics",
        "System.Text.RegularExpressions",
    ];

    private static readonly ScriptOptions DefaultOptions = ScriptOptions.Default
        .WithImports(DefaultImports)
        .WithReferences(typeof(object).Assembly,
                        typeof(Uri).Assembly,
                        typeof(HttpClient).Assembly,
                        typeof(Enumerable).Assembly,
                        typeof(JsonSerializer).Assembly,
                        typeof(DiagnosticListener).Assembly);

    private int _execCount;
    private readonly ToolTrustService? _trust;

    public CSharpScriptTool(ToolTrustService? trust = null) => _trust = trust;

    [Description("在进程中直接执行 C# 代码并返回结果。支持全部 .NET API。代码中可使用 return 返回值。注意：此工具在进程内执行代码，有安全风险，请确认后再使用。")]
    public async Task<string> RunCSharp(
        [Description("要执行的 C# 代码")] string code,
        [Description("确认执行。此工具在进程内执行任意 C# 代码，有安全风险。")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm && (_trust == null || _trust.RequiresConfirm("CSharpScriptTool.RunCSharp")))
            return "⛔ C# 脚本执行已取消：此工具在进程内执行任意代码，需设置 confirm=true 确认风险后执行。";

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
                output = Truncate(output, 16000),
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

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"\n... (truncated, {text.Length - max} more chars)";
}
