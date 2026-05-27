using Avalonia;
using LTAI.AI.Interfaces;
using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Agent;
using LTAI.Agent.Tools;
using LTAI.Core.Messaging;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.DNA;
using LTAI.Planning.Metrics;
using LTAI.Knowledge.Core;
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
        var services = BuildServices();
        var provider = services.BuildServiceProvider();
        ServiceLocator.SetProvider(provider);

        var toolRegistry = provider.GetRequiredService<AIToolRegistry>();
        Task.Run(() => toolRegistry.RegisterAllToolCategoriesAsync()).GetAwaiter().GetResult();
        Task.Run(() => provider.RegisterMarkdownToolsAsync(toolRegistry)).GetAwaiter().GetResult();

        var lts = provider.GetRequiredService<ILivingTreeSystem>();
        Task.Run(() => lts.InitializeAsync()).GetAwaiter().GetResult();

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
            {
                Endpoint = OptionService.Get("deepseek.endpoint") ?? "https://api.deepseek.com",
                Model = OptionService.Get("deepseek.model") ?? "deepseek-v4-pro"
            };
            ltaiOptions.AI.Providers["deepseek-fast"] = new ProviderConfig
            {
                Endpoint = OptionService.Get("deepseek.fast.endpoint") ?? "https://api.deepseek.com",
                Model = OptionService.Get("deepseek.fast.model") ?? "deepseek-v4-flash"
            };
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
    public ILivingTreeSystem LTS { get; }
    public DNAOrchestrator? DNA { get; }
    public LTAIMetricsCollector? Metrics { get; }
    public LTAI.Core.Governors.CPSProcessingService? CPS { get; }
    public LTAI.Core.Governors.CoordinationScheduler? Scheduler { get; }
    public LTAI.Core.Governors.ParetoRouter? Router { get; }
    public LTAI.Core.Governors.IMicroKernel? Kernel { get; }

    public LTAIService(ILivingTreeSystem lts, DNAOrchestrator? dna = null, LTAIMetricsCollector? metrics = null,
        LTAI.Core.Governors.CPSProcessingService? cps = null,
        LTAI.Core.Governors.CoordinationScheduler? scheduler = null,
        LTAI.Core.Governors.ParetoRouter? router = null,
        LTAI.Core.Governors.IMicroKernel? kernel = null)
    {
        LTS = lts; DNA = dna; Metrics = metrics;
        CPS = cps; Scheduler = scheduler; Router = router; Kernel = kernel;
    }
}
