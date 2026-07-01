using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Agent.Concurrency;
using LTAI.Agent.Context;
using LTAI.Agent.Scheduling;
using LTAI.Agent.Diagnostics;
using LTAI.Agent.Mcp;
using LTAI.Agent.Memory;
using LTAI.Agent.Orchestration;
using LTAI.Agent.Pipeline;
using LTAI.Agent.Services;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
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
    /// Workflow orchestration, routing, and scheduling services.
    /// DecisionTreeRouter, AgentWorkflows, MoAWorkflow, YAML workflow registry,
    /// agent management, budget tracking, steer model, background jobs.
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
            var registry = sp.GetRequiredService<ProviderRegistry>();
            var useReWoo = LTAI.Core.Configuration.EnvironmentConfig.ReWooEnabled;

            var clientKeys = new[] { "l2", "l2-alt", "l2-extra", "l3" };
            var proposers = new List<IChatClient>();

            foreach (var provider in registry.ActiveProviders.Take(proposerCount))
            {
                try
                {
                    var model = provider.Models.FirstOrDefault(m => m.ToolCall)?.ShortId
                        ?? provider.Models.FirstOrDefault()?.ShortId;
                    if (model == null) continue;
                    var apiKey = SecretManager.Get(provider.EnvVar) ?? "";
                    if (string.IsNullOrEmpty(apiKey)) continue;
                    var client = OpenAIChatClientFactory.Create(provider.Endpoint!, model, apiKey);
                    proposers.Add(WrapWithReWooIfEnabled(sp, client, useReWoo));
                    logger.LogDebug("MoA proposer from provider '{P}' model '{M}'", provider.Name, model);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "MoA: failed to create proposer from provider '{P}'", provider.Name);
                }
            }

            for (int i = 0; proposers.Count < proposerCount && i < clientKeys.Length; i++)
            {
                var client = sp.GetKeyedService<IChatClient>(clientKeys[i]);
                if (client != null) proposers.Add(WrapWithReWooIfEnabled(sp, client, useReWoo));
            }

            if (proposers.Count == 0)
            {
                var fallback = sp.GetKeyedService<IChatClient>("l2");
                if (fallback != null)
                    proposers = [WrapWithReWooIfEnabled(sp, fallback, useReWoo)];
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
                    "Configure multiple providers with API keys for effective MoA.");

            if (useReWoo && proposers.Count > 0)
                logger.LogInformation("MoA: ReWOO planning enabled for all {Count} proposers", proposers.Count);

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

        services.AddSingleton<Learning.SelfCritiqueGenerator>(sp =>
        {
            var steer = sp.GetKeyedService<IChatClient>("steer");
            return new Learning.SelfCritiqueGenerator(steer, sp.GetService<ILogger<Learning.SelfCritiqueGenerator>>());
        });

        services.AddSingleton<Learning.ReflectionGenerator>(sp =>
        {
            var steer = sp.GetKeyedService<IChatClient>("steer");
            return new Learning.ReflectionGenerator(steer, sp.GetService<ILogger<Learning.ReflectionGenerator>>());
        });

        services.AddSingleton<Memory.ReflectionStore>(sp =>
        {
            var palace = sp.GetRequiredService<PalaceStore>();
            var embedder = sp.GetService<EmbeddingClient>();
            var mstore = sp.GetService<MemoryStore>();
            var logger = sp.GetService<ILogger<Memory.ReflectionStore>>();
            return new Memory.ReflectionStore(palace, embedder, mstore, logger);
        });

        services.AddSingleton<Execution.DFSDToolExecutor>(sp =>
        {
            var steer = sp.GetKeyedService<IChatClient>("steer");
            var registry = sp.GetRequiredService<IToolRegistry>();
            return new Execution.DFSDToolExecutor(
                registry, steer, sp.GetService<ILogger<Execution.DFSDToolExecutor>>());
        });

        services.AddSingleton<Tools.CodeRepairAci>();

        services.AddSingleton<Tasks.TaskQueue>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new Tasks.TaskQueue(
                Tasks.SQLiteTaskStore.CreateShared(opts.ResolveDataPath("kg.db")));
        });

        return services;
    }

    private static IChatClient WrapWithReWooIfEnabled(IServiceProvider sp, IChatClient inner, bool enabled)
    {
        if (!enabled) return inner;
        var planner = sp.GetKeyedService<IChatClient>("l2");
        var toolReg = sp.GetRequiredService<IToolRegistry>();
        return new ReWOOPlanningChatClient(inner, planner, inner,
            sp.GetService<ILogger<ReWOOPlanningChatClient>>(), toolReg);
    }
}
