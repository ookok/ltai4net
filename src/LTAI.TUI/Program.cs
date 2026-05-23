using System.Runtime.InteropServices;
using System.Text;
using LTAI.AI;
using LTAI.AI.Governors;
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
using LTAI.TUI;
using LTAI.Knowledge.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    try { ConsoleFont.SetMapleMono(); } catch { /* non-fatal */ }
}

Console.Title = "LTAI Dev Console";

var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var isFirstRun = !File.Exists(configPath) || new FileInfo(configPath).Length < 50;

if (isFirstRun)
{
    Console.WriteLine("检测到首次运行，启动配置向导...");
    var setupWizard = new InteractiveSetupWizard(configPath);
    await setupWizard.RunAsync();
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
    Console.WriteLine("未检测到提供商配置，使用 DeepSeek 默认配置。运行 'ltai setup' 重新配置。");
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
var lts = sp.GetRequiredService<LivingTreeSystem>();
await lts.InitializeAsync();

var dna = sp.GetService<DNAOrchestrator>();
var reasoning = sp.GetService<ReasoningOrchestrator>();
var analyzer = sp.GetService<MultiLangCodeAnalyzer>();
var options = sp.GetService<IOptions<LTAIOptions>>();
var service = sp.GetService<ServiceManager>();
var modelMgr = sp.GetService<ModelManager>();

var app = new TuiApp(lts, dna, reasoning, analyzer, options, service, modelMgr);
await app.RunAsync();
