using System.Runtime.InteropServices;
using LTAI.AI.Interfaces;
using System.Text;
using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.Agent.Tools;
using LTAI.Core.Messaging;
using LTAI.Tools;
using LTAI.Tools.CodeEngine;
using LTAI.Tools.Reasoning;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Setup;
using LTAI.Core.System;
using LTAI.DNA;
using LTAI.Agent;
using LTAI.Knowledge.Memory;
using LTAI.Knowledge.Vector;
using LTAI.TUI;
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { ConsoleFont.SetMapleMono(); } catch { }
        }

        Console.Title = "LTAI Dev Console";

        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var status = FirstRunDetector.Check(configPath);
        if (status.IsFirstRun)
        {
            FirstRunDetector.PrintDiagnostics(status);
            Console.WriteLine("检测到未配置，启动配置向导...");
            await new InteractiveSetupWizard(configPath).RunAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        var ltaiOptions = new LTAIOptions();
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        config.GetSection(LTAIOptions.SectionName).Bind(ltaiOptions);

        if (ltaiOptions.AI.Providers.Count == 0)
        {
            ltaiOptions.AI.Providers["deepseek"] = new ProviderConfig { Endpoint = "https://api.deepseek.com", Model = "deepseek-v4-pro" };
            ltaiOptions.AI.Providers["deepseek-fast"] = new ProviderConfig { Endpoint = "https://api.deepseek.com", Model = "deepseek-v4-flash" };
            Console.WriteLine("未检测到提供商配置，使用 DeepSeek 默认配置。");
        }

        services.AddSingleton(Options.Create(ltaiOptions));
        services.AddLTAICore();
        services.AddLTAIVectorAuto(apiModel: ltaiOptions.AI.L0.Model);
        services.AddLTAIAI();
        services.AddLTAIMemory();
        services.AddLTAIDNA();
        services.AddLTAICapability();
        services.AddLTAIMAF();

        var sp = services.BuildServiceProvider();

        var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
        await toolRegistry.RegisterAllToolCategoriesAsync();

        var lts = sp.GetRequiredService<LivingTreeSystem>();
        await lts.InitializeAsync();

        var dna = sp.GetService<DNAOrchestrator>();
        var reasoning = sp.GetService<ReasoningOrchestrator>();
        var analyzer = sp.GetService<MultiLangCodeAnalyzer>();
        var options = sp.GetService<IOptions<LTAIOptions>>();
        var svc = sp.GetService<ServiceManager>();
        var modelMgr = sp.GetService<ModelManager>();

        var app = new TuiApp(lts, dna, reasoning, analyzer, options, svc, modelMgr);
        await app.RunAsync();
    }
}
