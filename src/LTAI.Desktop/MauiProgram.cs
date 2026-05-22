using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Tools;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.DNA;
using LTAI.Agent;
using LTAI.Planning.Metrics;
using LTAI.Knowledge.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace LTAI.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var services = builder.Services;

        var ltaiOptions = new LTAIOptions();

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        config.GetSection(LTAIOptions.SectionName).Bind(ltaiOptions);

        if (ltaiOptions.AI.Providers.Count == 0)
        {
            ltaiOptions.AI.Providers["deepseek"] = new ProviderConfig { Endpoint = "https://api.deepseek.com", Model = "deepseek-chat" };
        }

                services.AddSingleton(Options.Create(ltaiOptions));

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        services.AddLTAICore();
        services.AddLTAIVectorAuto(apiModel: ltaiOptions.AI.L0.Model);
        services.AddLTAIAI();
        services.AddLTAIDNA();
        services.AddLTAICapability();
        services.AddLTAIMetrics();
        services.AddLTAIMAF();

        services.AddSingleton<LTAIService>();

        var sp = services.BuildServiceProvider();
        var lts = sp.GetRequiredService<LivingTreeSystem>();
        lts.InitializeAsync().GetAwaiter().GetResult();

        return builder.Build();
    }
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
