using System.Runtime.InteropServices;
using LTAI.AI.Interfaces;
using System.Text;
using LTAI.AI;
using LTAI.Core.Interfaces;
using LTAI.Core.Messaging;
using LTAI.AI.Governors;
using LTAI.AI.Providers;
using LTAI.Agent;
using LTAI.Agent.Tools;
using LTAI.Knowledge.Core;
using LTAI.Core.Messaging;
using LTAI.Tools;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using LTAI.Core.Setup;
using LTAI.DNA;
using LTAI.DNA.Safety;
using LTAI.Infra.Sandbox;
using LTAI.Knowledge.Vector;
using LTAI.MCP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.MCP;

public static class EntryPoint
{
    public static async Task RunAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine("LTAI MCP Server V0.51.0");
            return;
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var status = FirstRunDetector.Check(configPath);
        if (status.IsFirstRun)
        {
            FirstRunDetector.PrintDiagnostics(status);
            Console.WriteLine("检测到未配置，启动配置向导...");
            await new InteractiveSetupWizard(configPath).RunAsync().ConfigureAwait(false);
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddConsole());

        var ltaiOptions = new LTAIOptions();
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        config.GetSection(LTAIOptions.SectionName).Bind(ltaiOptions);

        if (ltaiOptions.AI.Providers.Count == 0)
        {
            ltaiOptions.AI.Providers["deepseek"] = new ProviderConfig { Endpoint = OptionService.Get("deepseek.endpoint") ?? "https://api.deepseek.com", Model = OptionService.Get("deepseek.model") ?? "deepseek-chat" };
            Console.WriteLine("Providers loaded from config: deepseek");
        }

        services.AddSingleton(Options.Create(ltaiOptions));
        services.AddLTAICore();
        services.AddLTAIVectorAuto(apiModel: ltaiOptions.AI.L0.Model);
        services.AddLTAIAI();
        services.AddLTAIDNA();
        services.AddLTAICapability();
        services.AddLTAISandbox();
        services.AddSingleton<MCPServer>();
        services.AddSingleton<IMCPTransport, StdioTransport>();

        var sp = services.BuildServiceProvider();

        var toolRegistry = sp.GetRequiredService<AIToolRegistry>();
        await toolRegistry.RegisterAllToolCategoriesAsync().ConfigureAwait(false);

        var lts = sp.GetRequiredService<ILivingTreeSystem>();
        await lts.InitializeAsync().ConfigureAwait(false);

        var server = sp.GetRequiredService<MCPServer>();
        var transport = sp.GetRequiredService<IMCPTransport>();
        await transport.StartAsync(server).ConfigureAwait(false);
    }

internal sealed class McpEntryPointAdapter : ILTAIEntryPoint
{
    public bool CanHandle(string command) => string.Equals(command, "mcp", StringComparison.OrdinalIgnoreCase);
    public Task RunAsync(string[] args) => EntryPoint.RunAsync(args);
}

public static class McpEntryPointRegistration
{
    static McpEntryPointRegistration() { LTAIEntryPointRegistry.Register("mcp", new McpEntryPointAdapter()); }
    public static void Initialize() { }
}
}
