using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Core.Safety;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
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

    static ServiceCollectionExtensions()
    {
        // Defensive: MAF AIJsonUtilities.DefaultOptions does NOT include
        // NumberHandling.AllowReadingFromString by default. Chinese LLMs sometimes
        // emit string values for numeric / boolean parameters (e.g. "5" instead of 5,
        // "true" instead of true). Mutating the singleton is global but acceptable
        // in a single-process LTAI host. Replaces the deleted ToolCallRepairer
        // type-coercion path (P3 cleanup) for the most common case.
        AIJsonUtilities.DefaultOptions.NumberHandling =
            System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString;
    }

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
        foreach (var def in GetAgentDefinitions())
        {
            var captured = def;
            services.AddAIAgent(captured.Name, (sp, name) => captured.Build(sp, name), ServiceLifetime.Singleton);
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
        services.AddSingleton<KgStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new KgStore(opts.ResolveDataPath("kg.db"));
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
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<KbGraph>();
            return new KbGraph(store, llm, reranker, logger);
        });
        services.AddSingleton<CgGraph>(sp =>
        {
            var store = sp.GetRequiredService<KgStore>();
            var llm = sp.GetService<IChatClient>();
            var embedder = sp.GetService<LTAI.AI.EmbeddingClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<CgGraph>();
            return new CgGraph(store, llm, embedder, logger, Directory.GetCurrentDirectory());
        });

        // Step 3: Workflow orchestrator (with P7.7 decision-tree routing)
        // P12.1: pass ToolEmbeddingCache so the 10-agent description embeddings
        // are batched + persisted; cold-start 0 ONNX calls after first run.
        // P15: pass YAMLWorkflowRegistry so thresholds/candidates are hot-editable.
        services.AddSingleton<DecisionTreeRouter>(sp => new DecisionTreeRouter(
            sp.GetService<EmbeddingClient>(),
            sp.GetRequiredService<ILogger<DecisionTreeRouter>>(),
            sp.GetService<ToolEmbeddingCache>(),
            options: null,
            registry: sp.GetService<YAMLWorkflowRegistry>()));
        services.AddSingleton<AgentWorkflows>(sp =>
        {
            var all = sp.GetKeyedServices<AIAgent>(KeyedService.AnyKey)
                .ToDictionary(a => a.Name!, StringComparer.OrdinalIgnoreCase);
            return new AgentWorkflows(all.Values, all["router"],
                sp.GetRequiredService<ILogger<AgentWorkflows>>(),
                sp.GetRequiredService<DecisionTreeRouter>(),
                workflowRegistry: sp.GetService<YAMLWorkflowRegistry>());
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
        services.AddHostedService<WorkflowWatcherHostedService>();

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

        // Step 3c: Background job service
        services.AddSingleton<BackgroundJobService>();

        // Step 3c-mcp: MCP client factory (lazy connect to external MCP servers).
        // Connects on first GetToolsAsync call, then caches the tool list.
        services.AddSingleton<LTAI.Agent.Mcp.McpClientFactory>();

        // Step 3c-queue: in-process task queue (Channel<T>-based producer/consumer).
        // Lightweight substitute for MAF DurableTask; persists state in memory
        // and exposes EnqueueAsync / List / WaitAsync for deferred work.
        services.AddSingleton<LTAI.Agent.Tasks.TaskQueue>();

        // P14.13: TaskQueueTool — LLM-callable wrapper that exposes the queue
        // as 5 AITool methods (Enqueue/List/Get/Wait/Cancel). Owns a name->handler
        // registry so Enqueue dispatch works across the JSON tool boundary.
        services.AddSingleton<LTAI.Agent.Tools.TaskQueueTool>(sp =>
            new LTAI.Agent.Tools.TaskQueueTool(
                sp.GetRequiredService<LTAI.Agent.Tasks.TaskQueue>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<LTAI.Agent.Tools.TaskQueueTool>()));

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
            var chat = all["chat"];
            // Pro agent for complex task auto-upgrade (uses "deepseek-pro" provider)
            var proAgent = all.TryGetValue("chat-pro", out var p) ? p : chat;
            var budget = sp.GetService<LTAI.AI.BudgetTracker>();
            return new ChatAgent(chat, proAgent, wf, budget,
                localEmbedder: sp.GetService<LTAI.AI.LocalEmbedder>(),
                httpFactory: sp.GetService<IHttpClientFactory>());
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
        float? TopP)
    {
        public AIAgent Build(IServiceProvider sp, string name) => Task.Run(() =>
            BuildAgentImpl(sp, name, Description, CanRead, CanWrite, CanList, CanExec,
                modelId: ModelId, temperature: Temperature, topP: TopP)).GetAwaiter().GetResult();
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
                    TopP: (float?)def.TopP);
            }
            yield break;
        }

        // Fallback: hardcoded defaults (no agents/*.agent.md files found)
        // 任务类型 → temperature/topP 参考：AI编程 0.3/0.95 | 工具调用 0.3/0.95 | 通用问答 0.8/0.95 | 数学推理 1.0/0.95
        yield return new("LTAI-Router",   "任务调度器(无工具)",      false, false, false, false, null, 0.3f, 0.95f);
        yield return new("LTAI-Chat",     "通用对话助手",          true,  true,  true,  true,  null, 0.3f, 0.95f);
        yield return new("LTAI-Chat-Pro", "深度推理助手(Pro)",      true,  true,  true,  true,  "deepseek-pro", 0.3f, 0.95f);
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
        string? modelId = null, float? temperature = null, float? topP = null)
    {
        var ws = Directory.GetCurrentDirectory();
        var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var llm = sp.GetRequiredService<IChatClient>();
        var log = loggerFactory.CreateLogger("Agent." + name);

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
            tools.Add(AIFunctionFactory.Create(EiaTools.ClassifyAirQuality));
            tools.Add(AIFunctionFactory.Create(EiaTools.ClassifyNoise));
            tools.Add(AIFunctionFactory.Create(EiaTools.ClassifyWaterQuality));
            tools.Add(AIFunctionFactory.Create(EiaTools.GaussianPlume));
            tools.Add(AIFunctionFactory.Create(EiaTools.CO2Equivalent));
            tools.Add(AIFunctionFactory.Create(EiaTools.HazardQuotient));
            tools.Add(AIFunctionFactory.Create(EiaTools.LookupStandard));
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

        // Memory tools (persistent memory across sessions)
        var memDir = opts.ResolveDataPath("memories");
        var memory = new MemoryTools(ws, memDir);
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-System" or "LTAI-Writer")
        {
            tools.Add(AIFunctionFactory.Create(memory.Remember));
            tools.Add(AIFunctionFactory.Create(memory.Forget));
            tools.Add(AIFunctionFactory.Create(memory.RecallMemory));
            tools.Add(AIFunctionFactory.Create(memory.ListMemories));
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
            tools.Add(AIFunctionFactory.Create(SystemTools.ListDirectory));
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

        // Safety guardrail (optional — skip for local dev to reduce latency)
        SafetyCoordinator? safety = null;
        if (!opts.AI.SkipSafetyChecks)
        {
            var safetyKey = LTAI.Core.Configuration.SecretManager.Get(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY") ?? "";
            var safetyClient = OpenAIChatClientFactory.Create("https://api.deepseek.com/v1", "deepseek-chat", safetyKey);
            safety = new SafetyCoordinator(safetyClient, loggerFactory.CreateLogger<SafetyCoordinator>());
        }

        // LTAI does NOT use MAF's ShellEnvironmentProvider:
        // - It starts a persistent PowerShell process via LocalShellExecutor, which hangs
        //   on Windows .NET 10 preview during InitializeAsync (60+ seconds).
        // - LTAI has its own EnvironmentProvider (line below) + SafeShellTool + WasmtimeSandbox,
        //   so MAF's auto shell-context probing is redundant.
        // The variable is kept as null so AIContextProviders can be updated in one place.

        LTAI.Core.Configuration.UsageTracker.SetContextWindowSize(opts.AI.MaxTokens);
        var compaction = new CompactionProvider(
            new PipelineCompactionStrategy(
                new ContextWindowCompactionStrategy(64000, opts.AI.MaxTokens),
                new VerifiedSummarizationStrategy(
                    summarizer: llm,
                    verifier: llm,
                    trigger: CompactionTriggers.TokensExceed(64000),
                    minimumPreservedGroups: 2)
            ), loggerFactory: loggerFactory);

        // KB & Code graphs for context augmentation (SQLite FTS5 + CTE)
        var kbGraph = sp.GetRequiredService<KbGraph>();
        var codeGraph = sp.GetRequiredService<CgGraph>();

        // Wasmtime sandbox: WASM-based code execution with WASI capability restrictions.
        // Recommended over Hyperlight (v0.4, pre-1.0) for general-purpose sandboxing.
        // See sandbox-roadmap MEMORY.md for the full evaluation.
        var wasmtimeSandbox = new WasmtimeSandbox(ws, loggerFactory.CreateLogger<WasmtimeSandbox>());

            // Skills provider: loads SKILL.md from skills/ (框架自动去重合并)
        var skillsDir = new[] {
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(Directory.GetCurrentDirectory(), "skills"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "skills"),
        }.FirstOrDefault(Directory.Exists) ?? Path.Combine(Directory.GetCurrentDirectory(), "skills");
        Directory.CreateDirectory(skillsDir);

        var skillsBuilder = new Microsoft.Agents.AI.AgentSkillsProviderBuilder()
            .UseFileSkills([skillsDir]);

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

        // Cross-session long-term memory: Mem0 (remote, if MEM0_API_KEY set) or local SQLite+embedding.
        // Placed after tool-filtering providers (Tool RAG, Skill ranking) and before the final
        // instruction providers so memories augment the conversation context.
        var memoryProvider = LTAI.Agent.Memory.MemoryProviderSelector.Select(
            sp.GetRequiredService<LTAI.AI.EmbeddingClient>(),
            opts.DataDirectory,
            loggerFactory);

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
                tools.Add(mcpTool);
        }

        AIAgent agent = llm.AsHarnessAgent(
            maxContextWindowTokens: 64000,
            maxOutputTokens: opts.AI.MaxTokens,
            options: new HarnessAgentOptions
            {
                Name = name,
                // P10.2: Chinese harness instructions replacing the default English
                // block. LTAI's agents are Chinese-first; the default English text
                // is replaced wholesale rather than concatenated, since the agent's
                // own ChatOptions.Instructions (Description field below) carry the
                // agent-specific identity / role / duty content.
                HarnessInstructions = isPlanMode
                    ? null  // plan mode keeps the default — its behavior is rigid
                    : """
                      你是 LTAI 助手，使用工具完成用户的请求。

                      ## 一般准则
                      - 先思考再行动。复杂任务拆成清晰的步骤。
                      - 用可用工具收集信息、执行操作并验证结果。
                      - 解释你的推理过程，让用户能跟随你的思路。
                      - 连续调用 4 次以上工具前必须先向用户说明你正在做什么。
                      - 如果工具调用失败或返回异常，**调整策略**而不是重试同一个调用。
                      - 任务完成后，给出清晰的总结：做了什么、发现了什么。
                      - 如果模型不足以完成复杂任务（跨文件重构、并发安全分析等），
                        在回复中输出 `<<<NEEDS_PRO: <原因>>>` 标记，系统将自动切换到更强的模型。
                      """,
                Description = isPlanMode
                    ? """
                    <system-reminder>
                    # Plan Mode — 只读模式

                    你处于 Plan mode。严禁任何文件修改、shell 执行或系统变更。
                    你只能使用只读工具观察、分析和规划。

                    ## 职责
                    阅读代码、搜索信息、构造计划。完成后调用 PlanExit 工具提交计划并退出 Plan mode。

                    ## 约束
                    - 绝对禁止：写文件、编辑文件、运行命令、git 操作
                    - 允许：读文件、搜索、glob、目录列表
                    - 完成计划后调用 PlanExit
                    </system-reminder>
                    """
                    : $"你是 {name}，{description}。\n"
                    + "关于日期：当用户询问\"今天星期几\"\"现在几点\"等时间日期问题时，请直接调用 GetCurrentDateTime 工具获取实时时间，不要自行估算。\n"
                    + "工具调用注意：\n"
                    + "1. 参数必须是正确的JSON类型（数字不要加引号，布尔值用true/false）\n"
                    + "2. 不要用Markdown代码块包围工具调用\n"
                    + "3. 不要重复调用同一个工具（如果出错，先检查参数再重试）\n"
                    + "\n"
                    + "升级合约：如果当前模型无法完成复杂任务（如跨文件重构、并发安全性分析），\n"
                    + "在回复中输出 <<<NEEDS_PRO: <原因>>> 标记，系统将自动切换到更强的模型重试。\n"
                    + "示例：<<<NEEDS_PRO: 需要分析6个模块的循环依赖问题>>>",
                ChatOptions = new ChatOptions
                {
                    Temperature = temperature ?? (float)opts.AI.Temperature,
                    TopP = topP ?? 0.95f,
                    MaxOutputTokens = opts.AI.MaxTokens,
                    Tools = tools,
                    ModelId = modelId,
                },
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
                // Cross-session long-term memory: Mem0 (remote, if MEM0_API_KEY set) or local SQLite+embedding.
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
                       safety, compaction, kbGraph, codeGraph, wasmtimeSandbox, memoryProvider,
                       new LTAI.Agent.Tools.InstructionProvider(modelId), new LTAI.Agent.Tools.EnvironmentProvider(), skillsProvider]
                    : [new LTAI.Agent.Tools.ToolRetrievalProvider(
                            sp.GetRequiredService<LTAI.AI.EmbeddingClient>(),
                            cache: sp.GetService<LTAI.AI.ToolEmbeddingCache>()),
                       new LTAI.Agent.Tools.SkillRankingProvider(
                           sp.GetRequiredService<LTAI.Agent.Tools.SkillEvolutionEngine>(),
                           sp.GetRequiredService<ILoggerFactory>().CreateLogger<LTAI.Agent.Tools.SkillRankingProvider>()),
                       compaction, kbGraph, codeGraph, wasmtimeSandbox, memoryProvider,
                       new LTAI.Agent.Tools.InstructionProvider(modelId), new LTAI.Agent.Tools.EnvironmentProvider(), skillsProvider],

                // ── Disable MAF defaults LTAI doesn't need ────────────────────
                // LTAI uses its own Mem0/EmbeddedMemoryProvider (memoryProvider above).
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
                    ## BackgroundAgents (LTAI)
                    你可以将任务**异步委派**给以下 sibling agents。每个 agent 在自己 session 中并发执行。
                    - 调用 `BackgroundAgents_StartTask` 启动后台任务（不阻塞，可连续启动多个）
                    - 调用 `BackgroundAgents_WaitForFirstCompletion` 等待任意一个完成
                    - 调用 `BackgroundAgents_GetTaskResults` 取出已完成的文本结果
                    - 调用 `BackgroundAgents_GetAllTasks` 列出所有任务（id/状态/描述/agent 名）
                    - 调用 `BackgroundAgents_ContinueTask` 向已完成任务的 session 追加输入
                    - 调用 `BackgroundAgents_ClearCompletedTask` 释放已完成的 session 节省内存
                    - 重要：回复用户前**必须**等所有 outstanding tasks 完成
                    - 重要：取完结果后用 ClearCompletedTask 清理，除非还要 ContinueTask
                    {background_agents}
                    """,
                },
            });

        // LTAI's outer-most logging wrapper — captures the final agent response and the
        // pre-decorator inner-agent state. HarnessAgent's own OpenTelemetryAgent / ToolApprovalAgent
        // sit just inside this, so the log entry is recorded after both have transformed the run.
        agent = new LoggingAgent(agent, log);
        return agent;
    }
}
#pragma warning restore MAAI001
