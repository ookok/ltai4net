using LTAI.Agent.Memory;
using LTAI.Agent.Tools;
using LTAI.Agent.Tools.Review;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

partial class AgentBuilder
{
    static void RegisterGitTools(ToolSet tools, string name, string ws, string[]? yamlTools)
    {
        if (!(yamlTools != null
            ? HasYamlTool(yamlTools, "git")
            : name.StartsWith("LTAI-Chat") || name is "LTAI-Dev" or "LTAI-System" or "LTAI-Writer")) return;
        var git = new GitTools(ws);
        tools.Add(AIFunctionFactory.Create(git.GitStatus));
        tools.Add(AIFunctionFactory.Create(git.GitLog));
        tools.Add(AIFunctionFactory.Create(git.GitDiff));
        tools.Add(AIFunctionFactory.Create(git.GitBlame));
        tools.Add(AIFunctionFactory.Create(git.GitShow));
        tools.Add(AIFunctionFactory.Create(git.GitBranch));
        tools.Add(AIFunctionFactory.Create(git.GitCheckout));
        tools.Add(AIFunctionFactory.Create(git.GitBranchDelete));
        tools.Add(AIFunctionFactory.Create(git.GitAdd));
        tools.Add(AIFunctionFactory.Create(git.GitUnstage));
        tools.Add(AIFunctionFactory.Create(git.GitCommitAndPush));
        tools.Add(AIFunctionFactory.Create(git.GitUndoLast));
        tools.Add(AIFunctionFactory.Create(git.GitReset));
        tools.Add(AIFunctionFactory.Create(git.GitStash));
        tools.Add(AIFunctionFactory.Create(git.GitMerge));
        tools.Add(AIFunctionFactory.Create(git.GitRemote));
    }

    static void RegisterReviewTools(ToolSet tools, string name, string ws, PalaceStore? palaceStore = null,
        IServiceProvider? sp = null, IChatClient? llm = null, IReadOnlyList<AITool>? allTools = null)
    {
        if (name is not ("LTAI-Chat" or "LTAI-QA" or "LTAI-Dev" or "LTAI-Writer" or "LTAI-Ops")) return;
        var review = new ReviewTools(ws, palaceStore, sp, llm, allTools);
        tools.Add(AIFunctionFactory.Create(review.LoadReviewRules));
        tools.Add(AIFunctionFactory.Create(review.GroupChanges));
        tools.Add(AIFunctionFactory.Create(review.MatchReviewRules));
        tools.Add(AIFunctionFactory.Create(review.RepairReviewPositions));
        tools.Add(AIFunctionFactory.Create(review.ReflectReviewQuality));
        tools.Add(AIFunctionFactory.Create(review.BuildReviewContext));
        tools.Add(AIFunctionFactory.Create(review.SaveAuditFindings));
        tools.Add(AIFunctionFactory.Create(review.ResolveAuditFinding));
        tools.Add(AIFunctionFactory.Create(review.VerifyAuditFinding));
        tools.Add(AIFunctionFactory.Create(review.ListAuditFindings));
        tools.Add(AIFunctionFactory.Create(review.GetAuditFinding));
        tools.Add(AIFunctionFactory.Create(review.CloseAuditFinding));
        tools.Add(AIFunctionFactory.Create(review.ExportAuditFindings));
        tools.Add(AIFunctionFactory.Create(review.AuditStatistics));
        tools.Add(AIFunctionFactory.Create(review.BatchResolveAuditFindings));
        tools.Add(AIFunctionFactory.Create(review.BatchCloseAuditFindings));
        tools.Add(AIFunctionFactory.Create(review.DeleteAuditFinding));
        tools.Add(AIFunctionFactory.Create(review.FreezeAuditGates));
        if (allTools != null)
            tools.Add(AIFunctionFactory.Create(review.ParallelReview));
    }

    static void RegisterLspTools(ToolSet tools, string name, string[]? yamlTools,
        LanguageServer.LspLanguageManager lspManager)
    {
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "symbols")
            : !(name.StartsWith("LTAI-Chat") || name is "LTAI-Dev")) return;
        tools.Add(AIFunctionFactory.Create(async (string filePath, string content) =>
        {
            await lspManager.OpenFileAsync(filePath, content);
            return $"LSP opened: {filePath}";
        }, "LspOpenFile", "Open a file in its language server for real-time diagnostics"));
        tools.Add(AIFunctionFactory.Create(() =>
        {
            var diags = lspManager.FormatDiagnostics();
            return string.IsNullOrEmpty(diags) ? "(no LSP diagnostics)" : diags;
        }, "LspGetDiagnostics", "Get current LSP diagnostics for open files"));
    }

    static void RegisterDebugTools(ToolSet tools, string name, IServiceProvider sp)
    {
        if (name is not ("LTAI-Chat" or "LTAI-Dev" or "LTAI-System")) return;
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

    static void RegisterBuildAndPublishTools(ToolSet tools, string name, string ws, bool canExec)
    {
        if (name is not ("LTAI-Chat" or "LTAI-Dev" or "LTAI-Ops" or "LTAI-QA")) return;
        var build = new BuildTools(ws);
        tools.Add(AIFunctionFactory.Create(build.BuildProject));
        tools.Add(AIFunctionFactory.Create(build.BuildAndFix));
        tools.Add(AIFunctionFactory.Create(BuildTools.DetectBuild));
        tools.Add(AIFunctionFactory.Create(BuildTools.ParseBuildOutput));
        if (name is "LTAI-Ops" or "LTAI-Chat")
        {
            var publish = new PublishTools(ws);
            tools.Add(AIFunctionFactory.Create(publish.PublishProject));
            tools.Add(AIFunctionFactory.Create(PublishTools.DetectPublish));
            tools.Add(AIFunctionFactory.Create(PublishTools.ListPublished));
        }
    }

    static void RegisterIntegrationTools(ToolSet tools, string name, IHttpClientFactory httpFactory, string[]? yamlTools)
    {
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "eia")
            : name is not ("LTAI-Chat" or "LTAI-Data" or "LTAI-System" or "LTAI-Writer" or "LTAI-Dev")) return;
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

    static void RegisterChoiceAndSubagentTools(ToolSet tools, string name, IServiceProvider sp, IChatClient llm, string ws,
        string[]? yamlTools)
    {
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "subagent") || HasYamlTool(yamlTools, "choice")
            : name is "LTAI-Chat" or "LTAI-Writer" or "LTAI-Dev")
        {
            var sub = new SubagentTools(sp, llm, ws, tools.ToList());
        tools.Add(AIFunctionFactory.Create(sub.Explore));
        tools.Add(AIFunctionFactory.Create(sub.Research));
        tools.Add(AIFunctionFactory.Create(sub.Review));
        tools.Add(AIFunctionFactory.Create(sub.SecurityReview));
        tools.Add(AIFunctionFactory.Create(sub.DeepResearch));
        tools.Add(AIFunctionFactory.Create(sub.SpawnSubagent));
        }
    }
}
