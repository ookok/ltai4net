using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Agent.Caching;
using LTAI.Agent.Context;
using LTAI.Agent.Diagnostics;
using LTAI.Agent.Learning;
using LTAI.Agent.Memory;
using LTAI.Agent.Services;
using LTAI.Agent.Memory;
using LTAI.Agent.Tools;
using LTAI.Agent.Experts;
using LTAI.Agent.Experts.Adapters;
using LTAI.Agent.Experts.Routing;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.Agent.Orchestration;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Agents.AI.Workflows.Declarative.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

/// <summary>
/// Top-level DI registration for the LTAI Agent subsystem.
///
/// Layered registration order (do not change — consumers depend on it):
///   1. AIAgent keyed services  (via <see cref="AgentDefinitionLoader"/>)
///   2. Durable Agent pipeline  (MAF DurableTask gRPC sidecar)
///   3. Knowledge / Code graph  (KgStore, Reranker, KbGraph, CgGraph)
///   4. Workflow orchestration  (AgentWorkflows, DecisionTreeRouter, hot reload)
///   5. DevUI shared service    (LTAIDevUIService)
///   6. AI policy & steer model (BudgetTracker, IChatClient "steer")
///   7. Background workers      (BackgroundJobService, McpClientFactory, TaskQueue)
///   8. Indexing & knowledge    (DocumentIndexer, KnowledgeExtractor, CodeChunkIndex)
///   9. Tools                   (KnowledgeAssetTool, TaskQueueTool, QuestionTool, ClusterSummarizer, DeepenSearchTool, FailureMiner)
///  10. User state              (SnippetStore)
///  11. Skill evolution         (SkillEvolutionEngine)
///  12. ChatAgent (L1→L2 router)
///
/// Tool selection, prompt building, and context-provider assembly live in
/// <see cref="AgentBuilder"/>, <see cref="AgentPromptBuilder"/>, and
/// <see cref="AgentContextProviderBuilder"/> respectively.
/// </summary>
public static class ServiceCollectionExtensions
{
#pragma warning disable MAAI001

    public static IServiceCollection AddLTAIAgent(this IServiceCollection services)
        => AddLTAIAgent(services, out _);

