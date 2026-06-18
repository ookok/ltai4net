using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Agent.Pipeline.Steps;
using LTAI.Agent.Context;
using LTAI.Agent.Concurrency;
using LTAI.Agent.Scheduling;
using LTAI.Agent.Diagnostics;
using LTAI.Agent.Experts;
using LTAI.Agent.Experts.Adapters;
using LTAI.Agent.Experts.Routing;
using LTAI.Agent.Memory;
using LTAI.Agent.Mcp;
using LTAI.Agent.Orchestration;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Services;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.Agent.Workflows;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Agents.AI.Workflows.Declarative.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// MoE Experts: QueryEmbeddingCache, 7× IExpertModule, ExpertRegistry,
    /// ExpertRouter, FanOut, Aggregator, Feedback, Entropy, MemoryCompressor, FactExtractor.
    /// </summary>
    static IServiceCollection AddLTAIAgentExperts(this IServiceCollection services)
    {
        services.AddSingleton<QueryEmbeddingCache>();

        services.AddSingleton<IExpertModule, KbGraphExpert>(sp =>
        {
            var kbGraph = sp.GetRequiredService<KbGraph>();
            var kgStore = sp.GetRequiredService<KgStore>();
            return new KbGraphExpert(kbGraph, kgStore);
        });
        services.AddSingleton<IExpertModule>(sp =>
            new ShardedCgGraphExpert(sp.GetRequiredService<CgGraph>()));
        services.AddSingleton<IExpertModule>(sp =>
            DocumentExpert.CreateApiDocExpert(sp.GetRequiredService<KbGraph>()));
        services.AddSingleton<IExpertModule>(sp =>
            DocumentExpert.CreateRunbookExpert(sp.GetRequiredService<KbGraph>()));
        services.AddSingleton<IExpertModule>(sp =>
            DocumentExpert.CreateDesignDocExpert(sp.GetRequiredService<KbGraph>()));
        services.AddSingleton<IExpertModule, ToolExpert>(sp =>
            new ToolExpert(sp.GetRequiredService<EmbeddingClient>()));
        services.AddSingleton<IExpertModule, SkillExpert>(sp =>
        {
            var skillsDir = ResolveSkillsDir();
            Directory.CreateDirectory(skillsDir);
            return new SkillExpert(skillsDir);
        });

        services.AddSingleton<ExpertRegistry>(sp =>
        {
            var experts = sp.GetRequiredService<IEnumerable<IExpertModule>>();
            var embedder = sp.GetRequiredService<EmbeddingClient>();
            var cache = sp.GetService<ToolEmbeddingCache>();
            var queryCache = sp.GetService<QueryEmbeddingCache>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExpertRegistry>();
            return new ExpertRegistry(experts, embedder, cache, queryCache, logger);
        });

        services.AddSingleton<ExpertRouter>(sp =>
        {
            var registry = sp.GetRequiredService<ExpertRegistry>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExpertRouter>();
            return new ExpertRouter(registry, logger);
        });
        services.AddSingleton<ParallelFanOutExecutor>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ParallelFanOutExecutor>();
            return new ParallelFanOutExecutor(logger);
        });
        services.AddSingleton<ExpertAggregator>(sp =>
        {
            var embedder = sp.GetService<EmbeddingClient>();
            return new ExpertAggregator(embedder);
        });
        services.AddSingleton<ExpertFeedbackLogger>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExpertFeedbackLogger>();
            return new ExpertFeedbackLogger(logger);
        });
        services.AddSingleton<EntropyTracker>(sp =>
        {
            var feedback = sp.GetService<ExpertFeedbackLogger>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<EntropyTracker>();
            return new EntropyTracker(feedback, logger);
        });
        services.AddSingleton<MemoryCompressor>(sp =>
        {
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MemoryCompressor>();
            return new MemoryCompressor(l3, logger);
        });
        services.AddSingleton<FactExtractor>(sp =>
        {
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<FactExtractor>();
            return new FactExtractor(l3, logger);
        });

        return services;
    }

    /// <summary>
    /// Workflow orchestration, pipeline, routing, and scheduling services.
    /// DecisionTreeRouter, AgentWorkflows, MoAWorkflow, YAML workflow registry,
    /// PipelineRunner with steps, agent management, budget tracking.
    /// </summary>
    static IServiceCollection AddLTAIAgentWorkflows(this IServiceCollection services)
    {
        services.AddSingleton<RetryChainEmbedder>();
        services.AddSingleton<RoutingDiagnosticsStore>(sp =>
            new RoutingDiagnosticsStore(
                sp.GetRequiredService<IOptions<LTAIOptions>>().Value.DataDirectory,
                sp.GetRequiredService<ILogger<RoutingDiagnosticsStore>>()));
        services.AddSingleton<DecisionTreeRouter>(sp => new DecisionTreeRouter(
            sp.GetService<EmbeddingClient>(),
            sp.GetRequiredService<ILogger<DecisionTreeRouter>>(),
            sp.GetService<ToolEmbeddingCache>(),
            options: null,
            registry: sp.GetService<YAMLWorkflowRegistry>(),
            steer: sp.GetKeyedService<IChatClient>("steer"),
            retryChain: sp.GetService<RetryChainEmbedder>()));

        services.AddSingleton<AgentWorkflows>(sp =>
        {
            var all = sp.GetKeyedServices<AIAgent>(KeyedService.AnyKey)
                .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
            var routerAgent = all.TryGetValue(AgentNames.Router, out var ra) ? ra
                : throw new InvalidOperationException($"{AgentNames.Router} agent not registered");
            return new AgentWorkflows(all.Values, routerAgent,
                sp.GetRequiredService<ILogger<AgentWorkflows>>(),
                sp.GetRequiredService<DecisionTreeRouter>(),
                workflowRegistry: sp.GetService<YAMLWorkflowRegistry>(),
                diagnosticsStore: sp.GetService<RoutingDiagnosticsStore>(),
                queryClassifier: sp.GetService<QueryClassifier>(),
                checkpointDirectory: Path.Combine(
                    sp.GetRequiredService<IOptions<LTAIOptions>>().Value.DataDirectory,
                    "workflows", ".checkpoints"));
        });

        services.AddKeyedSingleton<MoAWorkflow>("moa", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value.AI;
            var proposerCount = Math.Max(1, opts.MoaProposerCount);
            var aggregatorCount = Math.Max(1, opts.MoaAggregatorCount);
            var logger = sp.GetRequiredService<ILogger<MoAWorkflow>>();

            var clientKeys = new[] { "l2", "l2-alt", "l2-extra", "l3" };
            var proposers = new List<IChatClient>();
            for (int i = 0; i < proposerCount; i++)
            {
                var key = i < clientKeys.Length ? clientKeys[i] : "l2";
                var client = sp.GetKeyedService<IChatClient>(key);
                if (client != null) proposers.Add(client);
            }
            if (proposers.Count == 0)
            {
                var fallback = sp.GetKeyedService<IChatClient>("l2");
                if (fallback != null)
                    proposers = Enumerable.Repeat(fallback, proposerCount).ToList()!;
            }

            var aggregators = new List<IChatClient>();
            for (int i = 0; i < aggregatorCount; i++)
            {
                var key = i < clientKeys.Length ? clientKeys[i] : "l2";
                var client = sp.GetKeyedService<IChatClient>(key);
                if (client != null) aggregators.Add(client);
            }
            if (aggregators.Count == 0)
            {
                var fallback = sp.GetKeyedService<IChatClient>("l2");
                if (fallback != null)
                    aggregators = Enumerable.Repeat(fallback, aggregatorCount).ToList()!;
            }

            if (proposers.Distinct().Count() == 1 && proposerCount > 1)
                logger.LogWarning("MoA: all proposers use the same IChatClient instance — proposals will not be diverse. " +
                    "Configure multiple L2 backends (l2/l2-alt/l3) for effective MoA.");

            return new MoAWorkflow(proposers, aggregators, logger,
                opts.MoaTimeoutSeconds > 0 ? TimeSpan.FromSeconds(opts.MoaTimeoutSeconds) : null);
        });

        services.AddSingleton<IMcpToolHandler, DefaultMcpToolHandler>();
        services.AddSingleton<WorkflowHotReloadNotifier>();
        services.AddSingleton<YAMLWorkflowRegistry>(sp => new YAMLWorkflowRegistry(
            sp.GetRequiredService<IOptions<LTAIOptions>>(),
            sp.GetRequiredService<ILogger<YAMLWorkflowRegistry>>(),
            sp.GetRequiredService<WorkflowHotReloadNotifier>(),
            sp.GetService<IMcpToolHandler>()));
        services.AddSingleton<YAMLWorkflowWatcher>(sp => new YAMLWorkflowWatcher(
            sp.GetRequiredService<YAMLWorkflowRegistry>().WatchDirectory,
            sp.GetRequiredService<YAMLWorkflowRegistry>(),
            sp.GetRequiredService<ILogger<YAMLWorkflowWatcher>>()));

        services.AddHostedService<AutoTunerService>();
        services.AddHostedService<WorkflowWatcherHostedService>();
        services.AddHostedService<LTAI.Agent.Services.GraphInitService>();
        services.AddHostedService<WarmupService>();

        services.AddSingleton<DevUI.LTAIDevUIService>();
        services.AddSingleton<Tooling.AgentModeObserver>();

        services.AddSingleton<QualityGateStep>();
        services.AddSingleton<SafetyCheckStep>();
        services.AddSingleton<ToolExecutionStep>();
        services.AddSingleton<CompactionStep>();
        services.AddSingleton<DoDCheckStep>();
        services.AddSingleton<RetrospectiveStep>();
        services.AddSingleton<GrammarCheckStep>(sp =>
            new GrammarCheckStep(
                logger: sp.GetService<ILogger<GrammarCheckStep>>(),
                tsParser: sp.GetService<CodeAnalysis.TreeSitterParser>(),
                lspManager: sp.GetService<LanguageServer.LspLanguageManager>()));

        services.AddSingleton<PipelineRunner>();
        services.AddSingleton<SolutionPool>();
        services.AddHostedService<ActiveContextCompressor>();
        services.AddTransient<Func<HypothesisRouterContext>>(_ =>
            () => HypothesisRouterContext.Create().Add("default").Build());

        services.AddSingleton<AgentWIPManager>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<LTAIOptions>>().Value.AgentManagement;
            return new AgentWIPManager(cfg.WipLimit, cfg.WipLimitPro);
        });
        services.AddSingleton<CapacityPlanner>(sp =>
        {
            var _ = sp.GetRequiredService<IOptions<LTAIOptions>>().Value.AgentManagement;
            return new CapacityPlanner();
        });
        services.AddSingleton<TieredCompressor>();
        services.AddHostedService<MemoryRefinery>();

        services.AddSingleton<LTAI.AI.BudgetTracker>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new LTAI.AI.BudgetTracker(
                globalMax: opts.AI.GlobalTokenBudget,
                perUserMax: opts.AI.PerUserTokenBudget);
        });

        services.AddKeyedSingleton<IChatClient>("steer", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var steer = opts.Steer;
            if (!steer.Enabled)
            {
                var log = sp.GetService<ILoggerFactory>()?.CreateLogger("LTAI.Steer");
                log?.LogDebug("Steer model disabled via config");
                return null!;
            }

            var steerKey = SecretManager.Get(steer.ApiKeyEnv);
            if (string.IsNullOrEmpty(steerKey))
            {
                var log = sp.GetService<ILoggerFactory>()?.CreateLogger("LTAI.Steer");
                log?.LogWarning("Steer model enabled but {EnvVar} is not set — disabling", steer.ApiKeyEnv);
                return null!;
            }

            return OpenAIChatClientFactory.Create(steer.Endpoint, steer.Model, steerKey);
        });

        services.AddSingleton<BackgroundJobService>();
        services.AddSingleton<Mcp.McpClientFactory>();

        services.AddSingleton<Tasks.TaskQueue>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new Tasks.TaskQueue(
                Tasks.SQLiteTaskStore.CreateShared(opts.ResolveDataPath("kg.db")));
        });

        return services;
    }
}
