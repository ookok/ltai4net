using LTAI.Agent.Agents;
using LTAI.Agent.MAF;
using LTAI.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LTAI.Agent;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLTAIAgent(this IServiceCollection services)
    {
        var ws = Directory.GetCurrentDirectory();

        // Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Application", "LTAI")
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}")
            .WriteTo.File("logs/ltai-agent-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        services.AddLogging(builder => { builder.ClearProviders(); builder.AddSerilog(dispose: true); });

        // Core agent pipeline: ChatAgent → LTAIAgent → LivingTreeChatClient → IChatClient (from AI)
        services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<ILogger<LTAIAgent>>();
            var llm = sp.GetRequiredService<IChatClient>();
            return new LTAIAgent(new LivingTreeChatClient(llm), log);
        });

        services.AddSingleton<ChatAgent>();

        // AI-callable tools (available for function calling when wired)
        services.AddSingleton(new FileSystemTools(ws));
        services.AddSingleton(new ShellTools(ws));

        return services;
    }
}
