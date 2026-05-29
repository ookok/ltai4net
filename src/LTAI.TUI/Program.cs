using LTAI.Agent;

using LTAI.AI;
using LTAI.Core;
using LTAI.Core.Configuration;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.TUI;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "LTAI Dev Console";

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Information));

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        services.Configure<LTAIOptions>(config.GetSection(LTAIOptions.SectionName));
        services.AddLTAICore();
        services.AddLTAIAI();
        services.AddLTAIAgent();
        services.AddSingleton(_ => new DocumentService(Directory.GetCurrentDirectory()));

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<LTAIOptions>>();
        var chatAgent = sp.GetRequiredService<ChatAgent>();
        var router = sp.GetRequiredService<MultiProviderChatClient>();
        var llmConfig = new LLMConfigPanel(options, router);

        var app = new TuiApp(chatAgent, llmConfig, options, Directory.GetCurrentDirectory());
        await app.RunAsync();
    }
}
