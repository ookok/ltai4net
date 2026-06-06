using System.Text.Json;
using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Core.Safety;
using LTAI.Agent.Context;
using LTAI.Agent.Diagnostics;
using LTAI.Agent.Learning;
using LTAI.Agent.Services;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.Agent.Memory;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Agents.AI.Workflows.Declarative.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

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

        // Step 1: Register each agent via MAF AddAIAgent (keyed services).
        // BuildAgentImpl still owns the 80+ tool selection, AIContextProviders,
        // decorators and Plan Mode handling — only the DI shape changes.
        // P0: per-agent isolation — if one agent fails to build, the others still work.
        foreach (var def in GetAgentDefinitions())
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
            return new AgentWorkflows(all.Values, all["LTAI-Router"],
                sp.GetRequiredService<ILogger<AgentWorkflows>>(),
                sp.GetRequiredService<DecisionTreeRouter>(),
                workflowRegistry: sp.GetService<YAMLWorkflowRegistry>(),
                diagnosticsStore: sp.GetService<RoutingDiagnosticsStore>());
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
        services.AddSingleton<LTAI.Agent.Tasks.TaskQueue>();

        // Step 3d: Knowledge indexing pipeline (semantic chunking + unified ingest)
        services.AddSingleton<LTAI.Agent.Indexing.DocumentIndexer>();
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
        services.AddSingleton<CompressionStore>();

        // Headroom: RetrieveContentTool — LLM-callable tool to retrieve original
        // content from the compression store by ID.
        services.AddSingleton<RetrieveContentTool>();

        // Code chunk index: AST-aware semantic code search (cocoindex-inspired).
        // Shares the same KgStore instance for vector storage.
        services.AddSingleton<LTAI.Agent.Indexing.CodeChunkIndex>(sp =>
        {
            var store = sp.GetRequiredService<KgStore>();
            var parser = new LTAI.Agent.Tools.TreeSitterParser(
                sp.GetService<ILogger<LTAI.Agent.Tools.TreeSitterParser>>());
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
                steerJudge: sp.GetKeyedService<IChatClient>("steer"));
        });

        return services;
    }

    /// <summary>
    /// Flat record describing one agent definition. Replaces the inline
    /// Dictionary&lt;string, AIAgent&gt; building so each agent can be registered
    /// as a MAF keyed service via <c>AddAIAgent</c>.
    /// </summary>
    private sealed record AgentDef(
        string Name,
        string Description,
        bool CanRead,
        bool CanWrite,
        bool CanList,
        bool CanExec,
        string? ModelId,
        float? Temperature,
        float? TopP,
        string? Prompt = null)
    {
        public AIAgent? Build(IServiceProvider sp, string name)
        {
            try
            {
                return Task.Run(() =>
                    BuildAgentImpl(sp, name, Description, CanRead, CanWrite, CanList, CanExec,
                        modelId: ModelId, temperature: Temperature, topP: TopP,
                        agentPrompt: Prompt)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                    ?.CreateLogger("LTAI.Agent.BuildAgent");
                logger?.LogError(ex, "Agent '{Name}' failed to build — skipping DI registration", name);
                return null;
            }
        }
    }

    private static IEnumerable<AgentDef> GetAgentDefinitions()
    {
        // Try loading from agents/*.agent.md files first
        var fileDefs = AgentRegistry.LoadAll();
        if (fileDefs.Count > 0)
        {
            foreach (var def in fileDefs)
            {
                var key = def.Name?.ToLowerInvariant().Replace("ltai-", "") ?? "unknown";
                yield return new AgentDef(
                    Name: def.Name ?? key,
                    Description: def.Description,
                    CanRead: def.Permissions.Contains("read"),
                    CanWrite: def.Permissions.Contains("write"),
                    CanList: def.Permissions.Contains("list"),
                    CanExec: def.Permissions.Contains("exec"),
                    ModelId: def.ModelId,
                    Temperature: (float?)def.Temperature,
                    TopP: (float?)def.TopP,
                    Prompt: def.Prompt);
            }
            // Internal router agent (not from files) — used by AgentWorkflows for handoff routing
            yield return new("LTAI-Router", "任务调度器(无工具)", false, false, false, false, null, 0.3f, 0.95f, Prompt: null);
            yield break;
        }

        // Fallback: hardcoded defaults (no agents/*.agent.md files found)
        // 任务类型 → temperature/topP 参考：AI编程 0.3/0.95 | 工具调用 0.3/0.95 | 通用问答 0.8/0.95 | 数学推理 1.0/0.95
        yield return new("LTAI-Router",   "任务调度器(无工具)",      false, false, false, false, null, 0.3f, 0.95f);
        yield return new("LTAI-Chat",     "通用对话助手",          true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Chat-Pro", "深度推理助手(Pro)",      true,  true,  true,  true,  "l2", 0.3f, 0.95f);
        yield return new("LTAI-Code",     "代码分析助手",          true,  true,  true,  false, null, 0.3f, 0.95f);
        yield return new("LTAI-Math",     "数学计算助手",          false, false, false, true,  null, 1.0f, 0.95f);
        yield return new("LTAI-Data",     "数据处理助手",          true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-System",   "系统管理助手",          false, false, false, true,  null, 0.3f, 0.95f);
        yield return new("LTAI-LLM",      "纯对话助手",            false, false, false, false, null, 0.8f, 0.95f);
        yield return new("LTAI-Writer",   "创意写作助手",          true,  true,  true,  true,  null, 0.8f, 0.95f);
        yield return new("LTAI-Frontend", "前端网页开发助手",       true,  true,  true,  true,  null, 0.8f, 0.95f);
        yield return new("LTAI-Plan",     "架构规划师(只读)",       true,  false, true,  false, null, 0.5f, 0.95f);
    }

    /// <summary>
    /// ─────────────────────────────────────────────────────
    ///  TOOL REGISTRATION MATRIX (agent × tool category)
    /// ─────────────────────────────────────────────────────
    ///                     Chat  Code  Math  Data  System  LLM  Writer  Frontend
    ///  Filesystem R/W       ✅    ✅    —     ✅     —      —     ✅      ✅
    ///  Shell/Exec           ✅    —     ✅    ✅     ✅     —     ✅      ✅
    ///  Search/Symbols       ✅    ✅    —     —     —      —     ✅      ✅
    ///  EIA                  ✅    —     —     ✅     ✅     —
    ///  Web                  ✅    —     —     ✅     —      —     ✅      ✅
    ///  Multimedia           ✅    ✅    —     ✅     ✅     —     ✅      ✅
    ///  Office               ✅    ✅    —     ✅     —      —
    ///  Memory               ✅    —     —     —     ✅     —     ✅      —
    ///  Git                  ✅    ✅    —     —     ✅     —     ✅      ✅
    ///  Plan/Flowchart       ✅    ✅    —     ✅     —      —     ✅      ✅
    ///  GIS/Weather/Trans    ✅    —     —     ✅     ✅     —     ✅      ✅
    ///  System/Network       ✅    —     —     —     ✅     —     ✅      —
    ///  Subagent             ✅    —     —     —     —      —     ✅      ✅
    ///  Task/Jobs            ✅    ✅    —     —     ✅     —     ✅      ✅
    ///  Container            ✅    —     ✅    ✅     ✅     —     —       ✅
    /// ─────────────────────────────────────────────────────
    ///  Permission flags: canRead, canWrite, canList, canExec
    ///  Add new tools by inserting a new section below.
    /// ─────────────────────────────────────────────────────
    /// </summary>
    private static AIAgent BuildAgent(IServiceProvider sp, string name, string description,
        bool canRead, bool canWrite, bool canList, bool canExec,
        string? modelId = null, float? temperature = null, float? topP = null) {
        return Task.Run(() => BuildAgentImpl(sp, name, description, canRead, canWrite, canList, canExec, modelId, temperature, topP)).GetAwaiter().GetResult();
    }

    // Original implementation
    private static async Task<AIAgent> BuildAgentImpl(IServiceProvider sp, string name, string description,
        bool canRead, bool canWrite, bool canList, bool canExec,
        string? modelId = null, float? temperature = null, float? topP = null, string? agentPrompt = null)
    {
        var ws = Directory.GetCurrentDirectory();
        var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var llm = sp.GetRequiredService<IChatClient>();
        var log = loggerFactory.CreateLogger("Agent." + name);

        // P0.1: Wrap with progress guard to detect repeated tool calls
        var guardedLlm = new LTAI.Agent.Clients.ThinkingTagValidator(
            new LTAI.Agent.Clients.ProgressGuardChatClient(llm));

        var tools = new List<AITool>();
        var fs = new FileSystemTools(ws);
        var text = new TextTools(ws);

        // File operations (read/write/list/copy/move/delete/glob/tree)
        if (canRead) tools.Add(AIFunctionFactory.Create(
            (string path) => fs.ReadFileContent(path),
            "ReadFileContent", "Read a file"));
        if (canRead) tools.Add(AIFunctionFactory.Create(fs.ListTools));
        if (canWrite) tools.Add(AIFunctionFactory.Create(fs.WriteFile));
        if (canList)
        {
            tools.Add(AIFunctionFactory.Create(fs.ListFiles));
            tools.Add(AIFunctionFactory.Create(fs.Glob));
            tools.Add(AIFunctionFactory.Create(fs.DirectoryTree));
        }
        if (canRead && canWrite)
        {
            tools.Add(AIFunctionFactory.Create(fs.CopyFile));
            tools.Add(AIFunctionFactory.Create(fs.MoveFile));
            tools.Add(AIFunctionFactory.Create(fs.DeleteFile));
            tools.Add(AIFunctionFactory.Create(fs.DeleteDirectory));
            tools.Add(AIFunctionFactory.Create(fs.GetFileInfo));
        }
        if (canExec)
        {
            tools.Add(AIFunctionFactory.Create(new SafeShellTool(ws).RunCommand));
        }

        // Text editing (edit/multi-edit/regex/diff)
        if (canRead && canWrite)
        {
            tools.Add(AIFunctionFactory.Create(text.EditFile));
            tools.Add(AIFunctionFactory.Create(text.MultiEdit));
        }
        if (canRead)
        {
            tools.Add(AIFunctionFactory.Create(TextTools.RegexTest));
        }
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Review" or "LTAI-Writer")
        {
            tools.Add(AIFunctionFactory.Create(TextTools.DiffFiles));
        }

        // Search tools (grep-style)
        var search = new SearchTools(ws);
        if (canRead)
        {
            tools.Add(AIFunctionFactory.Create(search.SearchContent));
            tools.Add(AIFunctionFactory.Create(search.SearchFiles));
        }

        // Code analysis tools (Roslyn-based for C#, pattern-based for others)
        var codeAnalysis = new CodeAnalysisTools(ws);
        if (canRead && (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Frontend"))
        {
            tools.Add(AIFunctionFactory.Create(codeAnalysis.GetSymbols));
            tools.Add(AIFunctionFactory.Create(codeAnalysis.FindInCode));
        }

        // EIA (Environmental Impact Assessment) tools
        if (name is "LTAI-Chat" or "LTAI-Data" or "LTAI-System" or "LTAI-Writer" or "LTAI-Frontend")
        {
            // C1: EIA tools are in optional LTAI.Agent.Eia project (modularized).
            // Register them only when the package is referenced. To enable, add
            // ProjectReference to LTAI.Agent.Eia and uncomment the lines below.
            //   tools.Add(AIFunctionFactory.Create(EiaTools.ClassifyAirQuality));
            //   tools.Add(AIFunctionFactory.Create(EiaTools.ClassifyNoise));
            //   tools.Add(AIFunctionFactory.Create(EiaTools.ClassifyWaterQuality));
            //   tools.Add(AIFunctionFactory.Create(EiaTools.GaussianPlume));
            //   tools.Add(AIFunctionFactory.Create(EiaTools.CO2Equivalent));
            //   tools.Add(AIFunctionFactory.Create(EiaTools.HazardQuotient));
            //   tools.Add(AIFunctionFactory.Create(EiaTools.LookupStandard));
        }

        // Web tools (search, fetch, custom HTTP requests)
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var web = new WebTools(httpFactory, sp.GetService<ILogger<WebTools>>());
        if (name.StartsWith("LTAI-Chat") || name == "LTAI-Data")
        {
            tools.Add(AIFunctionFactory.Create(web.WebSearch));
            tools.Add(AIFunctionFactory.Create(web.WebFetch));
            tools.Add(AIFunctionFactory.Create(web.HttpRequest));
        }

        // Multimedia tools (SkiaSharp + FFmpeg)
        var media = new MultimediaTools(ws);
        if (canRead)
        {
            tools.Add(AIFunctionFactory.Create(media.ImageInfo));
            tools.Add(AIFunctionFactory.Create(media.ImageResize));
            tools.Add(AIFunctionFactory.Create(media.ImageConvert));
            tools.Add(AIFunctionFactory.Create(media.MediaInfo));
            tools.Add(AIFunctionFactory.Create(media.AudioConvert));
        }
        if (canExec)
            tools.Add(AIFunctionFactory.Create(media.Screenshot));

        // Document tools (Excel/Word/PPT/PDF + doc gen pipeline)
        var doc = new DocumentTools(ws, sp.GetService<KbGraph>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<DocumentTools>());
        if (canRead && canWrite)
        {
            tools.Add(AIFunctionFactory.Create(doc.ExcelRead));
            tools.Add(AIFunctionFactory.Create(doc.ExcelWrite));
            tools.Add(AIFunctionFactory.Create(doc.ExcelCopyRange));
            tools.Add(AIFunctionFactory.Create(doc.ExcelGetStyles));
            tools.Add(AIFunctionFactory.Create(doc.WordRead));
            tools.Add(AIFunctionFactory.Create(doc.WordWrite));
            tools.Add(AIFunctionFactory.Create(doc.WordCopyStyle));
            tools.Add(AIFunctionFactory.Create(doc.WordGetStyles));
            tools.Add(AIFunctionFactory.Create(doc.PptRead));
            tools.Add(AIFunctionFactory.Create(doc.PptWrite));
            tools.Add(AIFunctionFactory.Create(doc.PptGetStyles));
            tools.Add(AIFunctionFactory.Create(doc.PptCopyStyle));
            tools.Add(AIFunctionFactory.Create(doc.PdfRead));
            tools.Add(AIFunctionFactory.Create(doc.SaveTemplateAsync));
            tools.Add(AIFunctionFactory.Create(doc.LoadTemplateAsync));
            tools.Add(AIFunctionFactory.Create(doc.RenderTemplate));
            tools.Add(AIFunctionFactory.Create(doc.InferContentTypes));
            tools.Add(AIFunctionFactory.Create(doc.BuildDocumentAsync));
        }

        // Plan approval workflow tools
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Writer" or "LTAI-Frontend")
        {
            tools.Add(AIFunctionFactory.Create(PlanTools.SubmitPlan));
            tools.Add(AIFunctionFactory.Create(PlanTools.MarkStepComplete));
            tools.Add(AIFunctionFactory.Create(PlanTools.RevisePlan));
            tools.Add(AIFunctionFactory.Create(PlanTools.PlanStatus));
        }

        // Flowchart / diagram tools (Mermaid + SVG)
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Data" or "LTAI-Writer" or "LTAI-Frontend")
        {
            var diagram = new FlowchartTools(httpFactory);
            tools.Add(AIFunctionFactory.Create(diagram.Flowchart));
            tools.Add(AIFunctionFactory.Create(diagram.SequenceDiagram));
            tools.Add(AIFunctionFactory.Create(diagram.ClassDiagram));
            tools.Add(AIFunctionFactory.Create(diagram.GanttChart));
            tools.Add(AIFunctionFactory.Create(diagram.ErDiagram));
        }

        // Choice/selection tool
        if (name is "LTAI-Chat" or "LTAI-Writer" or "LTAI-Frontend")
        {
            tools.Add(AIFunctionFactory.Create(ChoiceTools.AskChoice));
        }

        // Subagent tools (explore, research, review, spawn_subagent)
        if (name is "LTAI-Chat" or "LTAI-Writer" or "LTAI-Frontend")
        {
            var sub = new SubagentTools(sp, llm, ws, tools);
            tools.Add(AIFunctionFactory.Create(sub.Explore));
            tools.Add(AIFunctionFactory.Create(sub.Research));
            tools.Add(AIFunctionFactory.Create(sub.Review));
            tools.Add(AIFunctionFactory.Create(sub.SecurityReview));
            tools.Add(AIFunctionFactory.Create(sub.SpawnSubagent));
        }

        // Agent generator tool (LLM-powered agent config generation)
        if (name is "LTAI-Chat" or "LTAI-Writer")
        {
            var gen = new AgentGenerator(llm);
            tools.Add(AIFunctionFactory.Create(gen.GenerateAgent));
        }

        // Git tools (LibGit2Sharp, no CLI)
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-System" or "LTAI-Writer" or "LTAI-Frontend")
        {
            var git = new GitTools(ws);
            tools.Add(AIFunctionFactory.Create(git.GitStatus));
            tools.Add(AIFunctionFactory.Create(git.GitLog));
            tools.Add(AIFunctionFactory.Create(git.GitAdd));
            tools.Add(AIFunctionFactory.Create(git.GitCommit));
            tools.Add(AIFunctionFactory.Create(git.GitUnstage));
            tools.Add(AIFunctionFactory.Create(git.GitCheckout));
            tools.Add(AIFunctionFactory.Create(git.GitBranch));

            tools.Add(AIFunctionFactory.Create(git.GitMerge));
            tools.Add(AIFunctionFactory.Create(git.GitRemote));
            tools.Add(AIFunctionFactory.Create(git.GitTag));
            tools.Add(AIFunctionFactory.Create(git.GitStash));
            tools.Add(AIFunctionFactory.Create(git.GitStashList));
            tools.Add(AIFunctionFactory.Create(git.GitDiff));
            tools.Add(AIFunctionFactory.Create(git.GitBlame));
            tools.Add(AIFunctionFactory.Create(git.GitShow));
            tools.Add(AIFunctionFactory.Create(git.GitRebase));
            tools.Add(AIFunctionFactory.Create(git.GitReviewChanges));
            tools.Add(AIFunctionFactory.Create(git.GitReset));
            tools.Add(AIFunctionFactory.Create(git.GitPush));
            tools.Add(AIFunctionFactory.Create(git.GitPull));
            tools.Add(AIFunctionFactory.Create(git.GitFetch));
            tools.Add(AIFunctionFactory.Create(git.GitCommitAndPush));
            tools.Add(AIFunctionFactory.Create(git.GitUndoLast));
            tools.Add(AIFunctionFactory.Create(git.GitCleanupBranches));
            tools.Add(AIFunctionFactory.Create(git.GitBranchDelete));
        }

        // Task management tools (todo list)
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-System" or "LTAI-Code" or "LTAI-Writer" or "LTAI-Frontend")
        {
            tools.Add(AIFunctionFactory.Create(TaskTools.TodoWrite));
            tools.Add(AIFunctionFactory.Create(TaskTools.TodoComplete));
            tools.Add(AIFunctionFactory.Create(TaskTools.TodoList));
        }

        // Integration tools (GIS, weather, translate, image)
        if (name is "LTAI-Chat" or "LTAI-Data" or "LTAI-System" or "LTAI-Writer" or "LTAI-Frontend")
        {
            var integ = new IntegrationTools(httpFactory);
            tools.Add(AIFunctionFactory.Create(integ.Geocode));
            tools.Add(AIFunctionFactory.Create(integ.ReverseGeocode));
            tools.Add(AIFunctionFactory.Create(integ.PoiSearch));
            tools.Add(AIFunctionFactory.Create(integ.DistanceCalc));
            tools.Add(AIFunctionFactory.Create(integ.IpLocation));
            tools.Add(AIFunctionFactory.Create(integ.Weather));
            tools.Add(AIFunctionFactory.Create(integ.Translate));
            tools.Add(AIFunctionFactory.Create(integ.ImageSearch));
        }

        // System & Network tools (diagnostics + background jobs + Docker containers)
        if (name is "LTAI-Chat" or "LTAI-Chat-Pro" or "LTAI-System" or "LTAI-Writer")
        {
            tools.Add(AIFunctionFactory.Create(SystemTools.GetCurrentDateTime));
            tools.Add(AIFunctionFactory.Create(SystemTools.SystemInfo));
            tools.Add(AIFunctionFactory.Create(SystemTools.ListProcesses));
            tools.Add(AIFunctionFactory.Create(SystemTools.GetEnv));
            tools.Add(AIFunctionFactory.Create(SystemTools.NetworkInterfaces));
            tools.Add(AIFunctionFactory.Create(SystemTools.Ping));
            tools.Add(AIFunctionFactory.Create(SystemTools.DnsLookup));
            tools.Add(AIFunctionFactory.Create(SystemTools.CheckPort));
            tools.Add(AIFunctionFactory.Create(SystemTools.HttpCheck));
            tools.Add(AIFunctionFactory.Create(SystemTools.Whois));
            tools.Add(AIFunctionFactory.Create(SystemTools.SetEnv));
            tools.Add(AIFunctionFactory.Create(SystemTools.GetCurrentDirectory));
        }
        if (name is "LTAI-Chat" or "LTAI-System" or "LTAI-Code" or "LTAI-Writer" or "LTAI-Frontend")
        {
            var bgJobs = sp.GetRequiredService<BackgroundJobService>();
            tools.Add(AIFunctionFactory.Create(bgJobs.StartJob));
            tools.Add(AIFunctionFactory.Create(bgJobs.ListJobs));
            tools.Add(AIFunctionFactory.Create(bgJobs.GetJobOutput));
            tools.Add(AIFunctionFactory.Create(bgJobs.WaitForJob));
            tools.Add(AIFunctionFactory.Create(bgJobs.StopJob));
        }
        // P14.13: TaskQueueTool — async named-task dispatch (echo / sleep / custom).
        // Same 5 agents as BackgroundJobService (those that already manage long-running work).
        if (name is "LTAI-Chat" or "LTAI-Chat-Pro" or "LTAI-System" or "LTAI-Code" or "LTAI-Writer")
        {
            var tq = sp.GetRequiredService<LTAI.Agent.Tools.TaskQueueTool>();
            tools.Add(AIFunctionFactory.Create(tq.EnqueueTask));
            tools.Add(AIFunctionFactory.Create(tq.ListTasks));
            tools.Add(AIFunctionFactory.Create(tq.GetTask));
            tools.Add(AIFunctionFactory.Create(tq.WaitForTask));
            tools.Add(AIFunctionFactory.Create(tq.CancelTask));
        }
        // P17.5: question tool — every agent can ask structured follow-up questions.
        {
            var qt = sp.GetRequiredService<LTAI.Agent.Tools.QuestionTool>();
            tools.Add(AIFunctionFactory.Create(qt.AskQuestions));
        }
        // Knowledge asset tools — all agents can commit/search knowledge
        {
            var kat = sp.GetRequiredService<LTAI.Agent.Tools.KnowledgeAssetTool>();
            tools.Add(AIFunctionFactory.Create(kat.WikiCommit));
            tools.Add(AIFunctionFactory.Create(kat.WikiSearch));
            tools.Add(AIFunctionFactory.Create(kat.WikiList));
            tools.Add(AIFunctionFactory.Create(kat.WikiExtract));
        }
        if (canExec)
        {
            var sys = new SystemTools();
            tools.Add(AIFunctionFactory.Create(sys.RunInContainer));
            tools.Add(AIFunctionFactory.Create(sys.RunWithNetwork));
            tools.Add(AIFunctionFactory.Create(sys.CheckDockerAsync));
        }

        // File download tool (confirm=true 才下载)
        if (canRead && canWrite && (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Writer" or "LTAI-Frontend"))
        {
            tools.Add(AIFunctionFactory.Create(FileDownloadTool.DownloadFile));
        }

        // Workflow tools (lazy-resolve via IServiceProvider to avoid circular DI)
        if (name is "LTAI-Chat" or "LTAI-Writer" or "LTAI-Frontend")
        {
            var wfTools = new WorkflowTools(sp);
            tools.Add(AIFunctionFactory.Create(wfTools.WorkflowHandoff));
            tools.Add(AIFunctionFactory.Create(wfTools.WorkflowSequential));
            tools.Add(AIFunctionFactory.Create(wfTools.WorkflowConcurrent));
        }

        // ClusterSummarizer — LLM-powered retrieval result clustering.
        // Available to knowledge-heavy agents for organizing search results
        // by theme into a structured summary.
        if (name is "LTAI-Chat" or "LTAI-Chat-Pro" or "LTAI-System" or "LTAI-Writer" or "LTAI-Data")
        {
            var cs = sp.GetRequiredService<LTAI.Agent.Tools.ClusterSummarizer>();
            tools.Add(AIFunctionFactory.Create(cs.SummarizeAsync));
        }

        // DeepenSearchTool — DRIFT-inspired iterative deepen KG search.
        // Available to research-heavy agents for multi-hop knowledge discovery.
        if (name is "LTAI-Chat" or "LTAI-Chat-Pro" or "LTAI-System" or "LTAI-Writer" or "LTAI-Data")
        {
            var dst = sp.GetRequiredService<LTAI.Agent.Tools.DeepenSearchTool>();
            tools.Add(AIFunctionFactory.Create(dst.DeepenSearchAsync));
        }

        // ====== NEW TOOLS (added May 2026) ======

        // Archive tools (zip/tar/gz create & extract)
        if (canExec)
        {
            var archive = new ArchiveTools(ws);
            tools.Add(AIFunctionFactory.Create(archive.ArchiveCreate));
            tools.Add(AIFunctionFactory.Create(archive.ArchiveExtract));
        }

        // Chart tools (bar/line/pie via SkiaSharp)
        if (canRead && canWrite)
        {
            var chart = new ChartTools(ws);
            tools.Add(AIFunctionFactory.Create(chart.ChartCreate));
        }

        // Database tools (SQLite queries)
        if (name is "LTAI-Chat" or "LTAI-Data" or "LTAI-Code")
        {
            var db = new DatabaseTools();
            tools.Add(AIFunctionFactory.Create(db.SqlQuery));
        }

        // Data transformation tools (JSON query, CSV read/write)
        if (canRead && canWrite)
        {
            var dt = new DataTransformTools(ws);
            tools.Add(AIFunctionFactory.Create(dt.JsonQuery));
            tools.Add(AIFunctionFactory.Create(dt.CsvRead));
            tools.Add(AIFunctionFactory.Create(dt.CsvWrite));
        }

        // Crypto tools (hash, encrypt, decrypt, base64)
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-System" or "LTAI-Security" or "LTAI-Writer")
        {
            tools.Add(AIFunctionFactory.Create(CryptoTools.HashFile));
            tools.Add(AIFunctionFactory.Create(CryptoTools.EncryptFile));
            tools.Add(AIFunctionFactory.Create(CryptoTools.DecryptFile));
        }
        if (canRead)
        {
            tools.Add(AIFunctionFactory.Create(CryptoTools.Base64Encode));
            tools.Add(AIFunctionFactory.Create(CryptoTools.Base64Decode));
        }

        // Markdown rendering tool
        if (canRead)
        {
            tools.Add(AIFunctionFactory.Create(MarkdownTools.RenderMarkdown));
        }

        // CCR retrieval tool — every agent needs access to decompress CCR markers
        {
            var rc = sp.GetRequiredService<LTAI.Agent.Tools.RetrieveContentTool>();
            tools.Add(AIFunctionFactory.Create(rc.RetrieveContent));
        }

        // Safety guardrail (optional — skip for local dev to reduce latency)
        SafetyCoordinator? safety = null;
        if (!opts.AI.SkipSafetyChecks)
        {
            // P6 Steer: use lightweight model for safety when available (cheaper, faster).
            // Falls back to DeepSeek V4 Flash when steer is disabled or unavailable.
            var steerLlm = sp.GetKeyedService<IChatClient>("steer");
            IChatClient safetyClient;
            if (steerLlm != null)
            {
                safetyClient = steerLlm;
            }
            else
            {
                var safetyKey = LTAI.Core.Configuration.SecretManager.Get(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY") ?? "";
                safetyClient = OpenAIChatClientFactory.Create("https://api.deepseek.com/v1", "deepseek-v4-flash", safetyKey);
            }
            safety = new SafetyCoordinator(safetyClient, loggerFactory.CreateLogger<SafetyCoordinator>());
        }

        // LTAI does NOT use MAF's ShellEnvironmentProvider:
        // - It starts a persistent PowerShell process via LocalShellExecutor, which hangs
        //   on Windows .NET 10 preview during InitializeAsync (60+ seconds).
        // - LTAI has its own EnvironmentProvider (line below) + SafeShellTool + WasmtimeSandbox,
        //   so MAF's auto shell-context probing is redundant.
        // The variable is kept as null so AIContextProviders can be updated in one place.

        LTAI.Core.Configuration.UsageTracker.SetContextWindowSize(opts.AI.MaxTokens);
        // P6 Steer: use lightweight model as verifier when available (saves ~LLM call per compaction).
        // The summarizer is still the main LLM (needs full context window); the verifier
        // only does a hallucination check (short output), which the steer model handles well.
        var steerLlmVerify = sp.GetKeyedService<IChatClient>("steer");
        var compaction = new CompactionProvider(
            new PipelineCompactionStrategy(
                new ContextWindowCompactionStrategy(64000, opts.AI.MaxTokens),
                new VerifiedSummarizationStrategy(
                    summarizer: llm,
                    verifier: steerLlmVerify ?? llm,
                    trigger: CompactionTriggers.TokensExceed(64000),
                    minimumPreservedGroups: 2)
            ), loggerFactory: loggerFactory);

        // KB & Code graphs for context augmentation (SQLite FTS5 + CTE)
        var kbGraph = sp.GetRequiredService<KbGraph>();
        var codeGraph = sp.GetRequiredService<CgGraph>();
        var codeChunkIndex = sp.GetRequiredService<LTAI.Agent.Indexing.CodeChunkIndex>();

        // Wasmtime sandbox: WASM-based code execution with WASI capability restrictions.
        // Recommended over Hyperlight (v0.4, pre-1.0) for general-purpose sandboxing.
        // See sandbox-roadmap MEMORY.md for the full evaluation.
        var wasmtimeSandbox = new WasmtimeSandbox(ws, loggerFactory.CreateLogger<WasmtimeSandbox>());

            // Skills provider: loads SKILL.md from skills/ (框架自动去重合并)
        // P3 APM: also loads from .agents/skills/ (APM-managed skills)
        var apmSkillsDir = Path.Combine(ws, ".agents", "skills");
        var skillsDir = new[] {
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), "skills"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "skills"),
        }.FirstOrDefault(Directory.Exists) ?? Path.Combine(Directory.GetCurrentDirectory(), "skills");
        Directory.CreateDirectory(skillsDir);
        var skillDirs = new List<string> { skillsDir };
        if (Directory.Exists(apmSkillsDir))
            skillDirs.Add(apmSkillsDir);

        var skillsBuilder = new Microsoft.Agents.AI.AgentSkillsProviderBuilder()
            .UseFileSkills([.. skillDirs]);

        if (opts.SkillsUrls is { Length: > 0 })
            skillsBuilder = skillsBuilder.UseSource(
                new AgentUrlSkillsSource(opts.SkillsUrls, httpFactory.CreateClient()));

        var skillsProvider = skillsBuilder
            .UseFileScriptRunner(LTAI.Agent.Tools.SkillScriptRunner.RunAsync)
            .UseOptions(o =>
            {
                o.ScriptApproval = true;
                 o.SkillsInstructionPrompt =
                    """
                    你拥有领域专精技能（skills），每个技能包含专门的指令、参考文档和资产。

                    <available_skills>
                    {skills}
                    </available_skills>

                    当任务匹配某个技能的领域时：
                    1. 用 `load_skill` 加载技能指令（示例：load_skill("code-review")）
                    2. 遵循技能提供的指引
                    3. 如果技能声明了 allowedTools，请优先使用这些工具
                    {resource_instructions}
                    {script_instructions}
                    只加载所需技能，不要全部加载。
                    """;
            })
            .Build();

        // ── Plan mode 特殊处理 ──
        var isPlanMode = name == "LTAI-Plan";
        if (isPlanMode)
        {
            tools.Clear();
            tools.Add(AIFunctionFactory.Create(LTAI.Agent.Tools.PlanTools.PlanExit));
            if (canRead)
            {
                var planFs = new FileSystemTools(ws);
                tools.Add(AIFunctionFactory.Create((string path) => planFs.ReadFileContent(path), "ReadFileContent", "Read a file"));
                tools.Add(AIFunctionFactory.Create(planFs.Glob));
                tools.Add(AIFunctionFactory.Create(planFs.ListFiles));
                tools.Add(AIFunctionFactory.Create(planFs.DirectoryTree));
            }
            var planSearch = new SearchTools(ws);
            tools.Add(AIFunctionFactory.Create(planSearch.SearchContent));
            tools.Add(AIFunctionFactory.Create(planSearch.SearchFiles));
        }

        // Cross-session long-term memory: 7-layer memory palace (PalaceStore + AIContextProviders).
        // Hierarchical Wing→Room→Drawer architecture. Each layer has a fixed token budget
        // (L0+L1 ≈ 900t always loaded).
        var embedder = sp.GetRequiredService<LTAI.AI.EmbeddingClient>();
        var palaceDb = Path.Combine(opts.DataDirectory, "palace.db");
        WingClassifier.LlmClassifier = (text) => null;

        var palaceStore = new LTAI.Agent.Memory.PalaceStore(embedder, palaceDb,
            loggerFactory.CreateLogger<LTAI.Agent.Memory.PalaceStore>());

        // L0: Identity (~100t, always loaded). Reads from config or identity.txt.
        var identityPath = Path.Combine(AppContext.BaseDirectory, "identity.txt");
        var identityText = File.Exists(identityPath) ? File.ReadAllText(identityPath).Trim() : "";
        if (string.IsNullOrWhiteSpace(identityText))
            identityText = opts.AI.DefaultProvider ?? "";

        // Memory tools (persistent memory across sessions via PalaceStore)
        var palaceMemory = new MemoryTools(palaceStore, defaultWing: ws != null ? Path.GetFileName(ws.TrimEnd('/', '\\')) : "project");
        if (canWrite)
        {
            tools.Add(AIFunctionFactory.Create(palaceMemory.Remember));
            tools.Add(AIFunctionFactory.Create(palaceMemory.Forget));
            tools.Add(AIFunctionFactory.Create(palaceMemory.RecallMemory));
            tools.Add(AIFunctionFactory.Create(palaceMemory.ListMemories));
        }

        // MCP (Model Context Protocol) client tools: connect to external MCP servers
        // configured in appsettings.json under "LTAI:Mcp:Servers". Lazy + cached — the
        // factory's first call spawns child stdio processes, subsequent calls reuse the
        // tool list. Plan mode keeps its read-only set; MCP tools (e.g. filesystem) are
        // disabled there to maintain strict read-only guarantees.
        if (!isPlanMode)
        {
            var mcpFactory = sp.GetRequiredService<LTAI.Agent.Mcp.McpClientFactory>();
            var mcpTools = await mcpFactory.GetToolsAsync(opts.Mcp).ConfigureAwait(false);
            foreach (var mcpTool in mcpTools)
            {
                if (!canRead) continue;
                var mn = mcpTool.Name.ToLowerInvariant();
                if (mn.Contains("write") || mn.Contains("create") || mn.Contains("delete") || mn.Contains("upload"))
                { if (!canWrite) continue; }
                if (mn.Contains("shell") || mn.Contains("command") || mn.Contains("exec") || mn.Contains("process"))
                { if (!canExec) continue; }
                tools.Add(mcpTool);
            }
        }

        // Semantic code search tool (cocoindex-inspired AST chunk index).
        // Available for all canRead agents (not in Plan Mode — no AST index in read-only mode).
        if (canRead && !isPlanMode)
            tools.Add(AIFunctionFactory.Create(codeChunkIndex.SemanticCodeSearch));

        // P3: APM / MCP Registry 包管理工具 — 所有 agent 可用（需安装 apm CLI）
        {
            var pkg = new LTAI.Agent.Tools.PackageManagerTools();
            tools.Add(AIFunctionFactory.Create(pkg.PkgSearch));
            tools.Add(AIFunctionFactory.Create(pkg.PkgInstall));
            tools.Add(AIFunctionFactory.Create(pkg.PkgList));
        }

        // AI 调试工具集: 断点/变量/栈/步进 — 仅桌面端有 IDebugBridge 时生效
        // 可用 agent: LTAI-Chat, LTAI-Code, LTAI-System (调试相关 agent)
        if (name is "LTAI-Chat" or "LTAI-Chat-Pro" or "LTAI-Code" or "LTAI-System")
        {
            var debugBridge = sp.GetService<LTAI.Core.Debugging.IDebugBridge>();
            if (debugBridge != null)
            {
                var debug = new LTAI.Agent.Tools.DebugTools(debugBridge);
                tools.Add(AIFunctionFactory.Create(debug.DebugStatus));
                tools.Add(AIFunctionFactory.Create(debug.SetBreakpoint));
                tools.Add(AIFunctionFactory.Create(debug.RemoveBreakpoint));
                tools.Add(AIFunctionFactory.Create(debug.ListBreakpoints));
                tools.Add(AIFunctionFactory.Create(debug.DebugContinue));
                tools.Add(AIFunctionFactory.Create(debug.DebugStepOver));
                tools.Add(AIFunctionFactory.Create(debug.DebugStepInto));
                tools.Add(AIFunctionFactory.Create(debug.DebugStepOut));
                tools.Add(AIFunctionFactory.Create(debug.DebugStop));
                tools.Add(AIFunctionFactory.Create(debug.DebugGetStack));
                tools.Add(AIFunctionFactory.Create(debug.DebugGetVariables));
                tools.Add(AIFunctionFactory.Create(debug.DebugEvaluate));
                tools.Add(AIFunctionFactory.Create(debug.DebugGetThreads));
                tools.Add(AIFunctionFactory.Create(debug.DebugSwitchThread));
                tools.Add(AIFunctionFactory.Create(debug.DebugAnalyzeFailure));
            }
        }

        // 去重：同名工具保留第一个，记录警告
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = tools.Count - 1; i >= 0; i--)
        {
            if (!seenNames.Add(tools[i].Name))
            {
                log?.LogWarning("工具名重复已被移除: {Name}", tools[i].Name);
                tools.RemoveAt(i);
            }
        }

        AIAgent agent = guardedLlm.AsHarnessAgent(
            maxContextWindowTokens: 0, // 0 = disabled: LTAI's own CompactionProvider at position [5] handles compaction
            maxOutputTokens: opts.AI.MaxTokens,
            options: new HarnessAgentOptions
            {
                Name = name,
                // P10.2: Chinese harness instructions replacing the default English
                // block. Default is Chinese; switches to English when OS language is en-US.
                // Uses LTAI.Core.I18n.Locale for culture-aware string selection.
                HarnessInstructions = isPlanMode
                    ? null  // plan mode keeps the default
                    : AppendAgentPrompt(BuildSystemPrompt(), agentPrompt),
                Description = isPlanMode
                    ? BuildPlanModePrompt()
                    : BuildAgentDescription(name, description),
                ChatOptions = new ChatOptions
                {
                    Temperature = temperature ?? (float)opts.AI.Temperature,
                    TopP = topP ?? 0.95f,
                    MaxOutputTokens = opts.AI.MaxTokens,
                    Tools = tools,
                    ModelId = modelId,
                },
                // F2: cap at 200 messages to prevent unbounded memory growth
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new MaxMessageCountReducer(200),
                    ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
                }),
                // 7-layer memory palace: L0 identity → L1 essential → L3 on-demand → L4 deep → L6 diary.
                // Placed after tool-filtering providers (Tool RAG, Skill ranking) and before the final
                // instruction providers so memories augment the conversation context.
                // Tool RAG: 动态工具召回（放第一个）→ L1 Skill Evolution Ranking
                // P12.2: inject ToolEmbeddingCache so 80+ tool description embeddings are
                // batched + persisted. Cold start 0 ONNX calls after first run.
                AIContextProviders = safety != null
                    ? [new LTAI.Agent.Tools.ToolRetrievalProvider(
                            sp.GetRequiredService<LTAI.AI.EmbeddingClient>(),
                            cache: sp.GetService<LTAI.AI.ToolEmbeddingCache>()),
                       new LTAI.Agent.Tools.SkillRankingProvider(
                           sp.GetRequiredService<LTAI.Agent.Tools.SkillEvolutionEngine>(),
                           sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Tools.SkillRankingProvider>()),
                       safety,
                       new LTAI.Agent.Memory.L0IdentityProvider(identityText),
                       new LTAI.Agent.Memory.L1EssentialProvider(palaceStore, name, loggerFactory.CreateLogger<LTAI.Agent.Memory.L1EssentialProvider>()),
                       compaction,
                        new LTAI.Agent.Context.CCRProvider(
                            sp.GetRequiredService<LTAI.Agent.Context.CompressionStore>(),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Context.CCRProvider>()),
                         kbGraph, codeGraph, codeChunkIndex, wasmtimeSandbox,
                        new LTAI.Agent.Memory.L3OnDemandProvider(palaceStore, loggerFactory.CreateLogger<LTAI.Agent.Memory.L3OnDemandProvider>()),
                        new LTAI.Agent.Memory.L4DeepSearchProvider(palaceStore, embedder, loggerFactory.CreateLogger<LTAI.Agent.Memory.L4DeepSearchProvider>()),
                        new LTAI.Agent.Memory.L6AgentDiaryProvider(palaceStore, name, loggerFactory.CreateLogger<LTAI.Agent.Memory.L6AgentDiaryProvider>()),
                         sp.GetService<LTAI.Agent.Indexing.ProvenanceProvider>()!,
                          new LTAI.Agent.Tools.InstructionProvider(modelId), new LTAI.Agent.Tools.EnvironmentProvider(), skillsProvider,
                         new LTAI.Agent.Context.CacheAlignerProvider(
                             sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Context.CacheAlignerProvider>())]
                       : [new LTAI.Agent.Tools.ToolRetrievalProvider(
                              sp.GetRequiredService<LTAI.AI.EmbeddingClient>(),
                              cache: sp.GetService<LTAI.AI.ToolEmbeddingCache>()),
                         new LTAI.Agent.Tools.SkillRankingProvider(
                             sp.GetRequiredService<LTAI.Agent.Tools.SkillEvolutionEngine>(),
                             sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Tools.SkillRankingProvider>()),
                         new LTAI.Agent.Memory.L0IdentityProvider(identityText),
                         new LTAI.Agent.Memory.L1EssentialProvider(palaceStore, name, loggerFactory.CreateLogger<LTAI.Agent.Memory.L1EssentialProvider>()),
                         compaction,
                         new LTAI.Agent.Context.CCRProvider(
                             sp.GetRequiredService<LTAI.Agent.Context.CompressionStore>(),
                             sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Context.CCRProvider>()),
                         kbGraph, codeGraph, codeChunkIndex, wasmtimeSandbox,
                         new LTAI.Agent.Memory.L3OnDemandProvider(palaceStore, loggerFactory.CreateLogger<LTAI.Agent.Memory.L3OnDemandProvider>()),
                         new LTAI.Agent.Memory.L4DeepSearchProvider(palaceStore, embedder, loggerFactory.CreateLogger<LTAI.Agent.Memory.L4DeepSearchProvider>()),
                         new LTAI.Agent.Memory.L6AgentDiaryProvider(palaceStore, name, loggerFactory.CreateLogger<LTAI.Agent.Memory.L6AgentDiaryProvider>()),
                         sp.GetService<LTAI.Agent.Indexing.ProvenanceProvider>()!,
                         new LTAI.Agent.Tools.InstructionProvider(modelId), new LTAI.Agent.Tools.EnvironmentProvider(), skillsProvider,
                          new LTAI.Agent.Context.CacheAlignerProvider(
                              sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Context.CacheAlignerProvider>())],

                 // ── Disable MAF defaults LTAI doesn't need ────────────────────
                // LTAI uses its own 7-layer memory palace (PalaceStore + AIContextProviders).
                DisableFileMemory = true,
                // LTAI uses its own tools (WasmtimeSandbox + SafeShellTool), not the file-access provider.
                DisableFileAccess = true,
                // LTAI doesn't surface web search to its agents.
                DisableWebSearch = true,
                // LTAI doesn't surface the TodoProvider/AgentModeProvider workflow.
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                // LTAI has its own AgentSkillsProvider (the one passed above), pre-configured
                // with script approval + custom instructions. Don't double-register MAF's.
                DisableAgentSkillsProvider = true,

                // Keep ToolApprovalAgent + OpenTelemetryAgent enabled (HarnessAgent adds them
                // as the outermost decorators by default). Use the per-agent source name so
                // /health and DevUI can identify spans.
                OpenTelemetrySourceName = $"LTAI.{name}",

                // P10.3: bound function-invocation iterations. Default is 40; bump to 50
                // to give multi-agent BackgroundAgents delegation room to converge (the
                // "StartTask → WaitForFirstCompletion → GetResults" loop counts as
                // several iterations per logical task).
                MaximumIterationsPerRequest = 50,

                // P10.0: BackgroundAgents delegation. Every LTAI agent can asynchronously
                // delegate work to its sibling agents (LTAI-Chat → LTAI-Math for numerical
                // work, LTAI-Code for code execution, etc.) via the 6 BackgroundAgents_*
                // tools auto-injected by MAF. Sister agents are wrapped in
                // LazyAIAgentProxy to break the circular dependency at HarnessAgent
                // construction time (Name/Description come from the static AgentRegistry;
                // RunAsync/RunStreamingAsync resolve the actual agent on first call, by
                // which time the agent graph is fully built).
                BackgroundAgents = AgentRegistry.LoadAll()
                    .Where(d => !string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(d.Name, "router", StringComparison.OrdinalIgnoreCase))
                    .Select(d => (AIAgent)new LazyAIAgentProxy(sp, d.Name))
                    .ToList(),
                BackgroundAgentsProviderOptions = new BackgroundAgentsProviderOptions
                {
                    Instructions = """
                    ## BackgroundAgents — 异步委派
                    你可以将任务**异步委派**给以下 sibling agents。每个 agent 在自己 session 中独立并发执行。

                    ### 典型用法
                    1. `BackgroundAgents_StartTask(agentName, goal)` → 启动1个或多个后台任务（不阻塞）
                    2. `BackgroundAgents_WaitForFirstCompletion()` → 等待任意一个完成后取结果
                    3. `BackgroundAgents_GetTaskResults(id)` → 取出已完成的结果
                    4. 回复用户前**必须**等待所有 outstanding tasks 完成
                    5. 取完结果后调用 `BackgroundAgents_ClearCompletedTask(id)` 释放内存

                    ### 适用场景
                    - **并发搜索**：同时让 LTAI-Code 分析代码 + LTAI-Data 查数据 + LTAI-Writer 写文档
                    - **分步委派**：LTAI-Math 算数值 → 结果传给 LTAI-Code 实现 → 再传给 LTAI-Writer 写说明
                    - **异步后台**：长耗时操作（编译、测试、数据迁移）交给专用 agent，不阻塞主对话

                    ### 工具列表
                    - `BackgroundAgents_StartTask` — 启动后台任务（返回 taskId，不阻塞）
                    - `BackgroundAgents_WaitForFirstCompletion` — 等待任意一个任务完成
                    - `BackgroundAgents_GetTaskResults` — 取出已完成任务的文本结果
                    - `BackgroundAgents_GetAllTasks` — 列出所有任务（id/状态/描述/agent 名）
                    - `BackgroundAgents_ContinueTask` — 向已完成任务的 session 追加输入
                    - `BackgroundAgents_ClearCompletedTask` — 释放已完成任务的 session

                    {background_agents}
                    """,
                },
            });

        // LTAI's outer-most logging wrapper — captures the final agent response and the
        // pre-decorator inner-agent state. HarnessAgent's own OpenTelemetryAgent / ToolApprovalAgent
        // sit just inside this, so the log entry is recorded after both have transformed the run.
        agent = new LoggingAgent(agent, log!);
        return agent;
    }

    // ── B3: Bilingual system prompt helpers ──

    private static string BuildSystemPrompt()
    {
        if (!LTAI.Core.I18n.Locale.IsChinese)
        {
            return LTAI.Core.I18n.Locale.Get("SystemPromptIntro") + "\n\n"
                + "# Tone & Style\n"
                + "- Be concise and direct. Answer in 1-3 sentences when possible. Minimize output tokens.\n"
                + "- Do NOT add preamble/postamble like \"Here is the answer...\" or \"Based on the analysis...\".\n"
                + "- Do NOT add code explanation summaries unless asked. After working on a file, just stop.\n"
                + "- Use code references with `filepath:line_number` format (e.g., `src/services/process.ts:712`).\n"
                + "- Never use emojis unless the user explicitly requests them.\n"
                + "- Use GitHub-flavored markdown. Output renders in a command-line interface.\n"
                + "\n"
                + "# Task Execution\n"
                + "- Think before acting. Break complex tasks into clear steps.\n"
                + "- Use available tools to gather information, execute actions, and verify results.\n"
                + "- Explain your reasoning inside <thinking>...  tags so the user can follow.\n"
                + "- Structure: <thinking>analysis</thinking> → tool calls → <thinking>reflection</thinking> → final answer.\n"
                + "- Before calling more than 4 tools in a row, explain what you are doing.\n"
                + "- If a tool call fails, **adjust your strategy** instead of retrying the same call.\n"
                + "- After completing a task, give a clear summary: what was done, what was found.\n"
                + "- If the model is insufficient for complex tasks (cross-file refactoring, concurrency safety analysis, etc.),\n"
                + "  output `<<<NEEDS_PRO: <reason>>>` to request upgrade to a stronger model.\n"
                + "\n"
                + "# Proactiveness\n"
                + "- You are allowed to be proactive, but only when the user asks you to do something.\n"
                + "- Strive to balance: (1) doing the right thing when asked, including follow-up actions vs (2) not surprising the user.\n"
                + "- Do not add extra explanation or summary unless the user requests it.\n"
                + "\n"
                + "# Following Conventions\n"
                + "- When making changes, first understand the file's code conventions. Mimic code style, use existing libraries.\n"
                + "- NEVER assume a given library is available. Check neighboring files or package.json/cargo.toml first.\n"
                + "- When creating a new component, look at existing ones first for patterns, naming, typing.\n"
                + "- When editing, read the code's surrounding context (especially imports) to understand framework choice.\n"
                + "- Always follow security best practices. Never introduce code that exposes or logs secrets and keys.\n"
                + "- Never commit secrets or keys to the repository.\n"
                + "\n"
                + "# Code References\n"
                + "- When referencing specific functions or pieces of code, use `filepath:line_number` format\n"
                + "  so the user can easily navigate to the source code location.\n"
                + "\n"
                + "# Code Understanding\n"
                + "- When asked about how code works, call SemanticCodeSearch first to find relevant code snippets.\n"
                + "- Only use ReadFileContent for the full file context when snippets are insufficient.\n"
                + "- Before editing a file, read it first to understand its structure and conventions.\n"
                + "\n"
                + "# Tool Usage Policy\n"
                + "- Before making edits, read the file first with the Read tool (or equivalent).\n"
                + "- Use Glob and Grep to find files before reading them.\n"
                + "- Verify results after tool calls to ensure correctness.\n"
                + "- Call multiple independent tools in parallel for efficiency.\n"
                + "- Prefer editing existing files. NEVER write new files unless explicitly required.\n"
                + "- For multi-step tasks, use TodoWrite to track progress.\n"
                + "\n"
                + "## Example\n"
                + "- User: \"How many files are in src/?\"\n"
                + "- <thinking>User wants a file count. I'll use Glob or DirectoryTree to list files, then count.</thinking>\n"
                + "- [calls Glob(\"**/*\", \"src/\")]\n"
                + "- <thinking>The tool returned 42 files. I'll report this to the user.</thinking>\n"
                + "- 42 files in src/.\n";
        }
        return LTAI.Core.I18n.Locale.Get("SystemPromptIntro") + "\n\n"
            + "# 语气与风格\n"
            + "- 简洁直接。能用 1-3 句回答就不要写段落。最小化输出 token。\n"
            + "- 不要加前导/结尾语，如\"以下是答案...\"或\"基于以上分析...\"。\n"
            + "- 除非用户要求，不要对代码做额外解释。修改完文件直接结束。\n"
            + "- 代码引用使用 `filepath:行号` 格式（如 `src/services/process.ts:712`）。\n"
            + "- 除非用户明确要求，不要使用 emoji。\n"
            + "- 使用 GitHub-flavored markdown。输出将在命令行界面展示。\n"
            + "\n"
            + "# 任务执行\n"
            + "- 先思考再行动。复杂任务拆成清晰的步骤。\n"
            + "- 用可用工具收集信息、执行操作并验证结果。\n"
            + "- 推理过程用 <thinking>...</thinking> 包裹，让用户能跟随你的思路。\n"
            + "- 整体结构：<thinking>分析</thinking> → 工具调用 → <thinking>反思</thinking> → 最终回答。\n"
            + "- 连续调用 4 次以上工具前必须先向用户说明你正在做什么。\n"
            + "- 如果工具调用失败或返回异常，**调整策略**而不是重试同一个调用。\n"
            + "- 任务完成后，给出清晰的总结：做了什么、发现了什么。\n"
            + "- 如果模型不足以完成复杂任务（跨文件重构、并发安全分析等），\n"
            + "  在回复中输出 `<<<NEEDS_PRO: <原因>>>` 标记，系统将自动切换到更强的模型。\n"
            + "\n"
            + "# 主动性\n"
            + "- 用户要求做事时可以主动，但不要做用户没要求的事。\n"
            + "- 平衡两点：(1) 被问到时要做好，包括后续操作；(2) 不要让用户意外。\n"
            + "- 除非用户要求，不要添加额外解释或总结。\n"
            + "\n"
            + "# 遵循约定\n"
            + "- 修改代码前先理解文件的代码风格。模仿代码风格，使用现有库和工具。\n"
            + "- 绝不要假设某个库可用。先检查相邻文件或 package.json/cargo.toml。\n"
            + "- 创建新组件时先看现有的，了解模式、命名、类型约定。\n"
            + "- 编辑代码时读取上下文（尤其是 import）了解框架选择。\n"
            + "- 始终遵循安全最佳实践。不要引入暴露或记录密钥的代码。\n"
            + "- 永远不要将密钥提交到仓库。\n"
            + "\n"
            + "# 代码引用\n"
            + "- 引用函数或代码段时，使用 `filepath:行号` 格式方便用户导航。\n"
            + "\n"
            + "# 代码理解\n"
            + "- 需要理解代码逻辑时，先调用 SemanticCodeSearch 获取相关片段。\n"
            + "- 只有片段不够时再用 ReadFileContent 读取完整文件。\n"
            + "- 编辑文件前先读取，理解其结构和约定。\n"
            + "\n"
            + "# 工具使用策略\n"
            + "- 编辑前先用 Read 工具读取文件。\n"
            + "- 用 Glob 和 Grep 找到文件再读取。\n"
            + "- 工具调用后验证结果确保正确。\n"
            + "- 独立工具调用可以并行执行以提高效率。\n"
            + "- 优先编辑现有文件。除非明确要求，不要新建文件。\n"
            + "- 多步骤任务用 TodoWrite 追踪进度。\n"
            + "\n"
            + "## 示例\n"
            + "- 用户：\"src/ 目录下有多少文件？\"\n"
            + "- <thinking>用户想知道文件数。我用 Glob 或 DirectoryTree 列出文件再计数。</thinking>\n"
            + "- [调用 Glob(\"**/*\", \"src/\")]\n"
            + "- <thinking>工具返回了 42 个文件。我报告给用户。</thinking>\n"
            + "- src/ 目录下共有 42 个文件。\n";
    }

    private static string AppendAgentPrompt(string basePrompt, string? agentPrompt)
    {
        if (string.IsNullOrWhiteSpace(agentPrompt)) return basePrompt;
        return basePrompt + "\n\n## Agent 指令\n" + agentPrompt.Trim();
    }

    private static string BuildPlanModePrompt()
    {
        if (!LTAI.Core.I18n.Locale.IsChinese)
        {
            return """
            <system-reminder>
            # Plan Mode — Read-Only Planning

            You are in Plan Mode. No file modifications, shell execution, or system changes are allowed.

            ## Workflow (5 phases)
            1. **Initial Understanding** — Read relevant files to understand the codebase and the user's request.
            2. **Design** — Search for existing patterns, similar implementations, and edge cases.
            3. **Review** — Consider trade-offs, risks, alternatives. Use parallel exploration when needed.
            4. **Final Plan** — Construct a clear, step-by-step plan with file paths and key decisions.
            5. **Exit** — Call PlanExit to submit the plan and exit Plan Mode.

            ## Constraints
            - ABSOLUTELY FORBIDDEN: writing files, editing files, running commands, git operations
            - ALLOWED: reading files, searching, glob, directory listing, web fetch
            - After completing the plan, MUST call PlanExit
            </system-reminder>
            """;
        }
        return """
        <system-reminder>
        # Plan Mode — 只读规划

        你处于 Plan mode。严禁任何文件修改、shell 执行或系统变更。

        ## 工作流（5 阶段）
        1. **理解需求** — 读取相关文件，理解代码库和用户请求。
        2. **设计方案** — 搜索现有模式、相似实现和边界情况。
        3. **审查权衡** — 考虑取舍、风险、替代方案。必要时并行探索。
        4. **最终计划** — 构建清晰的步骤计划，包含文件路径和关键决策。
        5. **退出** — 调用 PlanExit 提交计划并退出 Plan Mode。

        ## 约束
        - 绝对禁止：写文件、编辑文件、运行命令、git 操作
        - 允许：读文件、搜索、glob、目录列表、web fetch
        - 完成计划后必须调用 PlanExit
        </system-reminder>
        """;
    }

    private static string BuildAgentDescription(string name, string description)
    {
        var isEn = !LTAI.Core.I18n.Locale.IsChinese;
        var roleLine = isEn
            ? $"You are {name}, {description}."
            : $"你是 {name}，{description}。";
        var dateHint = isEn
            ? "About dates: when users ask \"what day is it\" or \"what time is it\", call GetCurrentDateTime directly — do not guess."
            : "关于日期：当用户询问\"今天星期几\"\"现在几点\"等时间日期问题时，请直接调用 GetCurrentDateTime 工具获取实时时间，不要自行估算。";
        return $"{roleLine}\n{dateHint}\n";
    }
}

/// <summary>
/// P0: Minimal no-op AIAgent used when the real agent fails to build.
/// Returns a static error message so the caller can surface the failure gracefully.
/// </summary>
file sealed class FallbackAgent : AIAgent
{
    public FallbackAgent(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public override string? Name { get; }
    public override string? Description { get; }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct)
        => new(new MinimalAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonOptions, CancellationToken ct)
        => new(JsonSerializer.SerializeToElement(new { fallback = true }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement state, JsonSerializerOptions? jsonOptions, CancellationToken ct)
        => new(new MinimalAgentSession());

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct)
        => Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant,
            $"[Agent '{Name}' unavailable — build failed. Check logs for details.]")));

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken ct)
        => AsyncEnumerable.Repeat(new AgentResponseUpdate(ChatRole.Assistant,
            $"[Agent '{Name}' unavailable — build failed. Check logs for details.]"), 1);
}

file sealed class MinimalAgentSession : AgentSession
{
    public MinimalAgentSession() : base(new AgentSessionStateBag()) { }
}

// F2: caps InMemoryChatHistoryProvider message count to prevent unbounded growth
internal sealed class MaxMessageCountReducer : IChatReducer
{
    private readonly int _maxCount;
    public MaxMessageCountReducer(int maxCount) => _maxCount = Math.Max(10, maxCount);

    public Task<IEnumerable<ChatMessage>> ReduceAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        if (list.Count <= _maxCount)
            return Task.FromResult<IEnumerable<ChatMessage>>(list);

        // Keep the system prompt (first message) and the most recent messages
        var system = list.FirstOrDefault(m => m.Role == ChatRole.System);
        var recent = list.TakeLast(_maxCount - (system != null ? 1 : 0)).ToList();
        if (system != null)
            recent.Insert(0, system);

        return Task.FromResult<IEnumerable<ChatMessage>>(recent);
    }
}
#pragma warning restore MAAI001
