using System.Text;
using LTAI.AI;
using LTAI.AI.Governors;
using LTAI.AI.Providers;
using LTAI.Tools;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Core.Interfaces;
using LTAI.DNA;
using LTAI.DNA.Safety;
using LTAI.Infra.Sandbox;
using LTAI.Knowledge.Vector;
using LTAI.MCP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

if (args.Contains("--version") || args.Contains("-v"))
{
    Console.WriteLine("LTAI MCP Server v5.5.0");
    return 0;
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
    ltaiOptions.AI.Providers["deepseek"] = new ProviderConfig { Endpoint = "https://api.deepseek.com", Model = "deepseek-chat" };
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
var lts = sp.GetRequiredService<LivingTreeSystem>();
await lts.InitializeAsync();

var server = sp.GetRequiredService<MCPServer>();
var transport = sp.GetRequiredService<IMCPTransport>();

await transport.StartAsync(server);
return 0;
