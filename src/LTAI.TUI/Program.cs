using System.Diagnostics;
using LTAI.Agent;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using Spectre.Console;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace LTAI.TUI;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // 检测 Windows Terminal，不在则自动安装并重启动
        if (!EnsureWindowsTerminal())
            return; // 重启动后旧进程退出

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "LTAI";

        // Show splash immediately — no waiting for DI
        AnsiConsole.Write(new FigletText("LTAI").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]LivingTree AI — 轻量版[/]");
        AnsiConsole.MarkupLine("[yellow]正在初始化...[/]");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Application", "LTAI")
            .WriteTo.File("logs/ltai-agent-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => { b.ClearProviders(); b.AddSerilog(dispose: true); });

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        services.Configure<LTAIOptions>(config.GetSection(LTAIOptions.SectionName));
        services.AddLTAICore();
        services.AddLTAIAI();
        services.AddLTAIAgent();

        // Warm up in background while showing splash
        var sp = await Task.Run(() =>
        {
            var sp = services.BuildServiceProvider();
            // Force eager resolve of key singletons in background thread
            _ = sp.GetRequiredService<ChatAgent>();
            _ = sp.GetRequiredService<MultiProviderChatClient>();
            return sp;
        });

        var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
        var chatAgent = sp.GetRequiredService<ChatAgent>();
        var router = sp.GetRequiredService<MultiProviderChatClient>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var llmConfig = new LLMConfigPanel(options, router, httpFactory);

        var app = new TuiApp(chatAgent, llmConfig, options, Directory.GetCurrentDirectory());
        try { Console.Clear(); } catch { /* non-interactive terminal */ }
        await app.RunAsync();
    }

    /// <summary>检测 Windows Terminal，不在时尝试安装并重启动。</summary>
    private static bool EnsureWindowsTerminal()
    {
        // 环境变量 LTAI_NO_WT=1 跳过 → 用 cmd.exe
        if (Environment.GetEnvironmentVariable("LTAI_NO_WT") == "1")
            return true;

        // 已经在 Windows Terminal 中 → 跳过
        if (Environment.GetEnvironmentVariable("WT_SESSION") != null)
            return true;

        // 查找 wt.exe
        var wtPath = FindWindowsTerminal();
        if (wtPath != null)
            return RelaunchInTerminal(wtPath);

        // wt.exe 不存在 → 尝试安装
        AnsiConsole.MarkupLine("[yellow]Windows Terminal 未找到，正在自动安装...[/]");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "install --exact --id Microsoft.WindowsTerminal --silent --accept-package-agreements",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (proc == null)
            {
                AnsiConsole.MarkupLine("[red]无法启动 winget 安装程序[/]");
                return true; // 继续用 cmd.exe
            }

            // 等待安装完成（最多 60s）
            var exited = proc.WaitForExit(60_000);
            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();

            if (!exited || proc.ExitCode != 0)
            {
                AnsiConsole.MarkupLine($"[red]Windows Terminal 安装失败 (exit={proc.ExitCode})[/]");
                if (!string.IsNullOrEmpty(error))
                    AnsiConsole.MarkupLine($"[grey]{error.EscapeMarkup()}[/]");
                return true; // 继续用 cmd.exe
            }

            AnsiConsole.MarkupLine("[green]Windows Terminal 安装成功！正在启动...[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]安装过程出错:[/] {ex.Message.EscapeMarkup()}");
            return true; // 继续用 cmd.exe
        }

        // 重新查找（刚安装的需要刷新 PATH）
        wtPath = FindWindowsTerminal();
        if (wtPath == null)
        {
            AnsiConsole.MarkupLine("[red]安装后仍未找到 wt.exe，请手动启动 Windows Terminal[/]");
            return true;
        }

        return RelaunchInTerminal(wtPath);
    }

    /// <summary>查找 wt.exe 路径。</summary>
    private static string? FindWindowsTerminal()
    {
        // 常见位置
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps", "wt.exe"),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // 搜索 PATH
        try
        {
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
            foreach (var dir in paths)
            {
                try
                {
                    var test = Path.Combine(dir, "wt.exe");
                    if (File.Exists(test)) return test;
                }
                catch { /* 跳过无效路径 */ }
            }
        }
        catch { /* PATH 访问失败 */ }

        return null;
    }

    /// <summary>在当前 Windows Terminal 窗口中重启动。</summary>
    private static bool RelaunchInTerminal(string wtPath)
    {
        try
        {
            var cmd = Environment.CommandLine;
            // wt -d . <原始命令>
            var psi = new ProcessStartInfo
            {
                FileName = wtPath,
                Arguments = $"-d . -- {cmd}",
                UseShellExecute = true,
            };
            Process.Start(psi);
            return false; // 告诉 Main 退出当前进程
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]启动 Windows Terminal 失败:[/] {ex.Message.EscapeMarkup()}");
            return true; // 继续用 cmd.exe
        }
    }
}
