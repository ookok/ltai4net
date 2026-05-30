using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Core.Safety;
using LTAI.Agent.Tools;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.Core.Configuration;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LTAI.Agent;

public static class ServiceCollectionExtensions
{
#pragma warning disable MAAI001

    public static IServiceCollection AddLTAIAgent(this IServiceCollection services)
    {
        // Step 1: Build agents (no workflow dep)
        Dictionary<string, AIAgent> agents = null!;

        services.AddSingleton(sp =>
        {
            agents = BuildAllAgents(sp);
            return agents;
        });

        // Step 2: SQLite Knowledge Graph store
        services.AddSingleton<KgStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new KgStore(opts.ResolveDataPath("kg.db"));
        });

        // Step 2b: Knowledge/Code graph providers (registered in DI so lifecycle is managed)
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
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<CgGraph>();
            return new CgGraph(store, llm, logger, Directory.GetCurrentDirectory());
        });

        // Step 3: Workflow orchestrator (with optional vector routing)
        services.AddSingleton<WorkflowOrchestrator>(sp =>
            new WorkflowOrchestrator(agents.Values, agents["chat"],
                sp.GetRequiredService<ILogger<WorkflowOrchestrator>>(),
                sp.GetService<EmbeddingClient>()));

        // Step 3b: Token budget tracker (from AI config, optional)
        services.AddSingleton<LTAI.AI.BudgetTracker>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LTAIOptions>>().Value;
            return new LTAI.AI.BudgetTracker(
                globalMax: opts.AI.GlobalTokenBudget,
                perUserMax: opts.AI.PerUserTokenBudget);
        });

        // Step 3c: ChatAgent + workflow (default L1=flash, auto-upgrade to L2=pro)
        services.AddSingleton<ChatAgent>(sp =>
        {
            var wf = sp.GetRequiredService<WorkflowOrchestrator>();
            var chat = BuildOrchestrator(sp, agents.Values.ToArray());
            // Pro agent for complex task auto-upgrade (uses "deepseek-pro" provider)
            var proAgent = agents.TryGetValue("chat-pro", out var p) ? p : chat;
            var budget = sp.GetService<LTAI.AI.BudgetTracker>();
            return new ChatAgent(chat, proAgent, wf, budget);
        });

        return services;
    }

    private static Dictionary<string, AIAgent> BuildAllAgents(IServiceProvider sp)
    {
        // Try loading from agents/*.agent.md files first
        var fileDefs = AgentRegistry.LoadAll();
        if (fileDefs.Count > 0)
        {
            var result = new Dictionary<string, AIAgent>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in fileDefs)
            {
                var key = def.Name?.ToLowerInvariant().Replace("ltai-", "") ?? "unknown";
                var canRead = def.Permissions.Contains("read");
                var canWrite = def.Permissions.Contains("write");
                var canList = def.Permissions.Contains("list");
                var canExec = def.Permissions.Contains("exec");
                result[key] = BuildAgent(sp, def.Name ?? key, def.Description,
                    canRead, canWrite, canList, canExec,
                    modelId: def.ModelId,
                    temperature: (float)def.Temperature,
                    topP: (float)def.TopP);
            }
            return result;
        }

        // Fallback: hardcoded defaults (no agents/*.agent.md files found)
        return new(StringComparer.OrdinalIgnoreCase)
        {
            // 任务类型 → temperature/topP 参考：AI编程 0.3/0.95 | 工具调用 0.3/0.95 | 通用问答 0.8/0.95 | 数学推理 1.0/0.95
            ["chat"]     = BuildAgent(sp, "LTAI-Chat",   "通用对话助手",     true, true, true, true, temperature: 0.3f, topP: 0.95f),
            ["chat-pro"] = BuildAgent(sp, "LTAI-Chat-Pro","深度推理助手(Pro)",true, true, true, true, modelId: "deepseek-pro", temperature: 0.3f, topP: 0.95f),
            ["code"]     = BuildAgent(sp, "LTAI-Code",   "代码分析助手",     true, true, true, false, temperature: 0.3f, topP: 0.95f),
            ["math"]     = BuildAgent(sp, "LTAI-Math",   "数学计算助手",     false, false, false, true, temperature: 1.0f, topP: 0.95f),
            ["data"]     = BuildAgent(sp, "LTAI-Data",   "数据处理助手",     true, true, true, true, temperature: 0.3f, topP: 0.95f),
            ["system"]   = BuildAgent(sp, "LTAI-System", "系统管理助手",    false, false, false, true, temperature: 0.3f, topP: 0.95f),
            ["llm"]      = BuildAgent(sp, "LTAI-LLM",    "纯对话助手",      false, false, false, false, temperature: 0.8f, topP: 0.95f),
            ["writer"]   = BuildAgent(sp, "LTAI-Writer", "创意写作助手",     true, true, true, true, temperature: 0.8f, topP: 0.95f),
            ["frontend"] = BuildAgent(sp, "LTAI-Frontend","前端网页开发助手", true, true, true, true, temperature: 0.8f, topP: 0.95f),
        };
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
        return BuildAgentImpl(sp, name, description, canRead, canWrite, canList, canExec, modelId, temperature, topP);
    }

    // Original implementation
    private static AIAgent BuildAgentImpl(IServiceProvider sp, string name, string description,
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
        var edit = new EditFileTools(ws);
        var multiEdit = new MultiEditTools(ws);
        var dirTree = new DirectoryTreeTools(ws);
        var glob = new GlobTools(ws);

        if (canRead) tools.Add(AIFunctionFactory.Create(fs.ReadFile));
        if (canWrite) tools.Add(AIFunctionFactory.Create(fs.WriteFile));
        if (canList)
        {
            tools.Add(AIFunctionFactory.Create(fs.ListFiles));
            tools.Add(AIFunctionFactory.Create(dirTree.DirectoryTree));
            tools.Add(AIFunctionFactory.Create(glob.Glob));
        }
        if (canExec)
        {
            tools.Add(AIFunctionFactory.Create(new SafeShellTool(ws).RunCommand));
        }
        if (canRead && canWrite)
        {
            tools.Add(AIFunctionFactory.Create(edit.EditFile));
            tools.Add(AIFunctionFactory.Create(multiEdit.MultiEdit));
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
        if (canRead && (name is "LTAI-Chat" or "LTAI-Code" or "LTAI-Frontend"))
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

        // Web tools
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var web = new WebTools(httpFactory, sp.GetService<ILogger<WebTools>>());
        if (name == "LTAI-Chat" || name == "LTAI-Data")
        {
            tools.Add(AIFunctionFactory.Create(web.WebSearch));
            tools.Add(AIFunctionFactory.Create(web.WebFetch));
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

        // Office tools (Excel/Word via DocumentFormat.OpenXml)
        var office = new OfficeTools(ws);
        if (canRead && canWrite)
        {
            tools.Add(AIFunctionFactory.Create(office.ExcelRead));
            tools.Add(AIFunctionFactory.Create(office.ExcelWrite));
            tools.Add(AIFunctionFactory.Create(office.ExcelCopyRange));
            tools.Add(AIFunctionFactory.Create(office.WordRead));
            tools.Add(AIFunctionFactory.Create(office.WordWrite));
        }

        // Memory tools (persistent memory across sessions)
        var memDir = opts.ResolveDataPath("memories");
        var memory = new MemoryTools(ws, memDir);
        if (name is "LTAI-Chat" or "LTAI-System" or "LTAI-Writer")
        {
            tools.Add(AIFunctionFactory.Create(memory.Remember));
            tools.Add(AIFunctionFactory.Create(memory.Forget));
            tools.Add(AIFunctionFactory.Create(memory.RecallMemory));
            tools.Add(AIFunctionFactory.Create(memory.ListMemories));
        }

        // Filesystem CRUD tools (copy, move, delete, info)
        var fileOps = new FileTools(ws);
        if (canRead && canWrite)
        {
            tools.Add(AIFunctionFactory.Create(fileOps.CopyFile));
            tools.Add(AIFunctionFactory.Create(fileOps.MoveFile));
            tools.Add(AIFunctionFactory.Create(fileOps.DeleteFile));
            tools.Add(AIFunctionFactory.Create(fileOps.DeleteDirectory));
            tools.Add(AIFunctionFactory.Create(fileOps.GetFileInfo));
        }

        // Plan approval workflow tools
        if (name is "LTAI-Chat" or "LTAI-Code" or "LTAI-Writer" or "LTAI-Frontend")
        {
            tools.Add(AIFunctionFactory.Create(PlanTools.SubmitPlan));
            tools.Add(AIFunctionFactory.Create(PlanTools.MarkStepComplete));
            tools.Add(AIFunctionFactory.Create(PlanTools.RevisePlan));
            tools.Add(AIFunctionFactory.Create(PlanTools.PlanStatus));
        }

        // Flowchart / diagram tools (Mermaid + SVG)
        if (name is "LTAI-Chat" or "LTAI-Code" or "LTAI-Data" or "LTAI-Writer" or "LTAI-Frontend")
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
            var sub = new SubagentTools(sp, llm, ws);
            tools.Add(AIFunctionFactory.Create(sub.Explore));
            tools.Add(AIFunctionFactory.Create(sub.Research));
            tools.Add(AIFunctionFactory.Create(sub.Review));
            tools.Add(AIFunctionFactory.Create(sub.SecurityReview));
            tools.Add(AIFunctionFactory.Create(sub.SpawnSubagent));
        }

        // Git tools (LibGit2Sharp, no CLI)
        if (name is "LTAI-Chat" or "LTAI-Code" or "LTAI-System" or "LTAI-Writer" or "LTAI-Frontend")
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

        // Job & Task management tools
        if (name is "LTAI-Chat" or "LTAI-System" or "LTAI-Code" or "LTAI-Writer" or "LTAI-Frontend")
        {
            tools.Add(AIFunctionFactory.Create(TaskTools.TodoWrite));
            tools.Add(AIFunctionFactory.Create(TaskTools.TodoComplete));
            tools.Add(AIFunctionFactory.Create(TaskTools.TodoList));
        }
        if (canExec)
        {
            var jobs = new JobTools();
            tools.Add(AIFunctionFactory.Create(jobs.StartJob));
            tools.Add(AIFunctionFactory.Create(JobTools.ListJobs));
            tools.Add(AIFunctionFactory.Create(JobTools.GetJobOutput));
            tools.Add(AIFunctionFactory.Create(jobs.WaitForJob));
            tools.Add(AIFunctionFactory.Create(JobTools.StopJob));
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

        // System & Network tools
        if (name is "LTAI-Chat" or "LTAI-System" or "LTAI-Writer")
        {
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
        }

        // Container tools (Docker sandbox)
        var container = new ContainerTools();
        if (canExec)
        {
            tools.Add(AIFunctionFactory.Create(container.RunInContainer));
            tools.Add(AIFunctionFactory.Create(container.RunWithNetwork));
            tools.Add(AIFunctionFactory.Create(ContainerTools.CheckDockerAsync));
        }

        // File download tool (confirm=true 才下载)
        if (canRead && canWrite && name is "LTAI-Chat" or "LTAI-Code" or "LTAI-Writer" or "LTAI-Frontend")
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

        var safetyKey = LTAI.Core.Configuration.SecretManager.Get(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY") ?? "";
        var safetyHttp = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        var safetyClient = new OpenAiHttpClient(safetyHttp, "https://api.deepseek.com/v1", "deepseek-chat", safetyKey);
        var safety = new SafetyCoordinator(safetyClient, loggerFactory.CreateLogger<SafetyCoordinator>());

        var shellEnv = new ShellEnvironmentProvider(
            new LocalShellExecutor(new LocalShellExecutorOptions
            {
                WorkingDirectory = ws,
                Timeout = TimeSpan.FromSeconds(10),
            }),
            new ShellEnvironmentProviderOptions { ProbeTimeout = TimeSpan.FromSeconds(5) });

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

        var skillsProvider = new Microsoft.Agents.AI.AgentSkillsProviderBuilder()
            .UseFileSkills([skillsDir])
            .UseFileScriptRunner(LTAI.Agent.Tools.SkillScriptRunner.RunAsync)
            .Build();

        AIAgent agent = new ChatClientAgent(llm, new ChatClientAgentOptions
        {
            Name = name,
            // NOTE: No DateTime.Now — timestamps destroy prefix caching.
            Description = $"你是 {name}，{description}。\n"
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
            AIContextProviders = [shellEnv, safety, compaction, kbGraph, codeGraph, wasmtimeSandbox, skillsProvider],
            EnableMessageInjection = true,
            RequirePerServiceCallChatHistoryPersistence = true,
        }, loggerFactory, sp);

        agent = new LoggingAgent(agent, log);
        agent = new ToolApprovalAgent(agent);
        agent = new OpenTelemetryAgent(agent, $"LTAI.{name}", autoWireChatClient: true);
        return agent;
    }

    private static AIAgent BuildOrchestrator(IServiceProvider sp, AIAgent[] agents)
    {
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("LTAI.Orchestrator");
        log.LogInformation("Orchestrator ready with {Count} agents: {Agents}",
            agents.Length, string.Join(", ", agents.Select(a => a.Name)));
        return agents[0];
    }
}
