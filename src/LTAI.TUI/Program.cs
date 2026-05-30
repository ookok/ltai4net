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
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "LTAI";

        // Show splash immediately — no waiting for DI
        AnsiConsole.Write(new FigletText("LTAI").Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]LivingTree AI — 轻量版[/]");
        AnsiConsole.MarkupLine("[yellow]正在初始化...[/]");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Application", "LTAI")
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}")
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
}
