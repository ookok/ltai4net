using LTAI.AI;
using LTAI.Agent.Indexing;
using LTAI.Agent.Learning;
using LTAI.Agent.SeedER;
using LTAI.Agent.Tools;
using LTAI.Agent.CodeAnalysis;
using LTAI.Agent.Vector;
using LTAI.Agent.Execution;
using LTAI.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Indexing services, knowledge tools, code chunk index,
    /// seedER, skill evolution engine, summarizer tools.
    /// </summary>
    static IServiceCollection AddLTAIAgentIndexingAndTools(this IServiceCollection services)
    {
        // Document indexing pipeline
        services.AddSingleton<DocumentPageAnnotator>(sp =>
        {
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<DocumentPageAnnotator>();
            return new DocumentPageAnnotator(l3, logger);
        });
        services.AddSingleton<DocumentIndexer>();
        services.AddSingleton<IndexQueueWorker>(sp =>
        {
            var indexer = sp.GetRequiredService<DocumentIndexer>();
            var queue = sp.GetRequiredService<Tasks.TaskQueue>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<IndexQueueWorker>();
            return new IndexQueueWorker(indexer, queue, logger!);
        });
        services.AddSingleton<RetryQueueWorker>(sp =>
        {
            var client = sp.GetRequiredService<MultiProviderChatClient>();
            var queue = sp.GetRequiredService<Tasks.TaskQueue>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<RetryQueueWorker>();
            return new RetryQueueWorker(client, queue, logger!);
        });

        // Knowledge extraction
        services.AddSingleton<KnowledgeExtractor>(sp =>
        {
            var kg = sp.GetRequiredService<KgStore>();
            var llm = sp.GetRequiredService<IChatClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<KnowledgeExtractor>();
            return new KnowledgeExtractor(kg, llm, logger);
        });
        services.AddSingleton<KnowledgeQualityScorer>();
        services.AddSingleton<ProvenanceProvider>(sp =>
        {
            var tracker = sp.GetRequiredService<ProvenanceTracker>();
            var codeProv = sp.GetService<Delta.CodeProvenanceIndex>();
            return new ProvenanceProvider(tracker, codeProv);
        });
        services.AddSingleton<KnowledgeAssetTool>();
        services.AddSingleton<TaskQueueTool>(sp =>
            new TaskQueueTool(
                sp.GetRequiredService<Tasks.TaskQueue>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<TaskQueueTool>()));

        // Code analysis
        services.AddSingleton<CodeChunkIndex>(sp =>
        {
            var store = sp.GetRequiredService<KgStore>();
            var parser = sp.GetRequiredService<TreeSitterParser>();
            return new CodeChunkIndex(store, parser,
                sp.GetService<EmbeddingClient>(),
                sp.GetService<ILogger<CodeChunkIndex>>(),
                Directory.GetCurrentDirectory());
        });

        // Wasmtime sandbox: WASM-based code execution (shared Singleton to avoid native engine leak)
        services.AddSingleton<WasmtimeSandbox>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<WasmtimeSandbox>();
            return new WasmtimeSandbox(Directory.GetCurrentDirectory(), logger);
        });

        services.AddSingleton<FailureMiner>();

        // User-facing tools
        services.AddSingleton<QuestionService>();
        services.AddSingleton<QuestionTool>();
        services.AddSingleton<ClusterSummarizer>(sp =>
            new ClusterSummarizer(
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<ClusterSummarizer>()));
        services.AddSingleton<DeepenSearchTool>(sp =>
            new DeepenSearchTool(
                sp.GetRequiredService<Vector.KbGraph>(),
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<DeepenSearchTool>()));

        // SeedER
        services.AddSingleton<PathExplorer>(sp =>
            new PathExplorer(sp.GetRequiredService<KgStore>()));
        services.AddSingleton<SeedER.SeedER>(sp =>
            new SeedER.SeedER(
                sp.GetRequiredService<KgStore>(),
                sp.GetRequiredService<PathExplorer>(),
                sp.GetService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<SeedER.SeedER>()));
        services.AddSingleton<SeedERTool>(sp =>
            new SeedERTool(
                sp.GetRequiredService<SeedER.SeedER>(),
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<SeedERTool>()));

        // Model switch bridge
        services.AddSingleton<LocalEmbedderModelSwitchNotifier>(sp =>
            new LocalEmbedderModelSwitchNotifier(
                sp.GetRequiredService<IToolRegistry>(),
                sp.GetService<LocalEmbedder>()));

        // Skill evolution engine
        services.AddSingleton<SkillValidationGate>(sp =>
        {
            var judge = sp.GetKeyedService<IChatClient>("steer") ?? sp.GetRequiredService<IChatClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SkillValidationGate>();
            return new SkillValidationGate(judge, logger, ResolveSkillsDir());
        });
        services.AddSingleton<SkillEditBudget>(sp =>
            new SkillEditBudget(ResolveSkillsDir()));
        services.AddSingleton<SkillRejectedBuffer>(sp =>
            new SkillRejectedBuffer(ResolveSkillsDir()));
        services.AddSingleton<SkillEvalBenchmark>(sp =>
        {
            var gate = sp.GetRequiredService<SkillValidationGate>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SkillEvalBenchmark>();
            return new SkillEvalBenchmark(gate, logger, ResolveSkillsDir());
        });
        services.AddSingleton<SkillEvolutionEngine>(sp =>
        {
            var llm = sp.GetRequiredService<IChatClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SkillEvolutionEngine>();
            return new SkillEvolutionEngine(llm, logger, ResolveSkillsDir(),
                validationGate: sp.GetService<SkillValidationGate>(),
                editBudget: sp.GetService<SkillEditBudget>(),
                rejectedBuffer: sp.GetService<SkillRejectedBuffer>(),
                evalBenchmark: sp.GetService<SkillEvalBenchmark>());
        });

        // P6: Bounded session state stores (replace static ConcurrentDictionary instances)
        services.AddSingleton<PlanStore>();
        services.AddSingleton<TaskStore>();
        services.AddSingleton<Long2ShortTracker>();

        // DeltaDB-inspired services (content-addressed delta log, CRDT worktree, code provenance)
        services.AddSingleton<Delta.DeltaStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LTAI.Core.Configuration.LTAIOptions>>().Value;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Delta.DeltaStore>();
            var store = new Delta.DeltaStore(opts.ResolveDataPath("deltas.db"), logger);
            // Wire into FileSystemTools for delta tracking on every write
            Tools.FileSystemTools.SetDeltaStore(store);
            return store;
        });

        // ContextOffloader with predictive tracker + semantic compressor
        services.AddSingleton<Memory.ContextOffloader>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Memory.ContextOffloader>();
            var deltaStore = sp.GetService<Delta.DeltaStore>();
            var predictive = sp.GetService<Memory.PredictiveOffloadTracker>();
            var semantic = sp.GetService<Context.SemanticCompressor>();
            return new Memory.ContextOffloader(logger, deltaStore, predictive, semantic);
        });
        services.AddSingleton<Delta.CrdtWorktree>(sp =>
        {
            var store = sp.GetRequiredService<Delta.DeltaStore>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Delta.CrdtWorktree>();
            return new Delta.CrdtWorktree(store, logger);
        });
        services.AddSingleton<Delta.CodeProvenanceIndex>(sp =>
        {
            var store = sp.GetRequiredService<Delta.DeltaStore>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Delta.CodeProvenanceIndex>();
            return new Delta.CodeProvenanceIndex(store, logger);
        });

        // Wire DeltaStore into ProvenanceTracker
        services.AddSingleton<Indexing.ProvenanceTracker>(sp =>
        {
            var tracker = new Indexing.ProvenanceTracker();
            tracker.DeltaStore = sp.GetService<Delta.DeltaStore>();
            return tracker;
        });

        // Pipeline step: DeltaAnchor — generates delta IDs for tool-executed file edits
        services.AddSingleton<Pipeline.Steps.DeltaAnchorStep>();

        // Semantic compression (MiniLM/BGE sentence importance)
        services.AddSingleton<Context.SemanticCompressor>(sp =>
        {
            var embedder = sp.GetService<LTAI.AI.EmbeddingClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Context.SemanticCompressor>();
            return new Context.SemanticCompressor(embedder, logger);
        });

        // Predictive offload tracker (historical tool result size patterns)
        services.AddSingleton<Memory.PredictiveOffloadTracker>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Memory.PredictiveOffloadTracker>();
            return new Memory.PredictiveOffloadTracker(logger);
        });

        // Refs search index (FTS5 over .livingtree/refs/)
        services.AddSingleton<Memory.RefsSearchIndex>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LTAI.Core.Configuration.LTAIOptions>>().Value;
            var refsDir = Path.Combine(AppContext.BaseDirectory, ".livingtree", "refs");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Memory.RefsSearchIndex>();
            var idx = new Memory.RefsSearchIndex(opts.ResolveDataPath("refs_search.db"), refsDir, logger);
            // Index existing refs in background (fire-and-forget with error handling)
            Task.Run(async () =>
            {
                try { await idx.IndexDirectoryAsync().ConfigureAwait(false); }
                catch (Exception ex) { logger.LogWarning(ex, "RefsSearchIndex background indexing failed"); }
            });
            return idx;
        });

        // Tools for refs interaction (expand ref, search refs)
        services.AddSingleton<Tools.ExpandRefTool>(sp =>
        {
            var offloader = sp.GetService<Memory.ContextOffloader>();
            return new Tools.ExpandRefTool(offloader);
        });
        services.AddSingleton<Tools.RefsSearchTool>(sp =>
        {
            var index = sp.GetService<Memory.RefsSearchIndex>();
            return new Tools.RefsSearchTool(index);
        });

        // DeerFlow-inspired: Container sandbox provider
        services.AddSingleton<Tools.ContainerSandboxProvider>(sp =>
        {
            var ws = Directory.GetCurrentDirectory();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Tools.ContainerSandboxProvider>();
            var mode = LTAI.Core.Configuration.EnvironmentConfig.SandboxMode;
            return new Tools.ContainerSandboxProvider(ws, mode, logger);
        });

        // DeerFlow-inspired: IM Channel tool (Telegram/Slack/Feishu/DingTalk/WeChat)
        services.AddSingleton<Tools.ImChannelTool>(sp =>
        {
            var httpFactory = sp.GetService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Tools.ImChannelTool>();
            return new Tools.ImChannelTool(httpFactory, logger);
        });

        // Refs garbage collector — TTL-based cleanup of .livingtree/refs/
        services.AddSingleton<Memory.RefsGarbageCollector>(sp =>
        {
            var refsDir = Path.Combine(AppContext.BaseDirectory, ".livingtree", "refs");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Memory.RefsGarbageCollector>();
            var cfg = new Memory.CompactionConfig(); // default config
            return new Memory.RefsGarbageCollector(refsDir, logger,
                cleanupIntervalMinutes: cfg.Gc.CleanupIntervalMinutes,
                ttlHours: cfg.Gc.TtlHours,
                maxFiles: cfg.Gc.MaxFiles);
        });
        // Run GC on startup + background interval
        services.AddHostedService(sp =>
        {
            var gc = sp.GetRequiredService<Memory.RefsGarbageCollector>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("RefsGcHostedService");
            return new DelegatingHostedService("RefsGarbageCollector",
                async ct =>
                {
                    await gc.CleanupAsync(ttlHours: 24, maxFiles: 10000).ConfigureAwait(false);
                    logger.LogInformation("RefsGarbageCollector: initial cleanup complete");
                },
                logger);
        });

        return services;
    }
}
