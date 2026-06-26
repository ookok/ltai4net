using LTAI.Agent.Context;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.AI;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#pragma warning disable MAAI001

namespace LTAI.Agent;

/// <summary>
/// ─────────────────────────────────────────────────────
///  TOOL REGISTRATION MATRIX (agent × tool category)
/// ─────────────────────────────────────────────────────
///                     Chat  Code  Math  Data  System  LLM  Writer  Frontend  DCI
///  Filesystem R/W       ✅    ✅    —     ✅     —      —     ✅      ✅       ✅(R)
///  Shell/Exec           ✅    —     ✅    ✅     ✅     —     ✅      ✅       ✅
///  Search/Symbols       ✅    ✅    —     —     —      —     ✅      ✅       ✅
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
internal static partial class AgentBuilder
{
    public static AIAgent BuildAgent(IServiceProvider sp, string name, string description,
        bool canRead, bool canWrite, bool canList, bool canExec,
        string? modelId = null, float? temperature = null, float? topP = null, string[]? yamlTools = null)
    {
        return BuildAgentImpl(sp, name, description, canRead, canWrite, canList, canExec, modelId, temperature, topP, null, yamlTools);
    }

    private static readonly object _envLock = new();
    private static bool _envApplied;

    private static void ApplyEnvironmentConfig(LTAIOptions opts)
    {
        if (_envApplied) return;
        lock (_envLock)
        {
            if (_envApplied) return;
            LTAI.Core.Configuration.UsageTracker.SetContextWindowSize(opts.AI.ContextWindowSize);
            LTAI.Agent.Tools.RipgrepDetector.RipgrepDownloadUrl = opts.Mirrors.RipGrepUrl;
            LTAI.Agent.Tools.SkillScriptRunner.SystemPathFallback = opts.Security.SystemPathFallback;
            LTAI.Agent.Tools.SafeShellTool.SystemPathFallback = opts.Security.SystemPathFallback;
            LTAI.Agent.Tools.ShellSecurity.ApplyConfig(opts.Security);
            LTAI.Agent.Tools.SafeShellTool.ApplyConfig(opts.Security);
            EnvironmentConfig.Overrides = opts.EnvOverrides;
            LTAI.AI.LocalEmbedder.ModelBaseUrl = opts.Mirrors.ModelBaseUrl;
            _envApplied = true;
        }
    }

