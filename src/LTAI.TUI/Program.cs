using System.Diagnostics;
using LTAI.Agent;
using LTAI.AI;
using LTAI.Core;
using LTAI.Agent.Workflows;
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
        // ── 终端选择：仅 Windows 下 wt.exe → 当前终端 ──
        if (OperatingSystem.IsWindows() && (args.Length == 0 || args[0] != "--in-wt"))
        {
            var wt = EnsureWindowsTerminal();
            if (wt != null)
            {
                RelaunchInWindowsTerminal(wt);
                return;
            }
            PrintWindowsTerminalReminder();
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "LTAI";

        // Detect OS language for i18n (B1+B2)
        var detectedLang = LTAI.Core.I18n.Locale.CurrentLang;
        System.Diagnostics.Debug.WriteLine($"Locale detected: {detectedLang}");

        // Show splash immediately — no waiting for DI
        AnsiConsole.Write(new FigletText("LTAI").Color(Color.Green));
        var subtitle = LTAI.Core.I18n.Locale.IsChinese ? "LivingTree AI — 轻量版" : "LivingTree AI — Lightweight Edition";
        var loading = LTAI.Core.I18n.Locale.Get("Loading");
        AnsiConsole.MarkupLine($"[grey]{subtitle}[/]");
        AnsiConsole.MarkupLine($"[yellow]{loading}[/]");

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
        services.AddSingleton<LTAI.TUI.DevUI.DevUISpanCollector>();
        services.AddHostedService(sp => sp.GetRequiredService<LTAI.TUI.DevUI.DevUISpanCollector>());

        // Warm up in background while showing splash
        var sp = await Task.Run(() =>
        {
            var sp = services.BuildServiceProvider();
            // Force eager resolve of key singletons in background thread
            _ = sp.GetRequiredService<ChatAgent>();
            _ = sp.GetRequiredService<MultiProviderChatClient>();
            return sp;
        });

        // Wire up shared state for slash commands
        SlashCommands.Embedder = sp.GetService<LocalEmbedder>();
        SlashCommands.Router = sp.GetService<MultiProviderChatClient>();
        SlashCommands.HttpFactory = sp.GetService<IHttpClientFactory>();
        SlashCommands.SnippetStore = sp.GetService<LTAI.Agent.Snippets.SnippetStore>();
        SlashCommands.WorkflowRegistry = sp.GetService<LTAI.Agent.Workflows.YAMLWorkflowRegistry>();
        SlashCommands.Jobs = sp.GetService<LTAI.Agent.Tools.BackgroundJobService>();
        SlashCommands.Pipes = sp.GetService<LTAI.Agent.Workflows.AgentWorkflows>();
        SlashCommands.ModelsProvider = sp.GetService<LTAI.AI.ModelMetadataProvider>();
        var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
        SlashCommands.ActiveProvider = options.Value.AI.DefaultProvider ?? "DeepSeek";
        SlashCommands.L1Model = options.Value.AI.GetLayerConfig("fast").Model;
        SlashCommands.L2Model = options.Value.AI.GetLayerConfig("deep").Model;

        var chatAgent = sp.GetRequiredService<ChatAgent>();

        // 后台预热：加载 ONNX 模型 + 预热 HTTP 连接
        // 不阻塞 splash 显示
        // 检测 coreutils（Windows 下 grep/wc/sort 等 Unix 命令）
        CoreUtilsDetector.PrintReminder();

        var warmupTask = Task.Run(() => chatAgent.WarmUpAsync());

        var router = sp.GetRequiredService<MultiProviderChatClient>();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var llmConfig = new LLMConfigPanel(options, router, httpFactory);

        // 等待预热完成（最多 6 秒）
        try { await warmupTask.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
        catch { /* 预热超时不影响主流程 */ }

        var app = new TuiApp(
            chatAgent,
            llmConfig,
            options,
            Directory.GetCurrentDirectory(),
            sp.GetRequiredService<LTAI.Agent.DevUI.LTAIDevUIService>(),
            sp.GetRequiredService<LTAI.TUI.DevUI.DevUISpanCollector>(),
            sp.GetRequiredService<LTAI.Agent.Tools.QuestionService>(),
            sp.GetService<LTAI.Agent.Workflows.YAMLWorkflowRegistry>(),
            sp.GetService<LTAI.AI.LocalEmbedder>(),
            sp.GetService<LTAI.AI.ToolEmbeddingCache>(),
            sp.GetService<LTAI.AI.RemoteEmbeddingCache>(),
            sp.GetService<LTAI.AI.EmbeddingClient>(),
            sp.GetService<LTAI.AI.ModelMetadataProvider>());
        try { Console.Clear(); } catch { /* non-interactive terminal */ }
        await app.RunAsync().ConfigureAwait(false);
    }

    /// <summary>Windows Terminal 未安装时打印提醒。</summary>
    private static void PrintWindowsTerminalReminder()
    {
        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║ Windows Terminal 未安装。推荐使用它获得最佳 LTAI 体验。     ║");
        Console.WriteLine("╠════════════════════════════════════════════════════════════╣");
        Console.WriteLine("║ 安装命令:                                                ║");
        Console.WriteLine("║   winget install Microsoft.WindowsTerminal                ║");
        Console.WriteLine("║                                                          ║");
        Console.WriteLine("║ 手动下载:                                                ║");
        Console.WriteLine("║   https://apps.microsoft.com/detail/9N0DX20HK701         ║");
        Console.WriteLine("║                                                          ║");
        Console.WriteLine("║ 安装后重启 LTAI 即可自动使用 Windows Terminal。            ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    /// <summary>查找 wt.exe（Windows Terminal），优先 %LOCALAPPDATA% 下的商店安装路径。</summary>
    private static string? EnsureWindowsTerminal()
    {
        // winget 安装路径
        var storePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(storePath)) return storePath;
        // 通过 PATH 查找
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("where", "wt.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (proc != null)
            {
                var line = proc.StandardOutput.ReadLine();
                proc.WaitForExit(1000);
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(line))
                    return line.Trim();
            }
        }
        catch { }
        return null;
    }

    /// <summary>在 Windows Terminal 新标签页中重启当前进程。</summary>
    private static void RelaunchInWindowsTerminal(string wtPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = wtPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(Directory.GetCurrentDirectory());
            psi.ArgumentList.Add(Environment.ProcessPath!);
            psi.ArgumentList.Add("--in-wt");
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]启动 Windows Terminal 失败:[/] {ex.Message.EscapeMarkup()}");
        }
    }

}
