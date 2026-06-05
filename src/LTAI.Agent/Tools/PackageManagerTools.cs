using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LTAI.AI;

namespace LTAI.Agent.Tools;

[ToolDomain("pkg")]
public sealed class PackageManagerTools
{
    private static bool? _apmAvailable;

    private static bool ApmAvailable()
    {
        if (_apmAvailable.HasValue) return _apmAvailable.Value;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("apm", "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return (_apmAvailable = false).Value;
            p.WaitForExit(3000);
            return (_apmAvailable = p.ExitCode == 0).Value;
        }
        catch { return (_apmAvailable = false).Value; }
    }

    [Description("搜索 APM (Agent Package Manager) registry 中的 MCP 服务器或技能包。"
        + "返回包含名称、描述和安装方式的列表。"
        + "适用场景：需要连接外部服务（GitHub、数据库、文件系统等）时，先搜索可用 MCP 服务器。"
        + "关键词示例：filesystem, github, database, web, browser, docker")]
    [ToolExample("搜索文件系统相关的 MCP 服务器")]
    [ToolExample("看看有哪些可用的数据库 MCP 服务器")]
    public async Task<string> PkgSearch(string query)
    {
        if (!ApmAvailable()) return "APM CLI 未安装。请先安装: curl -sSL https://aka.ms/apm-unix | sh （Linux/macOS）或 iwr https://aka.ms/apm-windows | iex （Windows）";

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("apm", $"search \"{query}\" --format json")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            p.Start();
            var output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            if (p.ExitCode != 0) return $"搜索失败: {error}";
            if (string.IsNullOrWhiteSpace(output)) return "未找到匹配的包。";

            // Try parse JSON array
            try
            {
                var results = JsonSerializer.Deserialize<JsonElement>(output);
                var sb = new StringBuilder("搜索结果:\n\n");
                if (results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() : "?";
                        var desc = item.TryGetProperty("description", out var d) ? d.GetString() : "";
                        var pkgType = item.TryGetProperty("type", out var t) ? t.GetString() : "mcp";
                        sb.AppendLine($"  • [{(pkgType == "mcp" ? "MCP" : "技能")}] {name}");
                        if (!string.IsNullOrEmpty(desc))
                            sb.AppendLine($"    {desc}");
                        sb.AppendLine($"    安装: apm install {(pkgType == "mcp" ? "--mcp " : "")}{name}");
                        sb.AppendLine();
                    }
                }
                else
                {
                    sb.AppendLine(output.Length > 2000 ? output[..2000] + "..." : output);
                }
                return sb.ToString();
            }
            catch { return output.Length > 2000 ? output[..2000] + "..." : output; }
        }
        catch (Exception ex) { return $"搜索出错: {ex.Message}"; }
    }

    [Description("安装 APM 包（技能、MCP 服务器、agent 配置等）。"
        + "支持安装 MCP 服务器（--mcp 参数）和技能包。"
        + "适用场景：搜索到合适的包后，安装到当前项目。"
        + "示例: apm install microsoft/apm-sample-package, apm install --mcp io.github.github/github-mcp-server")]
    [ToolExample("安装 GitHub MCP 服务器")]
    [ToolExample("安装文件系统 MCP 服务器")]
    public async Task<string> PkgInstall(string package)
    {
        if (!ApmAvailable()) return "APM CLI 未安装。请先安装后再执行。";

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("apm", $"install \"{package}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            p.Start();
            var output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            if (p.ExitCode != 0) return $"安装失败:\n{error}";
            return $"✅ 安装成功: {package}\n\n{output}";
        }
        catch (Exception ex) { return $"安装出错: {ex.Message}"; }
    }

    [Description("列出当前项目已安装的 APM 包。显示所有技能、MCP 服务器和 agent 配置。"
        + "适用场景：查看项目当前有哪些 agent 依赖，确认安装状态。")]
    [ToolExample("已安装了哪些包")]
    public async Task<string> PkgList()
    {
        if (!ApmAvailable()) return "APM CLI 未安装。";

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("apm", "list --format json")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            p.Start();
            var output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            if (p.ExitCode != 0) return $"list 失败: {error}";
            if (string.IsNullOrWhiteSpace(output)) return "当前没有安装任何 APM 包。";
            return output.Length > 3000 ? output[..3000] + "..." : output;
        }
        catch (Exception ex) { return $"list 出错: {ex.Message}"; }
    }
}
