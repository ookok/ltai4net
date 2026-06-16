using System.Diagnostics;
using System.IO.Compression;
using LTAI.Agent;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using LTAI.Mm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Spectre.Console;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace LTAI.TUI;

public static class Program
{
    private static string s_wtDownloadUrl = "http://mogoo.com.cn/Microsoft.WindowsTerminal_1.24.11321.0_x64.zip";
    private static readonly HttpClient s_httpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var crash = new
                {
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Type = "AppDomain.UnhandledException",
                    Exception = e.ExceptionObject?.ToString(),
                    IsTerminating = e.IsTerminating,
                    OS = Environment.OSVersion.ToString(),
                    ProcessPath = Environment.ProcessPath,
                    WorkingDirectory = Directory.GetCurrentDirectory()
                };
                var json = System.Text.Json.JsonSerializer.Serialize(crash, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.json"), json);
            }
            catch
            {
                // non-critical, best-effort
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                var crash = new
                {
                    Timestamp = DateTime.UtcNow.ToString("O"),
                    Type = "TaskScheduler.UnobservedTaskException",
                    Exception = e.Exception?.ToString(),
                    Observed = e.Observed
                };
                var json = System.Text.Json.JsonSerializer.Serialize(crash, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash-unobserved.json"), json);
            }
            catch
            {
                // non-critical, best-effort
            }
            e.SetObserved();
        };

        var earlyConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        s_wtDownloadUrl = earlyConfig.GetSection("LTAI:Mirrors:WindowsTerminalUrl").Value
            ?? "http://mogoo.com.cn/Microsoft.WindowsTerminal_1.24.11321.0_x64.zip";

        // ── Windows Terminal selection ──
        if (OperatingSystem.IsWindows() && (args.Length == 0 || args[0] != "--in-wt"))
        {
            var wt = EnsureWindowsTerminal();
            if (wt != null) { RelaunchInWindowsTerminal(wt); return; }
            if (!await TryDownloadWindowsTerminalAsync())
                PrintWindowsTerminalReminder();
            else
            {
                wt = EnsureWindowsTerminal();
                if (wt != null)
                {
                    AnsiConsole.MarkupLine("[green]下载完成，启动 WT...[/]");
                    await Task.Delay(500);
                    RelaunchInWindowsTerminal(wt);
                    return;
                }
            }
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "LTAI";

        // ── Show splash then immediately start TG ──
        AnsiConsole.Write(new FigletText("LTAI").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]LivingTree AI — 轻量版[/]");
        Console.WriteLine(); // one blank line before TG starts

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .Enrich.WithProperty("Application", "LTAI")
            .WriteTo.File("logs/ltai-agent-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddLogging(b => { b.ClearProviders(); b.AddSerilog(dispose: true); });

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        services.AddSingleton<IConfigurationRoot>(config);
        services.Configure<LTAIOptions>(config.GetSection(LTAIOptions.SectionName));
        services.AddLTAICore();
        services.AddLTAIAI();
        services.AddLTAIAgent();

        ServiceProvider sp;
        try { sp = services.BuildServiceProvider(); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]初始化失败:[/] {ex.Message.EscapeMarkup()}");
            Console.ReadLine(); Environment.Exit(1); return;
        }

        var chatAgent = sp.GetRequiredService<ChatAgent>();

        // Background warmup
        _ = Task.Run(async () =>
        {
            try { await chatAgent.WarmUpAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Logger.Warning(ex, "WarmUp 失败"); }
        });

        // ── Terminal.Gui FullScreen mode (暂用 FullScreen 验证渲染) ──
        Application.AppModel = AppModel.FullScreen;
        var sessionMgr = new SessionManager(new MmSessionSerializer());
        var cacheStore = sp.GetService<LTAI.Agent.Caching.IMemoryCachingStore>();
        if (cacheStore != null)
            sessionMgr.OnSessionDeleted = id => { _ = cacheStore.InvalidateSessionAsync(id); };

        using var app = Application.Create();
        app.Init();

        // Build model label for display
        var cfg = config.GetSection(LTAIOptions.SectionName).Get<LTAIOptions>();
        var l1 = cfg?.AI?.L1;
        var l1Label = l1 != null && !string.IsNullOrEmpty(l1.Provider)
            ? $"L1: {l1.Provider} / {l1.Model ?? "default"}"
            : "未配置模型 (使用 /model 配置)";
        using var win = new MainWindow(app, chatAgent, sessionMgr, l1Label, sp);
        app.Run(win);
    }

    // ── Windows Terminal helpers ──

    private static async Task<bool> TryDownloadWindowsTerminalAsync()
    {
        try
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Windows Terminal 未找到。是否自动下载? (y/N)[/]");
            AnsiConsole.MarkupLine("[grey]  下载地址: " + s_wtDownloadUrl + "[/]");
            Console.Write("> ");
            var line = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (line != "y" && line != "yes") return false;
            var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools", "wt");
            Directory.CreateDirectory(toolsDir);
            AnsiConsole.MarkupLine($"[green]├─ 目录: {toolsDir.EscapeMarkup()}[/]");
            var zipPath = Path.Combine(Path.GetTempPath(), "wt.zip");
            AnsiConsole.MarkupLine("[cyan]├─ 正在下载 Windows Terminal...[/]");
            s_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LTAI/1.0");
            using var response = await s_httpClient.GetAsync(s_wtDownloadUrl);
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(zipPath)) await response.Content.CopyToAsync(fs);
            AnsiConsole.MarkupLine("[cyan]├─ 解压...[/]");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, toolsDir, overwriteFiles: true));
            File.Delete(zipPath);
            var wtExe = Directory.EnumerateFiles(toolsDir, "wt.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (wtExe != null) { AnsiConsole.MarkupLine($"[green]└─ 已安装 ({wtExe.EscapeMarkup()})[/]"); return true; }
            AnsiConsole.MarkupLine("[red]└─ 未找到 wt.exe[/]"); return false;
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]└─ 失败:[/] {ex.Message.EscapeMarkup()}"); return false; }
    }

    private static void PrintWindowsTerminalReminder()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("╔════════════════════════════════════════════════════════════╗");
        AnsiConsole.WriteLine("║ Windows Terminal 未安装。推荐使用它获得最佳 LTAI 体验。     ║");
        AnsiConsole.WriteLine("╠════════════════════════════════════════════════════════════╣");
        AnsiConsole.WriteLine("║ 安装: winget install Microsoft.WindowsTerminal            ║");
        AnsiConsole.WriteLine("╚════════════════════════════════════════════════════════════╝");
        AnsiConsole.WriteLine();
    }

    private static string? EnsureWindowsTerminal()
    {
        var wtDir = Path.Combine(AppContext.BaseDirectory, "tools", "wt");
        if (Directory.Exists(wtDir))
        {
            var found = Directory.EnumerateFiles(wtDir, "wt.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
        }
        var storePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(storePath)) return storePath;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", "wt.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            if (proc != null) { var line = proc.StandardOutput.ReadLine(); proc.WaitForExit(1000); if (proc.ExitCode == 0 && !string.IsNullOrEmpty(line)) return line.Trim(); }
        }
        catch
        {
            // non-critical, best-effort
        }
        return null;
    }

    private static void RelaunchInWindowsTerminal(string wtPath)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = wtPath, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(Directory.GetCurrentDirectory());
            psi.ArgumentList.Add(Environment.ProcessPath!); psi.ArgumentList.Add("--in-wt");
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }
        catch (Exception ex) { AnsiConsole.MarkupLine($"[red]启动 WT 失败:[/] {ex.Message.EscapeMarkup()}"); }
    }
}
