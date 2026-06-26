using LTAI.Agent.Context;
using LTAI.Agent.Indexing;
using LTAI.Agent.Prompts;
using LTAI.Agent.Vector;
using LTAI.AI;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Core infrastructure: AgentToolStore, AgentRegistry, PromptLoader,
    /// agent definitions, durable agents, graph stores, embedders, routers.
    /// </summary>
    static IServiceCollection AddLTAIAgentCore(this IServiceCollection services,
        out IReadOnlyList<string> registeredAgentNames)
    {
        var names = new List<string>();

        services.AddSingleton<AgentToolStore>();
        services.AddSingleton<IAgentRegistry, AgentRegistry>();
        services.AddSingleton<IPromptLoader, PromptLoader>();

        foreach (var def in AgentDefinitionLoader.GetAgentDefinitions())
        {
            var captured = def;
            services.AddAIAgent(captured.Name, (sp, name) =>
            {
                var agent = captured.Build(sp, name);
                if (agent == null)
                    return new FallbackAgent(captured.Name, captured.Description);
                return agent;
            }, ServiceLifetime.Singleton);
            names.Add(captured.Name);
        }
        registeredAgentNames = names;

        services.AddLTAIDurableAgents();

        return services;
    }

    /// <summary>
    /// Register shared infrastructure singletons previously held as static fields on AgentBuilder.
    /// LspLanguageManager, MmapCache, MmapFileProvider, WriteBuffer.
    /// </summary>
    static IServiceCollection AddLTAIAgentSharedInfra(this IServiceCollection services)
    {
        services.AddSingleton<Caching.MmapCache>(sp =>
        {
            return new Caching.MmapCache(new Caching.MmapCacheOptions
            {
                WatchDirectories = [Directory.GetCurrentDirectory()]
            });
        });
        services.AddSingleton(sp =>
        {
            var mmap = sp.GetRequiredService<Caching.MmapCache>();
            return new Caching.MmapFileProvider(mmap);
        });
        services.AddSingleton(sp =>
        {
            var mmap = sp.GetRequiredService<Caching.MmapCache>();
            return new Caching.WriteBuffer(mmap: mmap);
        });
        services.AddSingleton<CodeAnalysis.TreeSitterParser>(sp =>
            new CodeAnalysis.TreeSitterParser(
                sp.GetService<ILogger<CodeAnalysis.TreeSitterParser>>()));
        services.AddSingleton<LanguageServer.LspLanguageManager>();

        // EditLedger: per-session file edit tracker with static forwarder for tool callers.
        services.AddSingleton<EditLedger>(sp =>
        {
            var ledger = new EditLedger();
            EditLedger.SetDefault(ledger);
            return ledger;
        });

        // AgentContextProviderBuilder: resolves common DI services via constructor,
        // leaving only per-agent params for the Build() call.
        services.AddSingleton<AgentContextProviderBuilder>();

        return services;
    }

    /// <summary>
    /// Graph stores, embedders, and routers (KgStore, GloVe, lookahead, contracts, Reranker, KbGraph, CgGraph).
    /// </summary>
    static IServiceCollection AddLTAIAgentGraphInfra(this IServiceCollection services)
    {
        services.AddSingleton<KgStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new KgStore(opts.ResolveDataPath("kg.db"));
        });
        services.AddKeyedSingleton<KgStore>("cg", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            // Phase 1.1: merged from cg.db → kg.db (both use KgStore schema)
            return new KgStore(opts.ResolveDataPath("kg.db"));
        });

        services.AddSingleton<Glove50Embedder>();
        services.AddSingleton<AgentLookaheadRouter>();
        services.AddSingleton<ContractRegistry>();
        services.AddSingleton<ContractWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ContractWatcher>());

        services.AddSingleton<Reranker>(sp =>
        {
            var embedder = sp.GetRequiredService<EmbeddingClient>();
            var llm = sp.GetRequiredService<IChatClient>();
            var store = sp.GetService<KgStore>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Reranker>();
            return new Reranker(embedder, llm, store, logger);
        });

        services.AddSingleton<KbGraph>(sp =>
        {
            var store = sp.GetRequiredService<KgStore>();
            var llm = sp.GetService<IChatClient>();
            var reranker = sp.GetService<Reranker>();
            var embedder = sp.GetService<EmbeddingClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<KbGraph>();
            return new KbGraph(store, llm, reranker, embedder, logger);
        });

        services.AddSingleton<CgGraph>(sp =>
        {
            var store = sp.GetRequiredKeyedService<KgStore>("cg");
            var llm = sp.GetService<IChatClient>();
            var embedder = sp.GetService<LTAI.AI.EmbeddingClient>();
            var parser = sp.GetRequiredService<CodeAnalysis.TreeSitterParser>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<CgGraph>();
            return new CgGraph(store, llm, embedder, parser, logger, Directory.GetCurrentDirectory());
        });

        // HyGRAG 社区摘要缓存 + 混合图查询 (TODO: implement CommunitySummaryStore + HybridGraphQuery)
        // services.AddSingleton<CommunitySummaryStore>(sp => { ... });
        // services.AddSingleton<HybridGraphQuery>(sp => { ... });

        // ── SAG-inspired services: dynamic hyperedges, event extraction, dual-mode search ──
        services.AddSingleton<DynamicHyperedgeQuery>(sp =>
        {
            var kgStore = sp.GetRequiredService<KgStore>();
            var logger = sp.GetService<ILogger<DynamicHyperedgeQuery>>();
            return new DynamicHyperedgeQuery(kgStore, logger);
        });
        services.AddSingleton<EventExtractor>(sp =>
        {
            var kgStore = sp.GetRequiredService<KgStore>();
            var logger = sp.GetService<ILogger<EventExtractor>>();
            return new EventExtractor(kgStore, logger);
        });
        services.AddSingleton<DualModeSearch>(sp =>
        {
            var kgStore = sp.GetRequiredService<KgStore>();
            var hyperedge = sp.GetRequiredService<DynamicHyperedgeQuery>();
            var logger = sp.GetService<ILogger<DualModeSearch>>();
            return new DualModeSearch(kgStore, hyperedge, logger);
        });

        return services;
    }
}
