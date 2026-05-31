using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace LTAI.Agent.Tools;

[Description("C# 脚本执行引擎 — 利用 Roslyn Scripting 在进程中直接运行 C# 代码")]
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

    [Description("在进程中直接执行 C# 代码并返回结果。支持全部 .NET API。代码中可使用 return 返回值。")]
    public async Task<string> RunCSharp(
        [Description("要执行的 C# 代码")] string code,
        CancellationToken ct = default)
    {
        _execCount++;
        var sw = Stopwatch.StartNew();
        var id = _execCount;

        try
        {
            Debug.WriteLine($"[C#Script #{id}] 执行中...");

            object? result = await CSharpScript.EvaluateAsync<object>(code, DefaultOptions, cancellationToken: ct)
                .WaitAsync(TimeSpan.FromSeconds(60), ct);

            sw.Stop();

            var output = result switch
            {
                null => "(no return value)",
                string s => s,
                _ => JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
            };

            var elapsed = sw.ElapsedMilliseconds;
            Debug.WriteLine($"[C#Script #{id}] 完成 ({elapsed}ms)");

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
