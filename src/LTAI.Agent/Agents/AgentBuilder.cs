using System.Text.Json;
using LTAI.Agent.Context;
using LTAI.Agent.Memory;
using LTAI.Agent.Services;
using LTAI.Agent.Tools;
using LTAI.Agent.Tools.Review;
using LTAI.Agent.Vector;
using LTAI.Agent.Workflows;
using LTAI.AI;
using LTAI.AI.Compaction;
using LTAI.Core.Configuration;
using LTAI.Core.Safety;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
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
internal static class AgentBuilder
{
    // Shared LSP manager across all agents (process-wide)
    private static readonly LanguageServer.LspLanguageManager s_lsp = new();
    internal static LanguageServer.LspLanguageManager GetLspManager() => s_lsp;

    public static AIAgent BuildAgent(IServiceProvider sp, string name, string description,
        bool canRead, bool canWrite, bool canList, bool canExec,
        string? modelId = null, float? temperature = null, float? topP = null)
    {
        return Task.Run(() => BuildAgentImpl(sp, name, description, canRead, canWrite, canList, canExec, modelId, temperature, topP)).GetAwaiter().GetResult();
    }

    public static async Task<AIAgent> BuildAgentImpl(IServiceProvider sp, string name, string description,
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

        // ═══ Open Code Review-inspired review tooling ═══
        // Deterministic engineering: grouping, rules, position repair, reflection
        if (name is "LTAI-Chat" or "LTAI-Review" or "LTAI-Code" or "LTAI-Writer")
        {
            var review = new ReviewTools(ws);
            tools.Add(AIFunctionFactory.Create(review.LoadReviewRules));
            tools.Add(AIFunctionFactory.Create(review.GroupChanges));
            tools.Add(AIFunctionFactory.Create(review.MatchReviewRules));
            tools.Add(AIFunctionFactory.Create(review.RepairReviewPositions));
            tools.Add(AIFunctionFactory.Create(review.ReflectReviewQuality));
            tools.Add(AIFunctionFactory.Create(review.BuildReviewContext));
        }

        // #5 CODESKILL: skill bank for coding agents
        if (name is "LTAI-Code" or "LTAI-Frontend" or "LTAI-Chat")
        {
            var skillBank = new Tools.SkillBank();
            tools.Add(AIFunctionFactory.Create(
                (string query, string? lang) =>
                {
                    var results = skillBank.Search(query, lang, 5);
                    return results.Count > 0
                        ? string.Join("\n---\n", results.Select(s => $"{s.Name} [{s.Category}] ({s.UseCount} uses, {s.SuccessRate:P0} success)\n{s.Pattern}"))
                        : "(no matching skills found)";
                },
                "SkillBankSearch", "Search reusable code skills from past trajectories"));
            tools.Add(AIFunctionFactory.Create(
                (string name, string pattern, string lang, string cat, string pre, string post) =>
                {
                    skillBank.Register(name, pattern, lang, cat, pre, post);
                    return $"Registered skill '{name}' ({skillBank.Count} total)";
                },
                "SkillBankRegister", "Register a new code skill from a coding trajectory"));
        }

        // LSP diagnostics for MoonBit/Mojo/Cangjie — real-time without build
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Frontend")
        {
            tools.Add(AIFunctionFactory.Create(async (string filePath, string content) =>
            {
                await s_lsp.OpenFileAsync(filePath, content);
                return $"LSP opened: {filePath}";
            }, "LspOpenFile", "Open a file in its language server for real-time diagnostics"));
            tools.Add(AIFunctionFactory.Create(() =>
            {
                var diags = s_lsp.FormatDiagnostics();
                return string.IsNullOrEmpty(diags) ? "(no LSP diagnostics)" : diags;
            }, "LspGetDiagnostics", "Get current LSP diagnostics for open files"));
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
                    : AgentPromptBuilder.AppendAgentPrompt(AgentPromptBuilder.BuildSystemPrompt(), agentPrompt),
                Description = isPlanMode
                    ? AgentPromptBuilder.BuildPlanModePrompt()
                    : AgentPromptBuilder.BuildAgentDescription(name, description),
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
                AIContextProviders = AgentContextProviderBuilder.Build(sp, loggerFactory, name, identityText,
                    compaction, kbGraph, codeGraph, codeChunkIndex, wasmtimeSandbox,
                    embedder, palaceStore, identityText, modelId, skillsProvider, safety),

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
}

/// <summary>
/// P0: Minimal no-op AIAgent used when the real agent fails to build.
/// Returns a static error message so the caller can surface the failure gracefully.
/// </summary>
internal sealed class FallbackAgent : AIAgent
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
