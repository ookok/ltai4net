using LTAI.Agent.LanguageServer;
using LTAI.Agent.Memory;
using LTAI.Agent.Services;
using LTAI.Agent.Tools;
using LTAI.Agent.Tools.Review;
using LTAI.Agent.Vector;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

partial class AgentBuilder
{
    static void RegisterFileAndTextTools(List<AITool> tools, string name, bool canRead, bool canWrite, bool canList, bool canExec, string ws,
        Caching.MmapFileProvider? mmap = null, Caching.WriteBuffer? writeBuf = null)
    {
        var fs = new FileSystemTools(ws, mmap, writeBuf);
        var text = new TextTools(ws);
        if (canRead) tools.Add(AIFunctionFactory.Create((string path) => fs.ReadFileContent(path), "ReadFileContent", "Read a file"));
        if (canWrite) tools.Add(AIFunctionFactory.Create(fs.WriteFile));
        if (canList) { tools.Add(AIFunctionFactory.Create(fs.Glob)); }
        if (canRead && canWrite)
        {
            tools.Add(AIFunctionFactory.Create(fs.CopyFile));
            tools.Add(AIFunctionFactory.Create(fs.MoveFile));
            tools.Add(AIFunctionFactory.Create(fs.DeleteFile));
            tools.Add(AIFunctionFactory.Create(fs.DeleteDirectory));
            tools.Add(AIFunctionFactory.Create(fs.GetFileInfo));
        }
        if (canExec) tools.Add(AIFunctionFactory.Create(new SafeShellTool(ws).RunCommand));
        if (canRead && canWrite) { tools.Add(AIFunctionFactory.Create(text.EditFile)); tools.Add(AIFunctionFactory.Create(text.MultiEdit)); }
        if (canRead) tools.Add(AIFunctionFactory.Create(TextTools.RegexTest));
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Review" or "LTAI-Writer")
            tools.Add(AIFunctionFactory.Create(TextTools.DiffFiles));
    }

