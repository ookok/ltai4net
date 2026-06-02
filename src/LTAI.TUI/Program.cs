using System.Diagnostics;
using System.Reflection;
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
        // ── Alacritty 自嵌入：首次运行解压，以后复用 ──
        if (args.Length == 0 || args[0] != "--in-alacritty")
        {
            var alacritty = EnsureAlacritty();
            if (alacritty != null)
            {
                RelaunchInAlacritty(alacritty);
                return; // 旧进程退出
            }
        }

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
        var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
        SlashCommands.ActiveProvider = options.Value.AI.DefaultProvider ?? "DeepSeek";
        SlashCommands.L1Model = options.Value.AI.GetLayerConfig("fast").Model;
        SlashCommands.L2Model = options.Value.AI.GetLayerConfig("deep").Model;

        var chatAgent = sp.GetRequiredService<ChatAgent>();

        // 后台预热：加载 ONNX 模型 + 预热 HTTP 连接
        // 不阻塞 splash 显示
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
            sp.GetService<LTAI.Agent.Workflows.YAMLWorkflowRegistry>(),
            sp.GetService<LTAI.AI.LocalEmbedder>(),
            sp.GetService<LTAI.AI.ToolEmbeddingCache>(),
            sp.GetService<LTAI.AI.RemoteEmbeddingCache>(),
            sp.GetService<LTAI.AI.EmbeddingClient>());
        try { Console.Clear(); } catch { /* non-interactive terminal */ }
        await app.RunAsync().ConfigureAwait(false);
    }

    /// <summary>将嵌入的 Alacritty 解压到 %LOCALAPPDATA%/LTAI/ 并返回路径。</summary>
    private static string? EnsureAlacritty()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LTAI", "Alacritty");

        Directory.CreateDirectory(baseDir);

        var exePath = Path.Combine(baseDir, "alacritty.exe");
        var ymlPath = Path.Combine(baseDir, "alacritty.yml");
        var asm = Assembly.GetExecutingAssembly();

        try
        {
            // 只解压一次（二次启动直接复用）
            if (!File.Exists(exePath) || new FileInfo(exePath).Length == 0)
            {
                using var stream = asm.GetManifestResourceStream("LTAI.TUI.Assets.Alacritty.alacritty.exe");
                if (stream == null) return null;
                using var fs = new FileStream(exePath, FileMode.Create, FileAccess.Write);
                stream.CopyTo(fs);
            }

            if (!File.Exists(ymlPath))
            {
                using var stream = asm.GetManifestResourceStream("LTAI.TUI.Assets.Alacritty.alacritty.yml");
                if (stream == null) return null;
                using var fs = new FileStream(ymlPath, FileMode.Create, FileAccess.Write);
                stream.CopyTo(fs);
            }

            return exePath;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]解压 Alacritty 失败:[/] {ex.Message.EscapeMarkup()}");
            return null;
        }
    }

    /// <summary>在 Alacritty 终端中重启动当前进程。</summary>
    private static void RelaunchInAlacritty(string alacrittyExe)
    {
        try
        {
            var ymlPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LTAI", "Alacritty", "alacritty.yml");

            var psi = new ProcessStartInfo
            {
                FileName = alacrittyExe,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--config-file");
            psi.ArgumentList.Add(ymlPath);
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(Environment.ProcessPath!);
            psi.ArgumentList.Add("--in-alacritty");
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                psi.ArgumentList.Add(arg);

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]启动 Alacritty 失败:[/] {ex.Message.EscapeMarkup()}");
        }
    }
}
