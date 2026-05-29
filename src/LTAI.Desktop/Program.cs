using Avalonia;
using LTAI.Agent;
using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Knowledge.Core;
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
        services.AddSingleton(sp => new DocumentService(Directory.GetCurrentDirectory()));

        return services;
    }
}

public sealed class LTAIService
{
    public ChatAgent Chat { get; }
    public LTAIOptions Options { get; }

    // Display data — simplified with MS Agent Framework 1.8.0
    public string Mode => Options.AI.DefaultProvider;
    public string DNAStatus => "simplified (MS Agent Framework 1.8.0)";
    public string SafetyPosture => "safe";
    public long TokensUsed => 0;
    public int RequestsThisSession => 0;
    public double AvgLatencyMs => 0;

    public LTAIService(ChatAgent chat, IOptions<LTAIOptions> options)
    {
        Chat = chat;
        Options = options.Value;
    }
}
