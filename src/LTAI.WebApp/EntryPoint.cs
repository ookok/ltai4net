using LTAI.AI;
using LTAI.AI.Interfaces;
using LTAI.AI.Governors;
using LTAI.Agent.Tools;
using LTAI.Core.Messaging;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Tools;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Setup;
using LTAI.DNA;
using LTAI.Agent;
using LTAI.Knowledge.Memory;
using LTAI.Planning.Metrics;
using LTAI.Knowledge.Core;
using LTAI.Knowledge.Vector;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace LTAI.WebApp;

public static class EntryPoint
{
    public static async Task RunAsync(string[] args)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var status = FirstRunDetector.Check(configPath);
        if (status.IsFirstRun)
        {
            FirstRunDetector.PrintDiagnostics(status);
            Console.WriteLine("检测到未配置，启动配置向导...");
            await new InteractiveSetupWizard(configPath).RunAsync().ConfigureAwait(false);
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var ltaiSection = builder.Configuration.GetSection(LTAIOptions.SectionName);
        builder.Services.Configure<LTAIOptions>(ltaiSection);
        var ltaiOptions = ltaiSection.Get<LTAIOptions>() ?? new LTAIOptions();

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

        builder.Services.AddLTAICore();
        builder.Services.AddLTAIVectorAuto(apiModel: ltaiOptions.AI.L0.Model);
        builder.Services.AddLTAIAI();
        builder.Services.AddLTAIDNA();
        builder.Services.AddLTAIMemory();
        builder.Services.AddLTAICapability();
        builder.Services.AddLTAIMetrics();
        builder.Services.AddLTAIMAF();
        builder.Services.AddSingleton<ILivingTreeSystem>(sp => sp.GetRequiredService<LivingTreeSystem>());
        builder.Services.AddSingleton<DNAOrchestrator>();
        builder.Services.AddSingleton<LTAIMetricsCollector>();

        var app = builder.Build();
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapDevUIEndpoints();
        app.MapRazorComponents<LTAI.WebApp.Components.App>();

        var toolRegistry = app.Services.GetRequiredService<AIToolRegistry>();
        await toolRegistry.RegisterAllToolCategoriesAsync().ConfigureAwait(false);
        await app.Services.RegisterMarkdownToolsAsync(toolRegistry).ConfigureAwait(false);
        await app.Services.RegisterCodeActToolsAsync(toolRegistry).ConfigureAwait(false);

        var lts = app.Services.GetRequiredService<ILivingTreeSystem>();
        await lts.InitializeAsync().ConfigureAwait(false);

        var skillRegistry = app.Services.GetRequiredService<LTAI.Agent.Skills.SkillRegistry>();
        await skillRegistry.LoadAllAsync().ConfigureAwait(false);

        await app.RunAsync().ConfigureAwait(false);
    }
}

internal sealed class WebAppEntryPointAdapter : ILTAIEntryPoint
{
    public bool CanHandle(string command) => string.Equals(command, "webapp", StringComparison.OrdinalIgnoreCase);
    public Task RunAsync(string[] args) => EntryPoint.RunAsync(args);
}

public static class WebAppEntryPointRegistration
{
    static WebAppEntryPointRegistration() { LTAIEntryPointRegistry.Register("webapp", new WebAppEntryPointAdapter()); }
    public static void Initialize() { }
}
