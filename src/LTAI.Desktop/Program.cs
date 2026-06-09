using Avalonia;
using LTAI.Agent;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Session;
using LTAI.Desktop.Debugging;
using LTAI.Desktop.Services;
using LTAI.Desktop.ViewModels;
using LTAI.Mm;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    public static async Task InitializeServicesAsync()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "desktop-startup.log");
        void Log(string msg) => File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] {msg}\n");

        Log("InitializeServicesAsync started");
        var services = BuildServiceCollection();

        // ⏱ 全局超时：DI 容器 + WarmUp 共 18 秒内必须完成
        // 防止 ONNX 模型加载、EP 探测、或网络请求卡死初始化
        using var initCts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var ct = initCts.Token;

        Log("Building ServiceProvider...");
        var provider = await Task.Run(() => services.BuildServiceProvider(), ct);
        Log("ServiceProvider built");

        var options = provider.GetRequiredService<IOptions<LTAIOptions>>();
        Log("LTAIOptions resolved, validating...");
        ConfigMmValidator.ThrowIfInvalid(options.Value);
        Log("Config validation passed");

        var chatAgent = provider.GetRequiredService<ChatAgent>();
        Log("ChatAgent resolved, warming up...");
        await chatAgent.WarmUpAsync().WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        Log("WarmUp complete");

        App.ChatAgent = chatAgent;
        App.Options = options;
        App.Ltais = new LTAIService(chatAgent, options, provider);
        App.Router = provider.GetService<MultiProviderChatClient>();
        App.HttpFactory = provider.GetService<IHttpClientFactory>();
        Log("Static properties set");
        _ = Task.Run(async () =>
        {
            try
            {
                await LTAI.Core.Configuration.UsageTracker.FetchBalanceAsync(
                    options.Value.AI.DefaultProvider ?? "",
                    LTAI.Core.Configuration.SecretManager.Get("SILICONFLOW_API_KEY")
                    ?? LTAI.Core.Configuration.SecretManager.Get("OPENROUTER_API_KEY"));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Program] Balance fetch: {ex.Message}"); }
        });
    }

    private static IServiceCollection BuildServiceCollection()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();
        services.AddSingleton<IConfigurationRoot>(config);
        services.Configure<LTAIOptions>(config.GetSection(LTAIOptions.SectionName));
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddConsole());
        services.AddLTAICore();
        services.AddLTAIAI();
        services.AddLTAIAgent();
        services.AddSingleton<DebugBridge>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<DesktopCommandService>();
        services.AddSingleton<ILlmClient, LlmClient>();
        services.AddSingleton<ISessionSerializer>(_ => new MmSessionSerializer());
        services.AddSingleton<SessionManager>(sp =>
            ActivatorUtilities.CreateInstance<SessionManager>(sp,
                sp.GetRequiredService<ISessionSerializer>()));
        services.AddTransient<DevUIViewModel>();
        return services;
    }
}

public sealed class LTAIService
{
    public ChatAgent Chat { get; }
    public LTAIOptions Options { get; }
    public IServiceProvider Services { get; }

    public LTAIService(ChatAgent chat, IOptions<LTAIOptions> options, IServiceProvider services)
    {
        Chat = chat; Options = options.Value; Services = services;
    }

    public string Mode => Options.AI.DefaultProvider ?? "";
    public string DNAStatus => "simplified (MS Agent Framework 1.8.0)";
    public string SafetyPosture => "safe";

    private long _tokensUsed;
    private long _totalMs;
    private int _requests;
    private readonly System.Diagnostics.Stopwatch _sessionTimer = System.Diagnostics.Stopwatch.StartNew();

    public long TokensUsed => Interlocked.Read(ref _tokensUsed);
    public int RequestsThisSession => Interlocked.CompareExchange(ref _requests, 0, 0);
    public double AvgLatencyMs => _requests > 0 ? (double)_totalMs / _requests : 0;
    public TimeSpan Uptime => _sessionTimer.Elapsed;

    public LTAIService(ChatAgent chat, IOptions<LTAIOptions> options)
    {
        Chat = chat; Options = options.Value; Services = null!;
    }

    public async Task<string> ChatAsync(string message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _requests);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await Chat.ChatAsync(message, userId: null, ct: ct);
        sw.Stop();
        Interlocked.Add(ref _totalMs, sw.ElapsedMilliseconds);
        Interlocked.Add(ref _tokensUsed, (response?.Length ?? 0) / 4);
        return response ?? "";
    }
}
