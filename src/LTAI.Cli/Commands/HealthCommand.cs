using Spectre.Console;
using LTAI.Agent.Vector;
using LTAI.Core.Configuration;

namespace LTAI.Cli;

partial class Program
{
    internal static async Task<int> HandleHealth()
    {
        AnsiConsole.MarkupLine("[bold]🔍 LTAI 系统健康检查[/]\n");
        var allPass = true;

        // KgStore
        try
        {
            var dbPath = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "kg.db");
            if (File.Exists(dbPath))
            {
                using var store = new KgStore(dbPath);
                var nodeCount = await store.NodeCount().ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]✅ KgStore[/] — 节点: [bold]{nodeCount}[/] — {new FileInfo(dbPath).Length / 1024}KB");
            }
            else
                AnsiConsole.MarkupLine("[yellow]⚠️  KgStore[/] — 数据库尚未创建");
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]❌ KgStore[/] — {ex.Message.EscapeMarkup()}"); allPass = false; }

        // LLM providers
        try
        {
            var keys = new[] { ("DeepSeek", "DEEPSEEK_API_KEY"), ("OpenAI", "OPENAI_API_KEY"),
                ("SiliconFlow", "SILICONFLOW_API_KEY"), ("Brave", "BRAVE_API_KEY") };
            foreach (var (name, env) in keys)
            {
                var hasKey = !string.IsNullOrEmpty(SecretManager.Get(env));
                AnsiConsole.MarkupLine(hasKey ? $"[green]  ✅ {name}[/]" : $"[grey]  —   {name}[/] — 未设置");
            }
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]  ❌ LLM 检查: {ex.Message.EscapeMarkup()}[/]"); allPass = false; }

        // Disk
        try
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);
            if (drive != null)
            {
                var freeGb = drive.AvailableFreeSpace / 1.0 / (1024 * 1024 * 1024);
                AnsiConsole.MarkupLine($"{(freeGb > 1 ? "[green]" : "[yellow]")}  💾 磁盘[/] — {drive.Name} 剩余 {freeGb:F1}GB");
            }
        }
        catch { }

        // Runtime
        CliHelpers.UsageTrackerBar();

        // Network
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync("https://api.deepseek.com/v1/models").ConfigureAwait(false);
            AnsiConsole.MarkupLine(resp.IsSuccessStatusCode
                ? $"[green]  ✅ 网络[/] — DeepSeek API OK ({(int)resp.StatusCode})"
                : $"[yellow]  ⚠️  网络[/] — DeepSeek 返回 {(int)resp.StatusCode}");
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[yellow]  ⚠️  网络[/] — {ex.Message.EscapeMarkup()}"); }

        AnsiConsole.MarkupLine(allPass ? "\n[bold green]✅ All checks passed[/]" : "\n[bold yellow]⚠️  Some checks failed[/]");
        return allPass ? 0 : 1;
    }
}
