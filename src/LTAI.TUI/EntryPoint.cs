using System.Runtime.InteropServices;
using LTAI.AI.Interfaces;
using System.Text;
using LTAI.AI;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.Agent.Tools;
using LTAI.Tools;
using LTAI.Tools.CodeEngine;
using LTAI.Tools.Reasoning;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Setup;
using LTAI.Core.System;
using LTAI.DNA;
using LTAI.Agent;
using LTAI.Agent.MAF;
using LTAI.Knowledge.Memory;
using LTAI.Knowledge.Vector;
using LTAI.TUI;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.TUI;

public static class EntryPoint
{
    public static async Task RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.TreatControlCAsInput = true;

        // ConsoleFont removed — TUI simplified

        Console.Title = "LTAI Dev Console";

        var configPath = Path.Combine(OptionService.Get("paths.config") ?? AppContext.BaseDirectory, "appsettings.json");
        var status = FirstRunDetector.Check(configPath);
        if (status.IsFirstRun)
        {
            FirstRunDetector.PrintDiagnostics(status);
            Console.WriteLine("检测到未配置，启动配置向导...");
            // InteractiveSetupWizard removed — configure provider manually
            Console.WriteLine("Configuration: set LTAI.AI.provider in appsettings.json");
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        var ltaiOptions = new LTAIOptions();
        var config = new ConfigurationBuilder()
            .SetBasePath(OptionService.Get("paths.config") ?? AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        config.GetSection(LTAIOptions.SectionName).Bind(ltaiOptions);

        if (ltaiOptions.AI.Providers.Count == 0)
        {
            var deepseekEndpoint = OptionService.Get("deepseek.endpoint") ?? "https://api.deepseek.com";
            var deepseekModel = OptionService.Get("deepseek.model") ?? "deepseek-v4-pro";
            var fastEndpoint = OptionService.Get("deepseek.fast.endpoint") ?? "https://api.deepseek.com";
            var fastModel = OptionService.Get("deepseek.fast.model") ?? "deepseek-v4-flash";
            ltaiOptions.AI.Providers["deepseek"] = new ProviderConfig { Endpoint = deepseekEndpoint, Model = deepseekModel };
            ltaiOptions.AI.Providers["deepseek-fast"] = new ProviderConfig { Endpoint = fastEndpoint, Model = fastModel };
            Console.WriteLine($"Providers loaded from config: deepseek({deepseekModel}), deepseek-fast({fastModel})");
        }

        services.AddSingleton(Options.Create(ltaiOptions));
        services.AddLTAICore();
        services.AddLTAIVectorAuto(apiModel: ltaiOptions.AI.GetLayerConfig("embedding").Model);
        services.AddLTAIAI();
        services.AddLTAIMemory();
        services.AddLTAIDNA();
        services.AddLTAICapability();
        services.AddLTAIAgent();
        services.AddLTAIMAF();

        var sp = services.BuildServiceProvider();

        var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
        await toolRegistry.RegisterAllToolCategoriesAsync();
        await sp.RegisterMarkdownToolsAsync(toolRegistry).ConfigureAwait(false);
        await sp.RegisterCodeActToolsAsync(toolRegistry).ConfigureAwait(false);

        var lts = sp.GetRequiredService<ILivingTreeSystem>();
        await lts.InitializeAsync();

        var dna = sp.GetService<DNAOrchestrator>();
        var reasoning = sp.GetService<ReasoningOrchestrator>();
        var analyzer = sp.GetService<MultiLangCodeAnalyzer>();
        var options = sp.GetService<IOptions<LTAIOptions>>();
        var svc = sp.GetService<ServiceManager>();
        var modelMgr = sp.GetService<ModelManager>();
        var agenticLoop = sp.GetService<AgenticLoop>();
        var kg = sp.GetService<KnowledgeGraph>();
        var skillRegistry = sp.GetService<LTAI.Agent.Skills.SkillRegistry>();
        var cps = sp.GetService<LTAI.Core.Governors.CPSProcessingService>();
        var scheduler = sp.GetService<LTAI.Core.Governors.CoordinationScheduler>();
        var paretoRouter = sp.GetService<LTAI.Core.Governors.ParetoRouter>();
        var kernel = sp.GetService<LTAI.Core.Governors.IMicroKernel>();

        var llmConfig = new LLMConfigPanel(options);
        var app = new TuiApp(lts, analyzer, llmConfig, options, agenticLoop,
            skillRegistry: skillRegistry, projectRoot: AppContext.BaseDirectory);
        await app.RunAsync();
    }
}

internal sealed class TuiEntryPointAdapter : ILTAIEntryPoint
{
    public bool CanHandle(string command) => string.Equals(command, "tui", StringComparison.OrdinalIgnoreCase);
    public Task RunAsync(string[] args) => EntryPoint.RunAsync(args);
}

public static class TuiEntryPointRegistration
{
    static TuiEntryPointRegistration() { LTAIEntryPointRegistry.Register("tui", new TuiEntryPointAdapter()); }
    public static void Initialize() { }
}
