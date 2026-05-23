using Avalonia;
using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Setup;
using LTAI.DNA;
using LTAI.Planning.Metrics;
using LTAI.Knowledge.Vector;
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
        // First-run: show setup wizard for L0/L1/L2 model download
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath) || new FileInfo(configPath).Length < 50)
        {
            Task.Run(async () =>
            {
                Console.WriteLine("First run detected — starting setup wizard...");
                await new InteractiveSetupWizard(configPath).RunAsync();
            }).GetAwaiter().GetResult();
        }

        var services = BuildServices();
        var provider = services.BuildServiceProvider();
        ServiceLocator.SetProvider(provider);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        var ltaiOptions = new LTAIOptions();

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        config.GetSection(LTAIOptions.SectionName).Bind(ltaiOptions);
        if (ltaiOptions.AI.Providers.Count == 0)
        {
            ltaiOptions.AI.Providers["deepseek"] = new ProviderConfig
                { Endpoint = "https://api.deepseek.com", Model = "deepseek-chat" };
        }

        services.AddSingleton(Options.Create(ltaiOptions));
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));
        services.AddLTAICore();
        services.AddLTAIVectorAuto(apiModel: ltaiOptions.AI.L0.Model);
        services.AddLTAIAI();
        services.AddLTAIDNA();
        services.AddLTAIMetrics();
        services.AddSingleton<LTAIService>();

        return services;
    }
}

public sealed class ServiceLocator
{
    private static IServiceProvider? _provider;
    public static void SetProvider(IServiceProvider p) => _provider = p;
    public static T Get<T>() where T : notnull => _provider!.GetRequiredService<T>();
}

public sealed class LTAIService
{
    public LivingTreeSystem LTS { get; }
    public DNAOrchestrator? DNA { get; }
    public LTAIMetricsCollector? Metrics { get; }

    public LTAIService(LivingTreeSystem lts, DNAOrchestrator? dna = null, LTAIMetricsCollector? metrics = null)
    {
        LTS = lts; DNA = dna; Metrics = metrics;
    }
}
