using LTAI.AI;
using LTAI.Agent.Caching;
using LTAI.Agent.Experts;
using LTAI.Agent.Experts.Routing;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Tools;
using LTAI.Agent.CodeAnalysis;
using LTAI.Agent.Workflows;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// ChatAgent registration and escalation decider.
    /// Must run last because it depends on all previously registered services.
    /// </summary>
    static IServiceCollection AddLTAIAgentChat(this IServiceCollection services)
    {
        services.AddSingleton<IEscalationDecider, DefaultEscalationDecider>();

        services.AddSingleton<ChatAgent>(sp =>
        {
            var all = sp.GetKeyedServices<AIAgent>(KeyedService.AnyKey)
                .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
            var wf = sp.GetRequiredService<AgentWorkflows>();
            var chat = all[AgentNames.Chat];

            // MoE Expert routing layer wrapping LTAI-Chat
            var expertRouter = sp.GetRequiredService<ExpertRouter>();
            var expertFanOut = sp.GetRequiredService<ParallelFanOutExecutor>();
            var expertAggregator = sp.GetRequiredService<ExpertAggregator>();
            var expertRegistry = sp.GetRequiredService<ExpertRegistry>();
            chat = new ExpertRouterAgent(chat, expertRouter, expertFanOut, expertAggregator, expertRegistry,
                sp.GetService<ExpertFeedbackLogger>());

            var proAgent = all.TryGetValue(AgentNames.ChatPro, out var p) ? p : chat;
            var budget = sp.GetService<BudgetTracker>();

            var l1Cfg = sp.GetRequiredService<IOptions<LTAIOptions>>().Value.AI.L1;
            var l2Cfg = sp.GetRequiredService<IOptions<LTAIOptions>>().Value.AI.L2;
            bool sameModel = l1Cfg != null && l2Cfg != null
                && string.Equals(l1Cfg.Provider, l2Cfg.Provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(l1Cfg.Model, l2Cfg.Model, StringComparison.OrdinalIgnoreCase);

            return new ChatAgent(chat, proAgent, wf, budget,
                localEmbedder: sp.GetService<LocalEmbedder>(),
                httpFactory: sp.GetService<IHttpClientFactory>(),
                sameModel: sameModel,
                steerJudge: sp.GetKeyedService<IChatClient>("steer"),
                escalationDecider: sp.GetService<IEscalationDecider>(),
                tsParser: sp.GetService<TreeSitterParser>(),
                lspManager: sp.GetService<LanguageServer.LspLanguageManager>(),
                checkpointStore: sp.GetService<IMemoryCachingStore>(),
                escalationConfig: sp.GetRequiredService<IOptions<LTAIOptions>>().Value.Escalation,
                grammarCheck: sp.GetService<GrammarCheckStep>(),
                pipelineRunner: sp.GetService<Pipeline.PipelineRunner>(),
                logger: sp.GetService<ILogger<ChatAgent>>());
        });

        return services;
    }
}
