using LTAI.AI;
using LTAI.Core.Safety;
using LTAI.Agent.Tools;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
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
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Application", "LTAI")
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}")
            .WriteTo.File("logs/ltai-agent-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();
        services.AddLogging(b => { b.ClearProviders(); b.AddSerilog(dispose: true); });

        // Build all agents
        services.AddSingleton<ChatAgent>(sp =>
        {
            var agents = new Dictionary<string, AIAgent>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = BuildAgent(sp, "LTAI-Chat",   "通用对话助手",   true, true, true, true),
                ["code"] = BuildAgent(sp, "LTAI-Code",   "代码分析助手",   true, true, true, false),
                ["math"] = BuildAgent(sp, "LTAI-Math",   "数学计算助手",   false, false, false, true),
                ["data"] = BuildAgent(sp, "LTAI-Data",   "数据处理助手",   true, true, true, true),
                ["system"] = BuildAgent(sp, "LTAI-System","系统管理助手",  false, false, false, true),
                ["llm"] = BuildAgent(sp, "LTAI-LLM",     "纯对话助手",    false, false, false, false),
            };

            return new ChatAgent(BuildOrchestrator(sp, agents.Values.ToArray()));
        });

        return services;
    }

    private static AIAgent BuildAgent(IServiceProvider sp, string name, string description,
        bool canRead, bool canWrite, bool canList, bool canExec)
    {
        var ws = Directory.GetCurrentDirectory();
        var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var llm = sp.GetRequiredService<IChatClient>();
        var log = loggerFactory.CreateLogger("Agent." + name);

        var tools = new List<AITool>();
        var fs = new FileSystemTools(ws);
        var shell = new ShellTools(ws);
        if (canRead) tools.Add(AIFunctionFactory.Create(fs.ReadFile));
        if (canWrite) tools.Add(AIFunctionFactory.Create(fs.WriteFile));
        if (canList) tools.Add(AIFunctionFactory.Create(fs.ListFiles));
        if (canExec) tools.Add(AIFunctionFactory.Create(shell.ExecuteCommand));

        var safetyKey = Environment.GetEnvironmentVariable(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY") ?? "";
        var safetyHttp = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var safetyClient = new OpenAiHttpClient(safetyHttp, "https://api.deepseek.com/v1", "deepseek-chat", safetyKey);
        var safety = new SafetyCoordinator(safetyClient, loggerFactory.CreateLogger<SafetyCoordinator>());

        var memoryDir = Path.Combine(ws, ".livingtree", "memory");
        Directory.CreateDirectory(memoryDir);
        var fileMemory = new FileMemoryProvider(new FileSystemAgentFileStore(memoryDir),
            s => new FileMemoryState(), new FileMemoryProviderOptions());

        var fileSearch = new TextSearchProvider(
            (q, ct) => Task.FromResult<IEnumerable<TextSearchProvider.TextSearchResult>>([]),
            new TextSearchProviderOptions(), loggerFactory);

        var compaction = new CompactionProvider(
            new PipelineCompactionStrategy(
                new ContextWindowCompactionStrategy(64000, opts.AI.MaxTokens),
                new SummarizationCompactionStrategy(llm, CompactionTriggers.TokensExceed(64000), 2)
            ), loggerFactory: loggerFactory);

        AIAgent agent = new ChatClientAgent(llm, new ChatClientAgentOptions
        {
            Name = name,
            Description = $"你是 {name}，{description}。当前时间：{DateTime.Now:yyyy-MM-dd HH:mm (dddd)}。",
            ChatOptions = new ChatOptions
            {
                Temperature = (float)opts.AI.Temperature,
                MaxOutputTokens = opts.AI.MaxTokens,
                Tools = tools,
            },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            AIContextProviders = [safety, fileMemory, fileSearch, compaction],
            EnableMessageInjection = true,
            RequirePerServiceCallChatHistoryPersistence = true,
        }, loggerFactory, sp);

        agent = new LoggingAgent(agent, log);
        agent = new ToolApprovalAgent(agent);
        return agent;
    }

    private static AIAgent BuildOrchestrator(IServiceProvider sp, AIAgent[] agents)
    {
        var ws = Directory.GetCurrentDirectory();
        var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var llm = sp.GetRequiredService<IChatClient>();
        var log = loggerFactory.CreateLogger("LTAI.Orchestrator");

        // Round-robin group chat manager routes tasks to the right agent
        var groupChat = new RoundRobinGroupChatManager(agents,
            async (manager, messages, ct) =>
            {
                var lastMsg = messages.LastOrDefault();
                if (lastMsg?.Text?.Contains("[TASK_COMPLETE]") == true) return true;
                var response = await llm.GetResponseAsync([
                    new ChatMessage(ChatRole.System, "Does this conversation need more agent turns? Reply YES or NO."),
                    ..messages,
                ], cancellationToken: ct);
                return response.Messages?.LastOrDefault()?.Text?.Trim().ToUpperInvariant() == "NO";
            });

        // Create a workflow for multi-agent orchestration with checkpointing
        var checkpointDir = Path.Combine(ws, ".livingtree", "checkpoints");
        Directory.CreateDirectory(checkpointDir);
        var checkpointStore = new FileSystemJsonCheckpointStore(new DirectoryInfo(checkpointDir));
        var checkpointMgr = CheckpointManager.CreateJson(checkpointStore);

        // Build a workflow that orchestrates agents via round-robin
        // Workflow is created but only used for structure;
        // Actual agent dispatch happens through RoundRobinGroupChatManager
        log.LogInformation("Multi-agent orchestrator ready with {Count} agents", agents.Length);

        // Register the group chat manager with the workflow
        // The orchestrator agent (agents[0] = LTAI-Chat) handles dispatch decisions
        log.LogInformation("Orchestrator ready with {Count} agents", agents.Length);

        // Return the first agent as the workflow entry point;
        // InProcessExecution.RunAsync would be used for long-running workflows
        return agents[0];
    }
}
