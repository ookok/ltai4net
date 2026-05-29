using LTAI.AI;
using LTAI.Core.Safety;
using LTAI.Agent.Tools;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace LTAI.Agent;

public static class ServiceCollectionExtensions
{
#pragma warning disable MAAI001

    public static IServiceCollection AddLTAIAgent(this IServiceCollection services)
    {
        var ws = Directory.GetCurrentDirectory();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Application", "LTAI")
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}")
            .WriteTo.File("logs/ltai-agent-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        services.AddLogging(b => { b.ClearProviders(); b.AddSerilog(dispose: true); });

        services.AddSingleton<AIAgent>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var llm = sp.GetRequiredService<IChatClient>();

            // ── Tools for function calling ──
            var fs = new FileSystemTools(ws);
            var shell = new ShellTools(ws);
            var tools = new List<AITool>
            {
                AIFunctionFactory.Create(fs.ReadFile),
                AIFunctionFactory.Create(fs.WriteFile),
                AIFunctionFactory.Create(fs.ListFiles),
                AIFunctionFactory.Create(shell.ExecuteCommand),
            };

            // ── Safety guardrail (dedicated LLM client, not the pipeline) ──
            var safetyLogger = loggerFactory.CreateLogger<SafetyCoordinator>();
            var safetyApiKey = Environment.GetEnvironmentVariable(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY") ?? "";
            var safetyHttp = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            var safetyClient = new OpenAiHttpClient(safetyHttp,
                "https://api.deepseek.com/v1", "deepseek-chat", safetyApiKey);
            var safety = new SafetyCoordinator(safetyClient, safetyLogger);

            // ── Context compression ──
            var compaction = new CompactionProvider(
                new PipelineCompactionStrategy(
                    new ContextWindowCompactionStrategy(64000, opts.AI.MaxTokens),
                    new SummarizationCompactionStrategy(llm,
                        CompactionTriggers.TokensExceed(64000), 2)
                ), loggerFactory: loggerFactory);

            // ── Chat history ──
            var chatHistory = new InMemoryChatHistoryProvider();

            return new ChatClientAgent(llm,
                new ChatClientAgentOptions
                {
                    Name = "LTAI",
                    Description = $"你是 LTAI (小树), 一个 AI 助手。当前时间：{DateTime.Now:yyyy-MM-dd HH:mm (dddd)}。",
                    ChatOptions = new ChatOptions
                    {
                        Temperature = (float)opts.AI.Temperature,
                        MaxOutputTokens = opts.AI.MaxTokens,
                        Tools = tools,
                    },
                    ChatHistoryProvider = chatHistory,
                    AIContextProviders = [safety, compaction],
                    EnableMessageInjection = true,
                    RequirePerServiceCallChatHistoryPersistence = true,
                },
                loggerFactory, sp);
        });

        services.AddSingleton<ChatAgent>();

        return services;
    }
}
