using Avalonia;
using LTAI.Agent;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var services = BuildServices();
        var provider = services.BuildServiceProvider();

        App.ChatAgent = provider.GetRequiredService<ChatAgent>();
        App.Options = provider.GetRequiredService<IOptions<LTAIOptions>>();
        App.Router = provider.GetService<MultiProviderChatClient>();
        App.HttpFactory = provider.GetService<IHttpClientFactory>();

        // Async balance fetch (non-blocking)
        var opts = provider.GetRequiredService<IOptions<LTAIOptions>>();
        _ = LTAI.Core.Configuration.UsageTracker.FetchBalanceAsync(
            opts.Value.AI.DefaultProvider,
            LTAI.Core.Configuration.SecretManager.Get("SILICONFLOW_API_KEY")
            ?? LTAI.Core.Configuration.SecretManager.Get("OPENROUTER_API_KEY"));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        services.Configure<LTAIOptions>(config.GetSection(LTAIOptions.SectionName));
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddConsole());
        services.AddLTAICore();
        services.AddLTAIAI();
        services.AddLTAIAgent();


        return services;
    }
}

public sealed class LTAIService
{
    public ChatAgent Chat { get; }
    public LTAIOptions Options { get; }

    public string Mode => Options.AI.DefaultProvider;
    public string DNAStatus => "simplified (MS Agent Framework 1.8.0)";
    public string SafetyPosture => "safe";

    // Real tracking data
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
        Chat = chat;
        Options = options.Value;
    }

    public async Task<string> ChatAsync(string message, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _requests);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await Chat.ChatAsync(message, userId: null, ct: ct);
        sw.Stop();
        Interlocked.Add(ref _totalMs, sw.ElapsedMilliseconds);
        // Estimate tokens: roughly characters / 4
        Interlocked.Add(ref _tokensUsed, (response?.Length ?? 0) / 4);
        return response ?? "";
    }
}