    static void RegisterSearchAndCodeAnalysisTools(List<AITool> tools, string name, bool canRead, string ws)
    {
        var search = new SearchTools(ws);
        var codeAnalysis = new CodeAnalysisTools(ws);
        if (canRead) { tools.Add(AIFunctionFactory.Create(search.SearchContent)); tools.Add(AIFunctionFactory.Create(search.SearchFiles)); }
        if (canRead && (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Frontend"))
        { tools.Add(AIFunctionFactory.Create(codeAnalysis.GetSymbols)); tools.Add(AIFunctionFactory.Create(codeAnalysis.FindInCode)); }
    }

    static void RegisterWebTools(List<AITool> tools, string name, IHttpClientFactory httpFactory)
    {
        var web = new WebTools(httpFactory, null);
        if (name.StartsWith("LTAI-Chat") || name == "LTAI-Data")
        { tools.Add(AIFunctionFactory.Create(web.WebSearch)); tools.Add(AIFunctionFactory.Create(web.WebFetch)); tools.Add(AIFunctionFactory.Create(web.HttpRequest)); }
    }

    static void RegisterMultimediaTools(List<AITool> tools, bool canRead, bool canExec, string ws)
    {
        var media = new MultimediaTools(ws);
        if (canRead)
        { tools.Add(AIFunctionFactory.Create(media.ImageInfo)); tools.Add(AIFunctionFactory.Create(media.ImageResize)); tools.Add(AIFunctionFactory.Create(media.ImageConvert)); tools.Add(AIFunctionFactory.Create(media.MediaInfo)); tools.Add(AIFunctionFactory.Create(media.AudioConvert)); }
        if (canExec) tools.Add(AIFunctionFactory.Create(media.Screenshot));
    }

    static void RegisterDocumentTools(List<AITool> tools, bool canRead, bool canWrite, string ws, IServiceProvider sp)
    {
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
    }

    static void RegisterPlanAndDiagramTools(List<AITool> tools, string name, IHttpClientFactory httpFactory)
    {
        // Plan/todo tools now come from MAF's TodoProvider + AgentModeProvider (auto-injected by harness).
        // Only flowchart/diagram tools are registered here.
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Data" or "LTAI-Writer" or "LTAI-Frontend")
        {
            var diagram = new FlowchartTools(httpFactory);
            tools.Add(AIFunctionFactory.Create(diagram.Flowchart));
            tools.Add(AIFunctionFactory.Create(diagram.SequenceDiagram));
            tools.Add(AIFunctionFactory.Create(diagram.ClassDiagram));
            tools.Add(AIFunctionFactory.Create(diagram.GanttChart));
            tools.Add(AIFunctionFactory.Create(diagram.ErDiagram));
        }
    }

    static void RegisterChoiceAndSubagentTools(List<AITool> tools, string name, IServiceProvider sp, IChatClient llm, string ws)
    {
        if (name is "LTAI-Chat" or "LTAI-Writer" or "LTAI-Frontend")
        { tools.Add(AIFunctionFactory.Create(ChoiceTools.AskChoice)); }
        if (name is "LTAI-Chat" or "LTAI-Writer" or "LTAI-Frontend")
        {
            var sub = new SubagentTools(sp, llm, ws, tools);
            tools.Add(AIFunctionFactory.Create(sub.Explore));
            tools.Add(AIFunctionFactory.Create(sub.Research));
            tools.Add(AIFunctionFactory.Create(sub.Review));
            tools.Add(AIFunctionFactory.Create(sub.SecurityReview));
            tools.Add(AIFunctionFactory.Create(sub.SpawnSubagent));
        }
        if (name is "LTAI-Chat" or "LTAI-Writer")
        {
            var gen = new AgentGenerator(llm);
            tools.Add(AIFunctionFactory.Create(gen.GenerateAgent));
        }
    }

    static void RegisterGitTools(List<AITool> tools, string name, string ws)
    {
        if (!(name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-System" or "LTAI-Writer" or "LTAI-Frontend")) return;
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

    static void RegisterReviewTools(List<AITool> tools, string name, string ws)
    {
        if (name is not ("LTAI-Chat" or "LTAI-Review" or "LTAI-Code" or "LTAI-Writer")) return;
        var review = new ReviewTools(ws);
        tools.Add(AIFunctionFactory.Create(review.LoadReviewRules));
        tools.Add(AIFunctionFactory.Create(review.GroupChanges));
        tools.Add(AIFunctionFactory.Create(review.MatchReviewRules));
        tools.Add(AIFunctionFactory.Create(review.RepairReviewPositions));
        tools.Add(AIFunctionFactory.Create(review.ReflectReviewQuality));
        tools.Add(AIFunctionFactory.Create(review.BuildReviewContext));
    }

    static void RegisterSkillBankTools(List<AITool> tools, string name)
    {
        if (name is not ("LTAI-Code" or "LTAI-Frontend" or "LTAI-Chat")) return;
        var skillBank = new SkillBank();
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
            (string name_, string pattern, string lang, string cat, string pre, string post) =>
            {
                skillBank.Register(name_, pattern, lang, cat, pre, post);
                return $"Registered skill '{name_}' ({skillBank.Count} total)";
            },
            "SkillBankRegister", "Register a new code skill from a coding trajectory"));
    }

    static void RegisterLspTools(List<AITool> tools, string name)
    {
        if (!(name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Frontend")) return;
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

    static void RegisterTaskTools(List<AITool> tools, string name)
    {
        if (!(name.StartsWith("LTAI-Chat") || name is "LTAI-System" or "LTAI-Code" or "LTAI-Writer" or "LTAI-Frontend")) return;
        tools.Add(AIFunctionFactory.Create(TaskTools.TodoWrite));
        tools.Add(AIFunctionFactory.Create(TaskTools.TodoComplete));
        tools.Add(AIFunctionFactory.Create(TaskTools.TodoList));
    }

    static void RegisterIntegrationTools(List<AITool> tools, string name, IHttpClientFactory httpFactory)
    {
        if (name is not ("LTAI-Chat" or "LTAI-Data" or "LTAI-System" or "LTAI-Writer" or "LTAI-Frontend")) return;
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

    static void RegisterSystemAndJobTools(List<AITool> tools, string name, bool canExec, bool canRead, bool canWrite, string ws, IServiceProvider sp)
    {
        if (name is "LTAI-Chat" or "LTAI-Chat-Pro" or "LTAI-System" or "LTAI-Writer")
        {
            tools.Add(AIFunctionFactory.Create(SystemTools.GetCurrentDateTime));
            tools.Add(AIFunctionFactory.Create(SystemTools.SystemInfo));
            tools.Add(AIFunctionFactory.Create(SystemTools.ListDirectory));
            tools.Add(AIFunctionFactory.Create(SystemTools.ListProcesses));
            tools.Add(AIFunctionFactory.Create(SystemTools.GetEnv));
            tools.Add(AIFunctionFactory.Create(SystemTools.NetworkInterfaces));
            tools.Add(AIFunctionFactory.Create(SystemTools.NetworkDiag));
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
        if (name is "LTAI-Chat" or "LTAI-Chat-Pro" or "LTAI-System" or "LTAI-Code" or "LTAI-Writer")
        {
            var tq = sp.GetRequiredService<TaskQueueTool>();
            tools.Add(AIFunctionFactory.Create(tq.EnqueueTask));
            tools.Add(AIFunctionFactory.Create(tq.ListTasks));
            tools.Add(AIFunctionFactory.Create(tq.GetTask));
            tools.Add(AIFunctionFactory.Create(tq.WaitForTask));
            tools.Add(AIFunctionFactory.Create(tq.CancelTask));
        }
        if (canExec)
        {
            var sys = new SystemTools();
            tools.Add(AIFunctionFactory.Create(sys.RunInContainer));
            tools.Add(AIFunctionFactory.Create(sys.RunWithNetwork));
            tools.Add(AIFunctionFactory.Create(sys.CheckDockerAsync));
        }
        if (canRead && canWrite && (name.StartsWith("LTAI-Chat") || name is "LTAI-Code" or "LTAI-Writer" or "LTAI-Frontend"))
            tools.Add(AIFunctionFactory.Create(FileDownloadTool.DownloadFile));
    }

    static void RegisterWorkflowTools(List<AITool> tools, string name, IServiceProvider sp)
    {
        if (name is not ("LTAI-Chat" or "LTAI-Writer" or "LTAI-Frontend")) return;
        var wfTools = new WorkflowTools(sp);
        tools.Add(AIFunctionFactory.Create(wfTools.WorkflowHandoff));
        tools.Add(AIFunctionFactory.Create(wfTools.WorkflowSequential));
        tools.Add(AIFunctionFactory.Create(wfTools.WorkflowConcurrent));
    }

    static void RegisterClusterAndDeepenTools(List<AITool> tools, string name, IServiceProvider sp)
    {
        if (name is not ("LTAI-Chat" or "LTAI-Chat-Pro" or "LTAI-System" or "LTAI-Writer" or "LTAI-Data")) return;
        var cs = sp.GetRequiredService<ClusterSummarizer>();
        tools.Add(AIFunctionFactory.Create(cs.SummarizeAsync));
        var dst = sp.GetRequiredService<DeepenSearchTool>();
        tools.Add(AIFunctionFactory.Create(dst.DeepenSearchAsync));
    }

    static void RegisterNewDomainTools(List<AITool> tools, string name, bool canExec, bool canRead, bool canWrite, string ws, IServiceProvider sp)
    {
        if (canExec)
        {
            var archive = new ArchiveTools(ws);
            tools.Add(AIFunctionFactory.Create(archive.ArchiveCreate));
            tools.Add(AIFunctionFactory.Create(archive.ArchiveExtract));
        }
        if (canRead && canWrite)
        {
            var chart = new ChartTools(ws);
            tools.Add(AIFunctionFactory.Create(chart.ChartCreate));
        }
        if (name is "LTAI-Chat" or "LTAI-Data" or "LTAI-Code")
        {
            var db = new DatabaseTools();
            tools.Add(AIFunctionFactory.Create(db.SqlQuery));
        }
        if (canRead && canWrite)
        {
            var dt = new DataTransformTools(ws);
            tools.Add(AIFunctionFactory.Create(dt.JsonQuery));
            tools.Add(AIFunctionFactory.Create(dt.CsvRead));
            tools.Add(AIFunctionFactory.Create(dt.CsvWrite));
        }
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-System" or "LTAI-Security" or "LTAI-Writer")
        { tools.Add(AIFunctionFactory.Create(CryptoTools.HashFile)); tools.Add(AIFunctionFactory.Create(CryptoTools.EncryptFile)); tools.Add(AIFunctionFactory.Create(CryptoTools.DecryptFile)); }
        if (canRead) { tools.Add(AIFunctionFactory.Create(CryptoTools.Base64Encode)); tools.Add(AIFunctionFactory.Create(CryptoTools.Base64Decode)); }
        if (canRead) tools.Add(AIFunctionFactory.Create(MarkdownTools.RenderMarkdown));
        var rc = sp.GetRequiredService<RetrieveContentTool>();
        tools.Add(AIFunctionFactory.Create(rc.RetrieveContent));
    }

    static void RegisterMemoryTools(List<AITool> tools, bool canWrite, PalaceStore palaceStore, string ws)
    {
        if (!canWrite) return;
        var palaceMemory = new MemoryTools(palaceStore, defaultWing: ws != null ? Path.GetFileName(ws.TrimEnd('/', '\\')) : "project");
        tools.Add(AIFunctionFactory.Create(palaceMemory.Remember));
        tools.Add(AIFunctionFactory.Create(palaceMemory.Forget));
        tools.Add(AIFunctionFactory.Create(palaceMemory.RecallMemory));
        tools.Add(AIFunctionFactory.Create(palaceMemory.ListMemories));
    }

    static void RegisterDebugTools(List<AITool> tools, string name, IServiceProvider sp)
    {
        if (name is not ("LTAI-Chat" or "LTAI-Chat-Pro" or "LTAI-Code" or "LTAI-System")) return;
        var debugBridge = sp.GetService<LTAI.Core.Debugging.IDebugBridge>();
        if (debugBridge == null) return;
        var debug = new DebugTools(debugBridge);
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