    /// <summary>
    /// Variant that also returns the list of registered agent names so callers (e.g. LTAI.Web)
    /// can wire up protocol endpoints (A2A / AGUI / OpenAI) without having to resolve every
    /// agent eagerly just to discover their names.
    /// </summary>
    public static IServiceCollection AddLTAIAgent(this IServiceCollection services, out IReadOnlyList<string> registeredAgentNames)
    {
        var names = new List<string>();

        // P2: Central tool registry — MAF-aligned per-agent AITool discovery.
        // Tools are registered during BuildAgentImpl and can be queried via
        // AgentToolStore.GetTools(agentName). This mirrors MAF's keyed AITool DI
        // pattern without requiring IServiceCollection mutations after DI build.
        services.AddSingleton<AgentToolStore>();

        // Step 1: Register each agent via MAF AddAIAgent (keyed services).
        // AgentBuilder.BuildAgentImpl still owns the 80+ tool selection, AIContextProviders,
        // decorators and Plan Mode handling — only the DI shape changes.
        // P0: per-agent isolation — if one agent fails to build, the others still work.
        foreach (var def in AgentDefinitionLoader.GetAgentDefinitions())
        {
            var captured = def;
            services.AddAIAgent(captured.Name, (sp, name) =>
            {
                var agent = captured.Build(sp, name);
                if (agent == null)
                {
                    // Fallback: return a minimal no-op agent so DI doesn't crash
                    return new FallbackAgent(captured.Name, captured.Description);
                }
                return agent;
            }, ServiceLifetime.Singleton);
            names.Add(captured.Name);
        }
        registeredAgentNames = names;

        // Step 1b: P8 — MAF Durable Agent pipeline (self-host gRPC sidecar).
        // Wraps each AIAgent as a DurableAIAgentProxy so chat history + tool-call
        // state survive process restarts. The sidecar is in-process via
        // Microsoft.DurableTask.InProcessTestHost 0.2.3-preview.1 (preview but
        // production-grade; backed by an in-memory IOrchestrationService).
        services.AddLTAIDurableAgents();

        // Step 2: SQLite Knowledge Graph store
        // Two independent stores: KbGraph (kg.db) and CgGraph (cg.db) — no write lock contention
        services.AddSingleton<KgStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new KgStore(opts.ResolveDataPath("kg.db"));
        });
        services.AddKeyedSingleton<KgStore>("cg", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new KgStore(opts.ResolveDataPath("cg.db"));
        });

        // Step 2b: Reranker (two-stage embedding + LLM rescore)
        services.AddSingleton<Reranker>(sp =>
        {
            var embedder = sp.GetRequiredService<LTAI.AI.EmbeddingClient>();
            var llm = sp.GetRequiredService<IChatClient>();
            var store = sp.GetService<KgStore>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Reranker>();
            return new Reranker(embedder, llm, store, logger);
        });

        // Step 2c: Knowledge/Code graph providers (registered in DI so lifecycle is managed)
        // Both support FTS5-only mode (no LLM, no embedding) and enhanced mode with LLM rewriting + reranking.
        services.AddSingleton<KbGraph>(sp =>
        {
            var store = sp.GetRequiredService<KgStore>();
            var llm = sp.GetService<IChatClient>();
            var reranker = sp.GetService<Reranker>();
            var embedder = sp.GetService<LTAI.AI.EmbeddingClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<KbGraph>();
            return new KbGraph(store, llm, reranker, embedder, logger);
        });
        services.AddSingleton<CgGraph>(sp =>
        {
            var store = sp.GetRequiredKeyedService<KgStore>("cg");
            var llm = sp.GetService<IChatClient>();
            var embedder = sp.GetService<LTAI.AI.EmbeddingClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<CgGraph>();
            return new CgGraph(store, llm, embedder, logger, Directory.GetCurrentDirectory());
        });

        // Step 2d: MoE Expert layer — wraps KG, code graph, documents, tools, and skills
        // as IExpertModule so the ExpertRouter can treat them uniformly for
        // sparse activation (top-K selection + parallel query + aggregation).

        // P16.1: Request-scoped query→embedding cache eliminates duplicate ONNX calls
        // within a single turn (ExpertRegistry + ToolFilteringChatClient both embed
        // the same query text — this cache makes the second call instant).
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
            new ToolExpert(sp.GetRequiredService<LTAI.AI.EmbeddingClient>()));
        services.AddSingleton<IExpertModule, SkillExpert>(sp =>
        {
            var skillsDir = new[] {
                Path.Combine(AppContext.BaseDirectory, "skills"),
                Path.Combine(Directory.GetCurrentDirectory(), "skills"),
            }.FirstOrDefault(Directory.Exists) ?? Path.Combine(Directory.GetCurrentDirectory(), "skills");
            Directory.CreateDirectory(skillsDir);
            return new SkillExpert(skillsDir);
        });
        services.AddSingleton<ExpertRegistry>(sp =>
        {
            var experts = sp.GetRequiredService<IEnumerable<IExpertModule>>();
            var embedder = sp.GetRequiredService<LTAI.AI.EmbeddingClient>();
            var cache = sp.GetService<ToolEmbeddingCache>();
            var queryCache = sp.GetService<QueryEmbeddingCache>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExpertRegistry>();
            return new ExpertRegistry(experts, embedder, cache, queryCache, logger);
        });

        // Step 2e: MoE routing pipeline (Router → FanOut → Aggregator)
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
            var embedder = sp.GetService<LTAI.AI.EmbeddingClient>();
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

        // Step 3: Workflow orchestrator (with P7.7 decision-tree routing)
        // P12.1: pass ToolEmbeddingCache so the 10-agent description embeddings
        // are batched + persisted; cold-start 0 ONNX calls after first run.
        // P15: pass YAMLWorkflowRegistry so thresholds/candidates are hot-editable.
        services.AddSingleton<RetryChainEmbedder>();
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
            var routerAgent = all.TryGetValue("LTAI-Router", out var ra) ? ra
                : throw new InvalidOperationException("LTAI-Router agent not registered");
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

        // MoA (Mixture of Agents) — registered as a keyed singleton for optional use
        // as an alternative L2 backend. Enable via LTAI:AI:L2:MoA=true in appsettings.json.
        services.AddKeyedSingleton<MoAWorkflow>("moa", (sp, _) =>
        {
            var l2Client = sp.GetKeyedService<IChatClient>("l2");
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value.AI;
            var proposerCount = Math.Max(1, opts.MoaProposerCount);
            var aggregatorCount = Math.Max(1, opts.MoaAggregatorCount);
            var proposers = Enumerable.Repeat(l2Client, proposerCount).Where(c => c != null).Cast<IChatClient>().ToList();
            var aggregators = Enumerable.Repeat(l2Client, aggregatorCount).Where(c => c != null).Cast<IChatClient>().ToList();
            return new MoAWorkflow(proposers, aggregators, sp.GetRequiredService<ILogger<MoAWorkflow>>(),
                opts.MoaTimeoutSeconds > 0 ? TimeSpan.FromSeconds(opts.MoaTimeoutSeconds) : null);
        });

        // Step 3c: P15 hot-editable workflow registry + watcher + notifier.
        // The registry scans `.livingtree/workflows/*.yaml|*.json` at startup
        // and listens for file changes via FileSystemWatcher (debounced 250ms).
        // The watcher is registered as a hosted service so it starts/stops
        // with the host.
        // P14.7: DefaultMcpToolHandler is a singleton that owns the McpClient
        // cache and (per-server) HttpClient cache. Sharing it across workflows
        // avoids duplicating connections to the same MCP server.
        services.AddSingleton<IMcpToolHandler, DefaultMcpToolHandler>();
        services.AddSingleton<WorkflowHotReloadNotifier>();
        services.AddSingleton<YAMLWorkflowRegistry>(sp => new YAMLWorkflowRegistry(
            sp.GetRequiredService<IOptions<LTAIOptions>>(), // options
            sp.GetRequiredService<ILogger<YAMLWorkflowRegistry>>(), // logger
            sp.GetRequiredService<WorkflowHotReloadNotifier>(), // notifier
            sp.GetService<IMcpToolHandler>()));
        services.AddSingleton<YAMLWorkflowWatcher>(sp => new YAMLWorkflowWatcher(
            sp.GetRequiredService<YAMLWorkflowRegistry>().WatchDirectory,
            sp.GetRequiredService<YAMLWorkflowRegistry>(),
            sp.GetRequiredService<ILogger<YAMLWorkflowWatcher>>()));
        // P14.4: HPO auto-tuning background service
        services.AddHostedService<AutoTunerService>();
        services.AddHostedService<WorkflowWatcherHostedService>();
        services.AddHostedService<LTAI.Agent.Services.GraphInitService>();
        services.AddHostedService<LTAI.Agent.Services.WarmupService>();

        // Step 3a: DevUI shared service (P9.0)
        // Used by LTAI.Web (DevUI REST surface), LTAI.TUI (/dashboard),
        // LTAI.Desktop (WebView2 inspector). Backed by keyed AIAgents registered
        // in Step 1 and AgentRegistry metadata for card construction.
        services.AddSingleton<LTAI.Agent.DevUI.LTAIDevUIService>();

        // Step 3b: Token budget tracker (from AI config, optional)
        services.AddSingleton<LTAI.AI.BudgetTracker>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new LTAI.AI.BudgetTracker(
                globalMax: opts.AI.GlobalTokenBudget,
                perUserMax: opts.AI.PerUserTokenBudget);
        });

        // Step 3b-steer: lightweight meta-decision model (free/low-cost).
        // Used for: response quality judging, safety pre-checks, ambiguous routing,
        // and summary verification. Saves 15-25% token cost vs main LLM.
        // Registered as a keyed IChatClient ("steer") to avoid conflicting with the
        // main LLM IChatClient registration. Consumers resolve via
        // sp.GetKeyedService<IChatClient>("steer").
        // When disabled or missing key, no service is registered (null-safe).
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

            var steerKey = LTAI.Core.Configuration.SecretManager.Get(steer.ApiKeyEnv);
            if (string.IsNullOrEmpty(steerKey))
            {
                var log = sp.GetService<ILoggerFactory>()?.CreateLogger("LTAI.Steer");
                log?.LogWarning("Steer model enabled but {EnvVar} is not set — disabling", steer.ApiKeyEnv);
                return null!;
            }

            return OpenAIChatClientFactory.Create(steer.Endpoint, steer.Model, steerKey);
        });

        // Step 3c: Background job service
        services.AddSingleton<BackgroundJobService>();

        // Step 3c-mcp: MCP client factory (lazy connect to external MCP servers).
        // Connects on first GetToolsAsync call, then caches the tool list.
        services.AddSingleton<LTAI.Agent.Mcp.McpClientFactory>();

        // Step 3c-queue: in-process task queue (Channel<T>-based producer/consumer).
        // Lightweight substitute for MAF DurableTask; persists state in memory
        // and exposes EnqueueAsync / List / WaitAsync for deferred work.
        // Step 3c-queue: in-process task queue - shares kg.db via SQLiteTaskStore.CreateShared
        services.AddSingleton<LTAI.Agent.Tasks.TaskQueue>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new LTAI.Agent.Tasks.TaskQueue(
                LTAI.Agent.Tasks.SQLiteTaskStore.CreateShared(opts.ResolveDataPath("kg.db")));
        });

        // Step 3d: Knowledge indexing pipeline (semantic chunking + unified ingest)
        services.AddSingleton<LTAI.Agent.Indexing.DocumentPageAnnotator>(sp =>
        {
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Indexing.DocumentPageAnnotator>();
            return new LTAI.Agent.Indexing.DocumentPageAnnotator(l3, logger);
        });
        services.AddSingleton<LTAI.Agent.Indexing.DocumentIndexer>();
        services.AddSingleton<LTAI.Agent.Indexing.IndexQueueWorker>(sp =>
        {
            var indexer = sp.GetRequiredService<LTAI.Agent.Indexing.DocumentIndexer>();
            var queue = sp.GetRequiredService<LTAI.Agent.Tasks.TaskQueue>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<LTAI.Agent.Indexing.IndexQueueWorker>();
            return new LTAI.Agent.Indexing.IndexQueueWorker(indexer, queue, logger!);
        });
        services.AddSingleton<LTAI.Agent.Indexing.RetryQueueWorker>(sp =>
        {
            var client = sp.GetRequiredService<LTAI.AI.MultiProviderChatClient>();
            var queue = sp.GetRequiredService<LTAI.Agent.Tasks.TaskQueue>();
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<LTAI.Agent.Indexing.RetryQueueWorker>();
            return new LTAI.Agent.Indexing.RetryQueueWorker(client, queue, logger!);
        });
        services.AddSingleton<LTAI.Agent.Indexing.KnowledgeExtractor>(sp =>
        {
            var kg = sp.GetRequiredService<KgStore>();
            var llm = sp.GetRequiredService<IChatClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Indexing.KnowledgeExtractor>();
            return new LTAI.Agent.Indexing.KnowledgeExtractor(kg, llm, logger);
        });
        services.AddSingleton<LTAI.Agent.Indexing.KnowledgeQualityScorer>();
        services.AddSingleton<LTAI.Agent.Indexing.ProvenanceTracker>();
        services.AddSingleton<LTAI.Agent.Indexing.ProvenanceProvider>();

        // Step 3e: Knowledge assetization tool (WikiCommit, WikiSearch, etc.)
        services.AddSingleton<LTAI.Agent.Tools.KnowledgeAssetTool>();

        // P14.13: TaskQueueTool — LLM-callable wrapper that exposes the queue
        // as 5 AITool methods (Enqueue/List/Get/Wait/Cancel). Owns a name->handler
        // registry so Enqueue dispatch works across the JSON tool boundary.
        services.AddSingleton<LTAI.Agent.Tools.TaskQueueTool>(sp =>
            new LTAI.Agent.Tools.TaskQueueTool(
                sp.GetRequiredService<LTAI.Agent.Tasks.TaskQueue>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<LTAI.Agent.Tools.TaskQueueTool>()));

        // Headroom: CompressionStore — SQLite-backed reversible compression store
        // for CCR (Consistent Compression with Retrieval).
        // CompressionStore — now shares kg.db via CreateShared
        services.AddSingleton<CompressionStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return CompressionStore.CreateShared(opts.ResolveDataPath("kg.db"));
        });

        // Headroom: RetrieveContentTool — LLM-callable tool to retrieve original
        // content from the compression store by ID.
        services.AddSingleton<RetrieveContentTool>();

        // Code chunk index: AST-aware semantic code search (cocoindex-inspired).
        // Shares the same KgStore instance for vector storage.
        services.AddSingleton<LTAI.Agent.Indexing.CodeChunkIndex>(sp =>
        {
            var store = sp.GetRequiredService<KgStore>();
            var parser = new LTAI.Agent.CodeAnalysis.TreeSitterParser(
                sp.GetService<ILogger<LTAI.Agent.CodeAnalysis.TreeSitterParser>>());
            return new LTAI.Agent.Indexing.CodeChunkIndex(store, parser,
                sp.GetService<LTAI.AI.EmbeddingClient>(),
                sp.GetService<ILogger<LTAI.Agent.Indexing.CodeChunkIndex>>(),
                Directory.GetCurrentDirectory());
        });

        // Headroom: FailureMiner — offline analysis of failure records to
        // auto-generate AGENTS.md rules.
        services.AddSingleton<FailureMiner>();

        // Step 3c-snippets: User-defined common-phrase store. Shared between
        // LTAI.TUI and LTAI.Desktop via .livingtree/snippets.json. Supports
        // /snippet save|use|delete|rename|edit|list — see SnippetCommandParser.
        services.AddSingleton<LTAI.Agent.Snippets.SnippetStore>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LTAIOptions>>().Value;
            return new LTAI.Agent.Snippets.SnippetStore(
                opts.ResolveDataPath("snippets.json"),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Snippets.SnippetStore>());
        });

        // P17.5: QuestionService — bridges LLM tool calls and UI for structured
        // follow-up questions. The tool posts questions, the UI renders them
        // (Spectre.Console / Avalonia), and answers flow back via Reply/Reject.
        services.AddSingleton<LTAI.Agent.Tools.QuestionService>();
        services.AddSingleton<LTAI.Agent.Tools.QuestionTool>();

        // P? ClusterSummarizer — LLM-powered clustering + summarization for
        // knowledge retrieval results. The LLM organizes many result items into
        // topical groups with a short summary per group.
        services.AddSingleton<LTAI.Agent.Tools.ClusterSummarizer>(sp =>
            new LTAI.Agent.Tools.ClusterSummarizer(
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<LTAI.Agent.Tools.ClusterSummarizer>()));

        // DeepenSearchTool — DRIFT-inspired iterative deepen KG search.
        // Searches multiple rounds, identifies gaps via LLM, generates follow-ups.
        services.AddSingleton<LTAI.Agent.Tools.DeepenSearchTool>(sp =>
            new LTAI.Agent.Tools.DeepenSearchTool(
                sp.GetRequiredService<KbGraph>(),
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<LTAI.Agent.Tools.DeepenSearchTool>()));

        // SeedER — Structural Entity Discovery & Exploratory Retrieval.
        // Replaces flat similarity matching with structured path exploration
        // and multi-hop reasoning across the knowledge graph.
        services.AddSingleton<LTAI.Agent.SeedER.PathExplorer>(sp =>
            new LTAI.Agent.SeedER.PathExplorer(
                sp.GetRequiredService<KgStore>()));
        services.AddSingleton<LTAI.Agent.SeedER.SeedER>(sp =>
            new LTAI.Agent.SeedER.SeedER(
                sp.GetRequiredService<KgStore>(),
                sp.GetRequiredService<LTAI.Agent.SeedER.PathExplorer>(),
                sp.GetService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<LTAI.Agent.SeedER.SeedER>()));
        services.AddSingleton<LTAI.Agent.Tools.SeedERTool>(sp =>
            new LTAI.Agent.Tools.SeedERTool(
                sp.GetRequiredService<LTAI.Agent.SeedER.SeedER>(),
                sp.GetRequiredService<IChatClient>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<LTAI.Agent.Tools.SeedERTool>()));

        // P14.8: bridge LocalEmbedder.ModelSwitched → static registry cache
        // invalidation (LTAI.AI's ToolEmbeddingCache handles itself; this
        // handles AgentRegistry + ToolRegistry which live in LTAI.Agent).
        services.AddSingleton<LocalEmbedderModelSwitchNotifier>(sp =>
            new LocalEmbedderModelSwitchNotifier(sp.GetService<LTAI.AI.LocalEmbedder>()));

        // Step 3c-bis: Skill Evolution Engine (L1-L3)
        services.AddSingleton<SkillEvolutionEngine>(sp =>
        {
            var llm = sp.GetRequiredService<IChatClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<SkillEvolutionEngine>();
            var skillsDir = new[] {
                Path.Combine(AppContext.BaseDirectory, "skills"),
                Path.Combine(Directory.GetCurrentDirectory(), "skills"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "skills"),
            }.FirstOrDefault(Directory.Exists) ?? Path.Combine(Directory.GetCurrentDirectory(), "skills");
            return new SkillEvolutionEngine(llm, logger, skillsDir);
        });

        // Step 3d: ChatAgent + workflow (default L1=flash, auto-upgrade to L2=pro)
        services.AddSingleton<ChatAgent>(sp =>
        {
            var all = sp.GetKeyedServices<AIAgent>(KeyedService.AnyKey)
                .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
            var wf = sp.GetRequiredService<AgentWorkflows>();
            var chat = all["LTAI-Chat"];
            // P16: MoE Expert routing layer — wraps LTAI-Chat for knowledge-intensive queries.
            // Only activates when IsKnowledgeQuery returns true; casual chat passes through.
            var expertRouter = sp.GetRequiredService<ExpertRouter>();
            var expertFanOut = sp.GetRequiredService<ParallelFanOutExecutor>();
            var expertAggregator = sp.GetRequiredService<ExpertAggregator>();
            var expertRegistry = sp.GetRequiredService<ExpertRegistry>();
            chat = new ExpertRouterAgent(chat, expertRouter, expertFanOut, expertAggregator, expertRegistry,
                sp.GetService<ExpertFeedbackLogger>());
            // Pro agent for complex task auto-upgrade (uses l2 layer)
            var proAgent = all.TryGetValue("LTAI-Chat-Pro", out var p) ? p : chat;
            var budget = sp.GetService<LTAI.AI.BudgetTracker>();

            // Check if L1 and L2 use the same provider+model — skip upgrade (from appsettings.json)
            var l1Cfg = sp.GetRequiredService<IOptions<LTAIOptions>>().Value.AI.L1;
            var l2Cfg = sp.GetRequiredService<IOptions<LTAIOptions>>().Value.AI.L2;
            bool sameModel = l1Cfg != null && l2Cfg != null
                && string.Equals(l1Cfg.Provider, l2Cfg.Provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(l1Cfg.Model, l2Cfg.Model, StringComparison.OrdinalIgnoreCase);

            return new ChatAgent(chat, proAgent, wf, budget,
                localEmbedder: sp.GetService<LTAI.AI.LocalEmbedder>(),
                httpFactory: sp.GetService<IHttpClientFactory>(),
                sameModel: sameModel,
                steerJudge: sp.GetKeyedService<IChatClient>("steer"),
                escalationDecider: sp.GetService<IEscalationDecider>(),
                tsParser: sp.GetService<LTAI.Agent.CodeAnalysis.TreeSitterParser>(),
                lspManager: sp.GetService<LTAI.Agent.LanguageServer.LspLanguageManager>(),
                checkpointStore: sp.GetService<IMemoryCachingStore>(),
                escalationConfig: sp.GetRequiredService<IOptions<LTAIOptions>>().Value.Escalation);
        });

        // Step 7: PalaceStore (structured long-term memory) + consolidation service
        services.AddSingleton<PalaceStore>(sp =>
        {
            var embedder = sp.GetRequiredService<LTAI.AI.EmbeddingClient>();
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<PalaceStore>();
            return PalaceStore.CreateShared(embedder, opts.ResolveDataPath("kg.db"), logger);
        });
        services.AddHostedService<MemoryConsolidationService>(sp =>
        {
            var store = sp.GetRequiredService<PalaceStore>();
            var l3 = sp.GetKeyedService<IChatClient>("l3");
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<MemoryConsolidationService>();
            return new MemoryConsolidationService(store, l3, logger);
        });

        // Step 8: Conversation state checkpoint cache (Memory → File → Null)
        services.AddSingleton<IMemoryCachingStore>(sp =>
            new CachingCascade());

        return services;
    }

    /// <summary>
    /// Register the multi-graph memory system (MAGMA-inspired).
    /// Adds MultiGraphStore, AdaptiveBeamTraverser, IntentRouter,
    /// SalienceBudgetCompressor, and the slow-path consolidation worker.
    /// </summary>
    public static IServiceCollection AddLTAIMemory(this IServiceCollection services,
        Action<MultiGraphConfig>? configure = null)
    {
        var config = new MultiGraphConfig();
        configure?.Invoke(config);

        services.AddSingleton<MultiGraphStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            var dbPath = opts.ResolveDataPath("kg.db");
            return new MultiGraphStore(dbPath, config.CausalThreshold, config.SemanticThreshold);
        });

        services.AddSingleton<AdaptiveBeamTraverser>();
        services.AddSingleton<IntentRouter>();
        services.AddSingleton<QueryClassifier>();
        services.AddSingleton<SalienceBudgetCompressor>();

        return services;
    }
}

public sealed class MultiGraphConfig
{
    public double CausalThreshold { get; set; } = 0.7;
    public double SemanticThreshold { get; set; } = 0.6;
    public TimeSpan ConsolidationInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int ConsolidationBatchSize { get; set; } = 50;
}