    public static AIAgent BuildAgentImpl(IServiceProvider sp, string name, string description,
        bool canRead, bool canWrite, bool canList, bool canExec,
        string? modelId = null, float? temperature = null, float? topP = null, string? agentPrompt = null,
        string[]? yamlTools = null)
    {
        var ws = Directory.GetCurrentDirectory();
        var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        // Wire PlanTools to ExecutionEngine for integrated plan execution
        if (LTAI.Agent.Tools.PlanTools.ExecutionEngine == null)
        {
            try
            {
                var workflows = sp.GetRequiredService<LTAI.Agent.Workflows.AgentWorkflows>();
                var router = sp.GetService<LTAI.Agent.Workflows.DecisionTreeRouter>();
                var queryClassifier = sp.GetService<Memory.QueryClassifier>();
                var triggerMatcher = sp.GetService<Memory.TriggerMatcher>();
                LTAI.Agent.Tools.PlanTools.ExecutionEngine = new Execution.ExecutionEngine(
                    workflows, router, loggerFactory.CreateLogger<Execution.ExecutionEngine>(),
                    queryClassifier: queryClassifier, triggerMatcher: triggerMatcher);
            }
            catch { /* ExecutionEngine not available — PlanTools runs standalone */ }
        }

        // Wire DI singleton to AgentModeObserver.Default (P11)
        var observer = sp.GetService<LTAI.Agent.Tooling.AgentModeObserver>();
        if (observer != null) LTAI.Agent.Tooling.AgentModeObserver.Default = observer;

        // Wire bounded stores for session state (P6)
        if (LTAI.Agent.Tools.PlanTools.Store == null)
        {
            var planStore = sp.GetService<LTAI.Agent.Tools.PlanStore>();
            if (planStore != null) LTAI.Agent.Tools.PlanTools.Store = planStore;
        }
        if (LTAI.Agent.Tools.TaskTools.Store == null)
        {
            var taskStore = sp.GetService<LTAI.Agent.Tools.TaskStore>();
            if (taskStore != null) LTAI.Agent.Tools.TaskTools.Store = taskStore;
        }
        var llm = sp.GetRequiredService<IChatClient>();
        var log = loggerFactory.CreateLogger("Agent." + name);

        // Agent-level lookahead routing: predict preferred agent for query
        try
        {
            var router = sp.GetService<Context.AgentLookaheadRouter>();
            if (router != null && !string.IsNullOrEmpty(agentPrompt))
            {
                var predicted = router.Predict(agentPrompt);
                if (predicted.Length > 0 && !predicted.Contains(name))
                    log.LogDebug("AgentLookaheadRouter: query '{Q}' → {Agents} (current: {Name})",
                        agentPrompt.Length > 60 ? agentPrompt[..60] + "..." : agentPrompt,
                        string.Join(", ", predicted), name);
            }
        }
        catch { /* router unavailable — non-critical */ }

        // P0.1: LLM I/O logging (zap-inspired, enabled via LTAI_LLM_LOG=true)
        IChatClient guardedLlm = llm;
        var ltaiOptions = sp.GetService<Microsoft.Extensions.Options.IOptions<LTAIOptions>>()?.Value;
        guardedLlm = new LTAI.Agent.Clients.LlmLoggingChatClient(guardedLlm, ltaiOptions);

        // MAF-aligned: ToolFilteringChatClient runs at IChatClient level (after all AIContextProviders).
        // Replaces ToolRetrievalProvider's AIContextProvider approach which had ordering issues with
        // HarnessAgent's built-in providers (FileAccessProvider, BackgroundAgentsProvider).
        var embedder = sp.GetRequiredService<LTAI.AI.EmbeddingClient>();
        var toolRegistry = sp.GetRequiredService<LTAI.AI.IToolRegistry>();
        var toolEmbeddingCache = sp.GetService<LTAI.AI.ToolEmbeddingCache>();
        var queryEmbeddingCache = sp.GetService<Experts.QueryEmbeddingCache>();
        var l3Client = sp.GetKeyedService<IChatClient>("l3");
        guardedLlm = new LTAI.Agent.Clients.ToolFilteringChatClient(guardedLlm, embedder, toolRegistry, toolEmbeddingCache, queryEmbeddingCache, l3Client);

        // DeerFlow-inspired: SubagentContextIsolation wraps LLM calls to prevent context leakage
        // between sub-agent and main agent conversations
        guardedLlm = new LTAI.Agent.Clients.SubagentContextIsolation(guardedLlm);

        var tools = new ToolSet();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var mmapProvider = sp.GetService<Caching.MmapFileProvider>();
        var writeBuf = sp.GetService<Caching.WriteBuffer>();
        var lspManager = sp.GetRequiredService<LanguageServer.LspLanguageManager>();

        RegisterFileAndTextTools(tools, name, canRead, canWrite, canList, canExec, ws, sp,
            mmapProvider, writeBuf);
        RegisterSearchAndCodeAnalysisTools(tools, name, canRead, ws, yamlTools, sp);
        RegisterWebTools(tools, name, httpFactory, yamlTools);
        RegisterMultimediaTools(tools, canRead, canExec, ws, yamlTools);
        RegisterDocumentTools(tools, canRead, canWrite, ws, sp, yamlTools);
        RegisterPlanAndDiagramTools(tools, name, httpFactory, yamlTools);
        RegisterGitTools(tools, name, ws, yamlTools);
        // RegisterReviewTools moved below — needs palaceStore
        RegisterSkillBankTools(tools, name, yamlTools);
        RegisterLspTools(tools, name, yamlTools, lspManager);
        RegisterTaskTools(tools, name, yamlTools);
        RegisterIntegrationTools(tools, name, httpFactory, yamlTools);
        RegisterSystemAndJobTools(tools, name, canExec, canRead, canWrite, ws, sp, yamlTools);
        RegisterWorkflowTools(tools, name, sp, yamlTools);
        RegisterClusterAndDeepenTools(tools, name, sp);
        RegisterNewDomainTools(tools, name, canExec, canRead, canWrite, ws, sp, yamlTools);
        RegisterExploreTools(tools, name, ws);
        RegisterTextProcessingTools(tools, name, canRead, ws);
        RegisterDelegationTools(tools, name, sp);
        RegisterSessionLineageTools(tools, name, sp);
        RegisterBuildAndPublishTools(tools, name, ws, canExec, yamlTools);
        RegisterSandboxTools(tools, name, ws, yamlTools, sp);
        RegisterCommunicationTools(tools, name, httpFactory, yamlTools, sp);

        // EIA tools — in optional LTAI.Agent.Eia project (modularized)

        // Universal tools (all agents)
        {
            var qt = sp.GetRequiredService<LTAI.Agent.Tools.QuestionTool>();
            tools.Add(AIFunctionFactory.Create(qt.AskQuestions));
        }
        {
            var kat = sp.GetRequiredService<LTAI.Agent.Tools.KnowledgeAssetTool>();
            tools.Add(AIFunctionFactory.Create(kat.WikiCommit));
            tools.Add(AIFunctionFactory.Create(kat.WikiSearch));
            tools.Add(AIFunctionFactory.Create(kat.WikiList));
            tools.Add(AIFunctionFactory.Create(kat.WikiExtract));
        }

        // Safety guardrail (optional — skip for local dev to reduce latency)
        var safety = BuildSafetyCoordinator(sp, opts, log, name);

        // LTAI does NOT use MAF's ShellEnvironmentProvider:
        // - It starts a persistent PowerShell process via LocalShellExecutor, which hangs
        //   on Windows .NET 10 preview during InitializeAsync (60+ seconds).
        // - LTAI has its own EnvironmentProvider (line below) + SafeShellTool + WasmtimeSandbox,
        //   so MAF's auto shell-context probing is redundant.
        // The variable is kept as null so AIContextProviders can be updated in one place.

        ApplyEnvironmentConfig(opts);
        var steerLlmVerify = sp.GetKeyedService<IChatClient>("steer");
        var compaction = BuildCompactionProvider(llm, steerLlmVerify, opts, loggerFactory);

        // KB & Code graphs for context augmentation (SQLite FTS5 + CTE)
        var kbGraph = sp.GetRequiredService<KbGraph>();
        var codeGraph = sp.GetRequiredService<CgGraph>();
        var codeChunkIndex = sp.GetRequiredService<LTAI.Agent.Indexing.CodeChunkIndex>();

        // Wasmtime sandbox: WASM-based code execution with WASI capability restrictions (DI Singleton).
        var wasmtimeSandbox = sp.GetRequiredService<WasmtimeSandbox>();

        // Skills provider: loads SKILL.md from skills/
        var skillDirs = ResolveSkillDirectories();
        var skillsBuilder = new Microsoft.Agents.AI.AgentSkillsProviderBuilder()
            .UseFileSkills([.. skillDirs]);

        var skillsProvider = skillsBuilder
            .UseFileScriptRunner(LTAI.Agent.Tools.SkillScriptRunner.RunAsync)
            .UseOptions(o =>
            {
                o.ScriptApproval = true;
                 o.SkillsInstructionPrompt =
                    """
                    你拥有领域专精技能（skills），每个技能采用三层渐进式披露（Three-Layer Progressive Disclosure）：

                    <available_skills>
                    {skills}
                    </available_skills>

                    ### 三层加载策略
                    - **L1（上表中可见）**: name + description → 判断技能是否相关，零成本
                    - **L2**: 加载后 `## 概要` 节 → 了解核心步骤和关键参数（load_skill 后自动加载）
                    - **L3**: 完整技能正文 → 包含示例、边界情况、输出格式（继续阅读即可）

                    当任务匹配某个技能的领域时：
                    1. 先用 description 判断相关度（L1）
                    2. 用 `load_skill` 加载技能（示例：load_skill("code-review")），阅读 `## 概要` 节（L2）
                    3. 如需深入了解，继续阅读后续章节（L3）
                    4. 如果技能声明了 allowedTools，请优先使用这些工具
                    {resource_instructions}
                    {script_instructions}
                    只加载所需技能，不要全部加载。
                    """;
            })
            .Build();

        // ── Plan mode 特殊处理 ──
        var isPlanMode = name == AgentNames.Chat;
        if (isPlanMode)
        {
            tools = new ToolSet();
            tools.Add(AIFunctionFactory.Create(LTAI.Agent.Tools.PlanTools.PlanExit));
            tools.Add(AIFunctionFactory.Create((Func<string, string, string, Task<string>>)(LTAI.Agent.Tools.PlanTools.SubmitPlan)));
            tools.Add(AIFunctionFactory.Create((Func<Task<string>>)(LTAI.Agent.Tools.PlanTools.ApprovePlan)));
            tools.Add(AIFunctionFactory.Create(LTAI.Agent.Tools.PlanTools.PlanStatus));
            if (canRead && mmapProvider != null)
            {
                var planFs = new FileSystemTools(ws, mmapProvider, writeBuf);
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
        var palaceStore = BuildPalaceStore(embedder, opts, loggerFactory);

        // L0: Identity (~100t, always loaded).
        var identityText = ResolveIdentity(opts);

        RegisterMemoryTools(tools, canWrite, palaceStore, ws, yamlTools, sp);

        // MCP (Model Context Protocol) client tools: lazy-loaded on first invocation.
        if (!isPlanMode)
            RegisterMcpTools(tools, name, sp, opts, canRead, canWrite, canExec);

        // Semantic code search tool (cocoindex-inspired AST chunk index).
        // Available for all canRead agents (not in Plan Mode — no AST index in read-only mode).
        if (canRead && !isPlanMode)
            tools.Add(AIFunctionFactory.Create(codeChunkIndex.SemanticCodeSearch));

        RegisterDebugTools(tools, name, sp, yamlTools);
        
        // SubagentTools — registered last to capture the complete tool list for subagents
        if (!isPlanMode)
            RegisterChoiceAndSubagentTools(tools, name, sp, llm, ws, yamlTools);

        // ToolSet guarantees uniqueness at insertion time (case-insensitive name key).
        var toolList = tools.ToList();

        // Review & ParallelReview — registered last once toolList is complete
        if (!isPlanMode)
            RegisterReviewTools(tools, name, ws, yamlTools, palaceStore, sp, guardedLlm, toolList);

        // P2: Register tools in the central AgentToolStore (MAF-aligned tool discovery).
        sp.GetService<AgentToolStore>()?.RegisterRange(name, toolList);

        AIAgent agent = guardedLlm.AsHarnessAgent(
            options: new HarnessAgentOptions
            {
                MaxContextWindowTokens = opts.AI.ContextWindowSize,
                MaxOutputTokens = opts.AI.MaxTokens,
                Name = name,
                // P10.2: Chinese harness instructions replacing the default English
                // block. Default is Chinese; switches to English when OS language is en-US.
                // Uses LTAI.Core.I18n.Locale for culture-aware string selection.
                HarnessInstructions = isPlanMode
                    ? null  // plan mode keeps the default
                    : AgentPromptBuilder.AppendAgentPrompt(
                        AgentPromptBuilder.InjectVariables(AgentPromptBuilder.BuildSystemPrompt(), GetPromptVariables(name, ws)),
                        agentPrompt),
                Description = isPlanMode
                    ? AgentPromptBuilder.BuildPlanModePrompt()
                    : AgentPromptBuilder.BuildAgentDescription(name, description),
                ChatOptions = new ChatOptions
                {
                    Temperature = temperature ?? (float)opts.AI.Temperature,
                    TopP = topP ?? 0.95f,
                    MaxOutputTokens = opts.AI.MaxTokens,
                    Tools = toolList,
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
                AIContextProviders = sp.GetRequiredService<AgentContextProviderBuilder>().Build(name, identityText,
                    compaction, kbGraph, codeGraph, codeChunkIndex, wasmtimeSandbox,
                    embedder, palaceStore, identityText, modelId, skillsProvider, safety),

                 // ── Disable MAF defaults LTAI doesn't need ────────────────────
                // LTAI uses its own 7-layer memory palace (PalaceStore + AIContextProviders).
                DisableFileMemory = true,
                // MAF FileAccessProvider provides sandboxed file storage (.sandbox/ dir).
                // LTAI also registers FileSystemTools for direct workspace file operations.
                FileAccessStore = new FileSystemAgentFileStore(
                    Path.Combine(ws, ".sandbox", "file-access")),
                // MAF AgentModeProvider: plan (interactive) / execute (autonomous) / chat (free-form).
                // LTAI's own edit-mode (review/auto) is separate — controlled via /mode command.
                AgentModeProviderOptions = new AgentModeProviderOptions
                {
                    Modes =
                    [
                        new("plan", "交互式规划模式 — 分析需求、分解任务、制定计划，用户确认后执行"),
                        new("execute", "自主执行模式 — 执行已批准的计划，不向用户提问"),
                        new("chat", "自由对话模式 — 无约束的日常问答，不需要计划"),
                    ],
                    DefaultMode = "chat",
                },
                // LTAI doesn't surface web search to its agents.
                DisableWebSearch = true,
                // LTAI has its own AgentSkillsProvider (the one passed above), pre-configured
                // with script approval + custom instructions. Don't double-register MAF's.
                DisableAgentSkillsProvider = true,

                // MAF ToolApprovalAgent handles tool-level approval centrally
                // (replacing per-tool confirm parameters).
                // Use the per-agent source name so /health and DevUI can identify spans.
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

    private static Dictionary<string, string> GetPromptVariables(string name, string ws)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["workspace"] = ws,
            ["agent_name"] = name,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        };
    }

    /// <summary>Check if a YAML tool category is present. Returns true for unknown categories (forward compat).</summary>
    private static bool HasYamlTool(string[]? yamlTools, string category)
        => yamlTools == null || yamlTools.Length == 0
            || yamlTools.Contains(category, StringComparer.OrdinalIgnoreCase);
}
