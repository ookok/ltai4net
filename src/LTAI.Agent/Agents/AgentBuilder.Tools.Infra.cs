using LTAI.Agent.Memory;
using LTAI.Agent.Services;
using LTAI.Agent.Tools;
using LTAI.Core.Session;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

partial class AgentBuilder
{
    static void RegisterSkillBankTools(ToolSet tools, string name, string[]? yamlTools)
    {
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "search")
            : name is not ("LTAI-Dev" or "LTAI-Chat")) return;
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

    static void RegisterTaskTools(ToolSet tools, string name, string[]? yamlTools)
    {
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "plan") || HasYamlTool(yamlTools, "task")
            : !(name.StartsWith("LTAI-Chat") || name is "LTAI-System" or "LTAI-Dev" or "LTAI-Writer" or "LTAI-Arch")) return;
        tools.Add(AIFunctionFactory.Create(TaskTools.TodoWrite));
        tools.Add(AIFunctionFactory.Create(TaskTools.TodoComplete));
        tools.Add(AIFunctionFactory.Create(TaskTools.TodoList));
    }

    static void RegisterSystemAndJobTools(ToolSet tools, string name, bool canExec, bool canRead, bool canWrite, string ws, IServiceProvider sp,
        string[]? yamlTools)
    {
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "system")
            : name is "LTAI-Chat" or "LTAI-System" or "LTAI-Writer")
        {
            tools.Add(AIFunctionFactory.Create(SystemTools.date));
            tools.Add(AIFunctionFactory.Create(SystemTools.uname));
            tools.Add(AIFunctionFactory.Create(SystemTools.uptime));
            tools.Add(AIFunctionFactory.Create(SystemTools.whoami));
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
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "job")
            : name is "LTAI-Chat" or "LTAI-System" or "LTAI-Dev" or "LTAI-Writer")
        {
            var bgJobs = sp.GetRequiredService<BackgroundJobService>();
            tools.Add(AIFunctionFactory.Create(bgJobs.StartJob));
            tools.Add(AIFunctionFactory.Create(bgJobs.ListJobs));
            tools.Add(AIFunctionFactory.Create(bgJobs.GetJobOutput));
            tools.Add(AIFunctionFactory.Create(bgJobs.WaitForJob));
            tools.Add(AIFunctionFactory.Create(bgJobs.StopJob));
        }
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "task")
            : name is "LTAI-Chat" or "LTAI-System" or "LTAI-Dev" or "LTAI-Writer")
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
        if (canRead && canWrite && (yamlTools != null
            ? HasYamlTool(yamlTools, "download")
            : name.StartsWith("LTAI-Chat") || name is "LTAI-Dev" or "LTAI-Writer"))
            tools.Add(AIFunctionFactory.Create(FileDownloadTool.DownloadFile));
    }

    static void RegisterWorkflowTools(ToolSet tools, string name, IServiceProvider sp, string[]? yamlTools)
    {
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "workflow")
            : name is not ("LTAI-Chat" or "LTAI-Writer" or "LTAI-Dev")) return;
        var wfTools = new WorkflowTools(sp);
        tools.Add(AIFunctionFactory.Create(wfTools.WorkflowHandoff));
        tools.Add(AIFunctionFactory.Create(wfTools.WorkflowSequential));
        tools.Add(AIFunctionFactory.Create(wfTools.WorkflowConcurrent));
    }

    static void RegisterClusterAndDeepenTools(ToolSet tools, string name, IServiceProvider sp)
    {
        if (name is not ("LTAI-Chat" or "LTAI-System" or "LTAI-Writer" or "LTAI-Data")) return;
        var cs = sp.GetRequiredService<ClusterSummarizer>();
        tools.Add(AIFunctionFactory.Create(cs.SummarizeAsync));
        var dst = sp.GetRequiredService<DeepenSearchTool>();
        tools.Add(AIFunctionFactory.Create(dst.DeepenSearchAsync));
    }

    static void RegisterNewDomainTools(ToolSet tools, string name, bool canExec, bool canRead, bool canWrite, string ws, IServiceProvider sp,
        string[]? yamlTools)
    {
        if (canExec)
        {
            var archive = new ArchiveTools(ws);
            tools.Add(AIFunctionFactory.Create(archive.ArchiveCreate));
            tools.Add(AIFunctionFactory.Create(archive.ArchiveExtract));
        }
        if (canRead && canWrite)
        {
            var mm = new MultimediaTools(ws);
            tools.Add(AIFunctionFactory.Create(mm.ChartCreate));
        }
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "data") || HasYamlTool(yamlTools, "database")
            : name is "LTAI-Chat" or "LTAI-Data" or "LTAI-Dev")
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
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "security")
            : name.StartsWith("LTAI-Chat") || name is "LTAI-System" or "LTAI-Ops" or "LTAI-Writer")
        { tools.Add(AIFunctionFactory.Create(CryptoTools.sha256sum)); tools.Add(AIFunctionFactory.Create(CryptoTools.md5sum)); tools.Add(AIFunctionFactory.Create(CryptoTools.HashFile)); tools.Add(AIFunctionFactory.Create(CryptoTools.EncryptFile)); tools.Add(AIFunctionFactory.Create(CryptoTools.DecryptFile)); }
        if (canRead) { tools.Add(AIFunctionFactory.Create(CryptoTools.base64)); tools.Add(AIFunctionFactory.Create(CryptoTools.Base64Encode)); tools.Add(AIFunctionFactory.Create(CryptoTools.Base64Decode)); }
        if (canRead) tools.Add(AIFunctionFactory.Create(MarkdownTools.RenderMarkdown));
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "search")
            : name.StartsWith("LTAI-Chat") || name is "LTAI-Dev" or "LTAI-Data" or "LTAI-Writer")
        {
            var rc = sp.GetRequiredService<RetrieveContentTool>();
            tools.Add(AIFunctionFactory.Create(rc.RetrieveContent));
        }
    }

    static void RegisterMemoryTools(ToolSet tools, bool canWrite, PalaceStore palaceStore, string ws, string[]? yamlTools,
        IServiceProvider? sp = null)
    {
        if (!canWrite) return;
        var palaceMemory = new MemoryTools(palaceStore, defaultWing: ws != null ? Path.GetFileName(ws.TrimEnd('/', '\\')) : "project");
        tools.Add(AIFunctionFactory.Create(palaceMemory.Remember));
        tools.Add(AIFunctionFactory.Create(palaceMemory.Forget));
        tools.Add(AIFunctionFactory.Create(palaceMemory.RecallMemory));
        tools.Add(AIFunctionFactory.Create(palaceMemory.ListMemories));

        if (sp != null)
        {
            var offloader = sp.GetService<Memory.ContextOffloader>();
            var refsIndex = sp.GetService<Memory.RefsSearchIndex>();
            if (offloader != null)
            {
                var expand = new Tools.ExpandRefTool(offloader);
                tools.Add(AIFunctionFactory.Create(expand.ExpandRef));
            }
            if (refsIndex != null)
            {
                var refsSearch = new Tools.RefsSearchTool(refsIndex);
                tools.Add(AIFunctionFactory.Create(refsSearch.SearchRefs));
                tools.Add(AIFunctionFactory.Create(refsSearch.RebuildIndex));
            }
        }
    }

    static void RegisterSandboxTools(ToolSet tools, string name, string ws, string[]? yamlTools,
        IServiceProvider sp)
    {
        if (!HasYamlTool(yamlTools, "sandbox")
            && name is not ("LTAI-Chat" or "LTAI-Dev" or "LTAI-System" or "LTAI-Ops"))
            return;

        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("Sandbox");
        var mode = LTAI.Core.Configuration.EnvironmentConfig.SandboxMode;
        var sandbox = new Tools.ContainerSandboxProvider(ws, mode, logger);
        tools.Add(AIFunctionFactory.Create(sandbox.ExecuteInSandbox));
        tools.Add(AIFunctionFactory.Create(sandbox.WriteFileToSandbox));
        tools.Add(AIFunctionFactory.Create(sandbox.ReadFileFromSandbox));
    }

    static void RegisterCommunicationTools(ToolSet tools, string name, IHttpClientFactory httpFactory, string[]? yamlTools,
        IServiceProvider sp)
    {
        if (!HasYamlTool(yamlTools, "communication")
            && name is not ("LTAI-Chat" or "LTAI-System"))
            return;

        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("IM");
        var im = new Tools.ImChannelTool(httpFactory, logger);
        tools.Add(AIFunctionFactory.Create(im.SendMessage));
        tools.Add(AIFunctionFactory.Create(im.ListChannels));
    }

    static void RegisterExploreTools(ToolSet tools, string name, string ws)
    {
        if (name != AgentNames.Explore) return;
        var explore = new ExploreToolSet(ws);
        tools.Add(AIFunctionFactory.Create(explore.ReadCite));
        tools.Add(AIFunctionFactory.Create(explore.Glob));
        tools.Add(AIFunctionFactory.Create(explore.SearchCompact));
        tools.Add(AIFunctionFactory.Create(explore.ListDir));
        tools.Add(AIFunctionFactory.Create(explore.Tree));
    }

    static void RegisterDelegationTools(ToolSet tools, string name, IServiceProvider sp)
    {
        if (name is not ("LTAI-Chat" or "LTAI-Dev" or "LTAI-System" or "LTAI-Writer")) return;
        var del = new DelegationTools(sp.GetRequiredService<Delegation.DelegationContext>());
        tools.Add(AIFunctionFactory.Create(del.EnqueueDelegationTask));
        tools.Add(AIFunctionFactory.Create(del.ClaimNextTask));
        tools.Add(AIFunctionFactory.Create(del.WriteVerifiedUpdate));
        tools.Add(AIFunctionFactory.Create(del.ReadVerifiedContext));
        tools.Add(AIFunctionFactory.Create(del.ListDelegationTasks));
    }

    static void RegisterSessionLineageTools(ToolSet tools, string name, IServiceProvider sp)
    {
        if (name is not ("LTAI-Chat" or "LTAI-Dev" or "LTAI-System" or "LTAI-Writer")) return;
        var lt = new SessionLineageTools(sp.GetRequiredService<SessionManager>());
        tools.Add(AIFunctionFactory.Create(lt.ForkSession));
        tools.Add(AIFunctionFactory.Create(lt.MergeSessions));
        tools.Add(AIFunctionFactory.Create(lt.SessionGraph));
        tools.Add(AIFunctionFactory.Create(lt.ListChildSessions));
    }
}
