using LTAI.AI;
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

    private static string? _stableSessionId;
    private static readonly object _sessionLock = new();

    internal static string GetStableSessionId()
    {
        if (_stableSessionId == null)
            lock (_sessionLock)
                _stableSessionId ??= $"ltai-{Environment.MachineName}-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMdd}";
        return _stableSessionId;
    }

    public static IServiceCollection AddLTAIAgent(this IServiceCollection services)
    {
        // Step 1: Build agents (no workflow dep)
        Dictionary<string, AIAgent> agents = null!;

        services.AddSingleton(sp =>
        {
            agents = BuildAllAgents(sp);
            return agents;
        });

        // Step 2: Graph / Vector stores
        var dbPath = Path.Combine(Directory.GetCurrentDirectory(), ".livingtree", "graph.db");
        var graphStore = new GraphStore(dbPath);
        services.AddSingleton(graphStore);

        // Step 2b: Knowledge/Code graph (registered in DI so lifecycle is managed)
        services.AddSingleton<KnowledgeGraph>(sp =>
        {
            var store = sp.GetRequiredService<GraphStore>();
            var embedder = sp.GetRequiredService<EmbeddingClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<KnowledgeGraph>();
            return new KnowledgeGraph(store, embedder, logger);
        });
        services.AddSingleton<CodeGraph>(sp =>
        {
            var store = sp.GetRequiredService<GraphStore>();
            var embedder = sp.GetRequiredService<EmbeddingClient>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<CodeGraph>();
            return new CodeGraph(store, embedder, logger, Directory.GetCurrentDirectory());
        });

        // Step 3: Workflow orchestrator
        services.AddSingleton<WorkflowOrchestrator>(sp =>
            new WorkflowOrchestrator(agents.Values, agents["chat"],
                sp.GetRequiredService<ILogger<WorkflowOrchestrator>>()));

        // Step 3: ChatAgent + workflow
        services.AddSingleton<ChatAgent>(sp =>
        {
            var wf = sp.GetRequiredService<WorkflowOrchestrator>();
            return new ChatAgent(BuildOrchestrator(sp, agents.Values.ToArray()), wf);
        });

        return services;
    }

    private static Dictionary<string, AIAgent> BuildAllAgents(IServiceProvider sp)
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["chat"]   = BuildAgent(sp, "LTAI-Chat",   "通用对话助手",   true, true, true, true),
            ["code"]   = BuildAgent(sp, "LTAI-Code",   "代码分析助手",   true, true, true, false),
            ["math"]   = BuildAgent(sp, "LTAI-Math",   "数学计算助手",   false, false, false, true),
            ["data"]   = BuildAgent(sp, "LTAI-Data",   "数据处理助手",   true, true, true, true),
            ["system"] = BuildAgent(sp, "LTAI-System", "系统管理助手",  false, false, false, true),
            ["llm"]    = BuildAgent(sp, "LTAI-LLM",    "纯对话助手",    false, false, false, false),
        };
    }

    private static AIAgent BuildAgent(IServiceProvider sp, string name, string description,
        bool canRead, bool canWrite, bool canList, bool canExec)
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
        if (canExec) tools.Add(new LocalShellExecutor(new LocalShellExecutorOptions
        {
            WorkingDirectory = ws,
            Timeout = TimeSpan.FromSeconds(60),
            MaxOutputBytes = 64 * 1024,
            AcknowledgeUnsafe = false,
        }).AsAIFunction(requireApproval: true));
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
        if (canRead && (name == "LTAI-Chat" || name == "LTAI-Code"))
        {
            tools.Add(AIFunctionFactory.Create(codeAnalysis.GetSymbols));
            tools.Add(AIFunctionFactory.Create(codeAnalysis.FindInCode));
        }

        // EIA (Environmental Impact Assessment) tools
        if (name is "LTAI-Chat" or "LTAI-Data" or "LTAI-System")
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
        var memory = new MemoryTools(ws);
        if (name is "LTAI-Chat" or "LTAI-System")
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
        if (name is "LTAI-Chat" or "LTAI-Code")
        {
            tools.Add(AIFunctionFactory.Create(PlanTools.SubmitPlan));
            tools.Add(AIFunctionFactory.Create(PlanTools.MarkStepComplete));
            tools.Add(AIFunctionFactory.Create(PlanTools.RevisePlan));
            tools.Add(AIFunctionFactory.Create(PlanTools.PlanStatus));
        }

        // Flowchart / diagram tools (Mermaid + SVG)
        if (name is "LTAI-Chat" or "LTAI-Code" or "LTAI-Data")
        {
            var diagram = new FlowchartTools(httpFactory);
            tools.Add(AIFunctionFactory.Create(diagram.Flowchart));
            tools.Add(AIFunctionFactory.Create(diagram.SequenceDiagram));
            tools.Add(AIFunctionFactory.Create(diagram.ClassDiagram));
            tools.Add(AIFunctionFactory.Create(diagram.GanttChart));
            tools.Add(AIFunctionFactory.Create(diagram.ErDiagram));
        }

        // Choice/selection tool
        if (name == "LTAI-Chat")
        {
            tools.Add(AIFunctionFactory.Create(ChoiceTools.AskChoice));
        }

        // Subagent tools (explore, research, review, spawn_subagent)
        if (name == "LTAI-Chat")
        {
            var sub = new SubagentTools(sp, llm, ws);
            tools.Add(AIFunctionFactory.Create(sub.Explore));
            tools.Add(AIFunctionFactory.Create(sub.Research));
            tools.Add(AIFunctionFactory.Create(sub.Review));
            tools.Add(AIFunctionFactory.Create(sub.SecurityReview));
            tools.Add(AIFunctionFactory.Create(sub.SpawnSubagent));
        }

        // Git tools (LibGit2Sharp, no CLI)
        if (name is "LTAI-Chat" or "LTAI-Code" or "LTAI-System")
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
        if (name is "LTAI-Chat" or "LTAI-System" or "LTAI-Code")
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
        if (name is "LTAI-Chat" or "LTAI-Data" or "LTAI-System")
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
        if (name is "LTAI-Chat" or "LTAI-System")
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

        // Workflow tools (lazy-resolve via IServiceProvider to avoid circular DI)
        if (name == "LTAI-Chat")
        {
            var wfTools = new WorkflowTools(sp);
            tools.Add(AIFunctionFactory.Create(wfTools.WorkflowHandoff));
            tools.Add(AIFunctionFactory.Create(wfTools.WorkflowSequential));
            tools.Add(AIFunctionFactory.Create(wfTools.WorkflowConcurrent));
        }

        var safetyKey = Environment.GetEnvironmentVariable(opts.AI.ApiKeyEnv ?? "DEEPSEEK_API_KEY") ?? "";
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

        var compaction = new CompactionProvider(
            new PipelineCompactionStrategy(
                new ContextWindowCompactionStrategy(64000, opts.AI.MaxTokens),
                new SummarizationCompactionStrategy(llm, CompactionTriggers.TokensExceed(64000), 2)
            ), loggerFactory: loggerFactory);

        // KB & Code graphs for context augmentation (resolved from DI for lifecycle management)
        var kbGraph = sp.GetRequiredService<KnowledgeGraph>();
        var codeGraph = sp.GetRequiredService<CodeGraph>();

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
                Temperature = (float)opts.AI.Temperature,
                MaxOutputTokens = opts.AI.MaxTokens,
                Tools = tools,
            },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            AIContextProviders = [shellEnv, safety, compaction, kbGraph, codeGraph],
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
