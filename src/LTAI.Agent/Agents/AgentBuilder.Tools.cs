using LTAI.Agent.LanguageServer;
using LTAI.Agent.Memory;
using LTAI.Agent.Services;
using LTAI.Agent.Tools;
using LTAI.Agent.CodeAnalysis;
using LTAI.Agent.Tools.Review;
using LTAI.Agent.Vector;
using LTAI.Core.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent;

partial class AgentBuilder
{
    static void RegisterFileAndTextTools(ToolSet tools, string name, bool canRead, bool canWrite, bool canList, bool canExec, string ws,
        IServiceProvider sp,
        Caching.MmapFileProvider? mmap = null, Caching.WriteBuffer? writeBuf = null)
    {
        var fs = new FileSystemTools(ws, mmap, writeBuf);
        var text = new TextTools(ws);
        if (canRead)
        {
            tools.Add(AIFunctionFactory.Create((string path) => fs.ReadFileContent(path), "ReadFileContent", "Read a file"));
            tools.Add(AIFunctionFactory.Create(fs.cat));
            tools.Add(AIFunctionFactory.Create(fs.head));
            tools.Add(AIFunctionFactory.Create(fs.find));
            tools.Add(AIFunctionFactory.Create(fs.ls));
            tools.Add(AIFunctionFactory.Create(fs.stat));
        }
        if (canWrite) tools.Add(AIFunctionFactory.Create(fs.WriteFile));
        if (canList) { tools.Add(AIFunctionFactory.Create(fs.Glob)); }
        if (canRead && canWrite)
        {
            tools.Add(AIFunctionFactory.Create(fs.cp));
            tools.Add(AIFunctionFactory.Create(fs.mv));
            tools.Add(AIFunctionFactory.Create(fs.rm));
            tools.Add(AIFunctionFactory.Create(fs.rmdir));
            tools.Add(AIFunctionFactory.Create(fs.mkdir));
            tools.Add(AIFunctionFactory.Create(fs.touch));
            tools.Add(AIFunctionFactory.Create(fs.CopyFile));
            tools.Add(AIFunctionFactory.Create(fs.MoveFile));
            tools.Add(AIFunctionFactory.Create(fs.DeleteFile));
            tools.Add(AIFunctionFactory.Create(fs.DeleteDirectory));
            tools.Add(AIFunctionFactory.Create(fs.GetFileInfo));
        }
        if (canExec) tools.Add(AIFunctionFactory.Create(new SafeShellTool(ws).RunCommand));
        if (canRead && canWrite)
        {
            tools.Add(AIFunctionFactory.Create(text.EditFile));
            tools.Add(AIFunctionFactory.Create(text.MultiEdit));
            var patchEdit = new PatchEditTool(ws);
            var cg = sp.GetService<CgGraph>();
            if (cg != null)
            {
                patchEdit.ImpactAnalyzer = async (symbol) =>
                    await cg.QueryImpactAsync(symbol, depth: 2).ConfigureAwait(false);
            }
            tools.Add(AIFunctionFactory.Create(patchEdit.ApplyPatches));
        }
        if (canRead) tools.Add(AIFunctionFactory.Create(TextTools.RegexTest));
        if (name.StartsWith("LTAI-Chat") || name is "LTAI-Dev" or "LTAI-QA" or "LTAI-Writer")
            tools.Add(AIFunctionFactory.Create(TextTools.DiffFiles));
    }

    static void RegisterSearchAndCodeAnalysisTools(ToolSet tools, string name, bool canRead, string ws, string[]? yamlTools,
        IServiceProvider sp)
    {
        var search = new SearchTools(ws);
        var codeAnalysis = new CodeAnalysisTools(ws);
        if (canRead) { tools.Add(AIFunctionFactory.Create(search.SearchContent)); tools.Add(AIFunctionFactory.Create(search.grep)); tools.Add(AIFunctionFactory.Create(search.SearchFiles)); }
        if (canRead && (yamlTools != null
            ? HasYamlTool(yamlTools, "symbols")
            : name.StartsWith("LTAI-Chat") || name is "LTAI-Dev"))
        { tools.Add(AIFunctionFactory.Create(codeAnalysis.GetSymbols)); tools.Add(AIFunctionFactory.Create(codeAnalysis.FindInCode)); }

        if (canRead)
        {
            System.Func<string, int, Task<string>> codeMapFn = (path, maxFiles) =>
                CodeMap.GetMapAsync(ws, path, Math.Clamp(maxFiles, 1, 50));
            tools.Add(AIFunctionFactory.Create(codeMapFn, "CodeMap",
                "Get a compact structural outline of a file or directory. Returns class/struct/interface/method/enum symbols with line numbers in a token-efficient format (~40% smaller than GetSymbols). Parameters: path (file or directory), maxFiles (1-50, default 20). Preferred over GetSymbols for quick overviews."));
        }

        if (canRead)
        {
            var cg = sp.GetService<CgGraph>();
            if (cg != null)
            {
                System.Func<string, int, Task<string>> impactFn = (symbol, depth) => cg.QueryImpactAsync(symbol, depth);
                tools.Add(AIFunctionFactory.Create(impactFn, "QueryImpact",
                    "Analyze what would break if a symbol changes. Returns forward (called by) and reverse (calls) reachability. depth: 1-3."));

                System.Func<string, Task<string>> compactFn = (query) => cg.QueryCompactAsync(query);
                tools.Add(AIFunctionFactory.Create(compactFn, "QueryCodeGraph", "Search code graph in compact format (~27% fewer tokens than JSON). Returns symbol type, name, and source path."));
            }

            var contracts = sp.GetService<ContractRegistry>();
            if (contracts != null)
            {
                System.Func<string, string, string> crossRepoFn = (repoA, repoB) =>
                    System.Text.Json.JsonSerializer.Serialize(contracts.FindCrossRepo(repoA, repoB));
                tools.Add(AIFunctionFactory.Create(crossRepoFn, "FindCrossRepoContracts",
                    "Find API contracts shared between two repositories. Detects HTTP routes, gRPC services, message topics, env vars."));

                tools.Add(AIFunctionFactory.Create(() =>
                    contracts.ToString(),
                    "ListContracts",
                    "List all registered API contracts with provider/consumer repos."));
            }
        }
    }

    static void RegisterWebTools(ToolSet tools, string name, IHttpClientFactory httpFactory, string[]? yamlTools)
    {
        var web = new WebTools(httpFactory, null);
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "web")
            : name.StartsWith("LTAI-Chat") || name == "LTAI-Data")
        { tools.Add(AIFunctionFactory.Create(web.WebSearch)); tools.Add(AIFunctionFactory.Create(web.WebFetch)); tools.Add(AIFunctionFactory.Create(web.HttpRequest)); }
    }

    static void RegisterMultimediaTools(ToolSet tools, bool canRead, bool canExec, string ws, string[]? yamlTools)
    {
        var media = new MultimediaTools(ws);
        if (canRead)
        { tools.Add(AIFunctionFactory.Create(media.ImageInfo)); tools.Add(AIFunctionFactory.Create(media.ImageResize)); tools.Add(AIFunctionFactory.Create(media.ImageConvert)); tools.Add(AIFunctionFactory.Create(media.MediaInfo)); tools.Add(AIFunctionFactory.Create(media.AudioConvert)); }
        if (canExec) tools.Add(AIFunctionFactory.Create(media.Screenshot));
    }

    static void RegisterDocumentTools(ToolSet tools, bool canRead, bool canWrite, string ws, IServiceProvider sp,
        string[]? yamlTools)
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

    static void RegisterPlanAndDiagramTools(ToolSet tools, string name, IHttpClientFactory httpFactory,
        string[]? yamlTools)
    {
        if (yamlTools != null
            ? HasYamlTool(yamlTools, "diagram")
            : name.StartsWith("LTAI-Chat") || name is "LTAI-Dev" or "LTAI-Data" or "LTAI-Writer")
        {
            var diagram = new FlowchartTools(httpFactory);
            tools.Add(AIFunctionFactory.Create(diagram.Flowchart));
            tools.Add(AIFunctionFactory.Create(diagram.SequenceDiagram));
            tools.Add(AIFunctionFactory.Create(diagram.ClassDiagram));
            tools.Add(AIFunctionFactory.Create(diagram.GanttChart));
            tools.Add(AIFunctionFactory.Create(diagram.ErDiagram));
        }
    }

    static void RegisterTextProcessingTools(ToolSet tools, string name, bool canRead, string ws)
    {
        if (!canRead) return;
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.tail));
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.wc));
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.sort));
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.uniq));
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.cut));
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.tr));
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.tee));
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.du));
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.df));
        tools.Add(AIFunctionFactory.Create(TextProcessingTools.seq));

        var initService = new ProjectInitService(ws);
        System.Func<Task<string>> initFn = () => initService.InitAsync();
        tools.Add(AIFunctionFactory.Create(initFn, "InitProject",
            "One-click project initialization. Detects project type (dotnet/node/rust/python/go), creates LTAI.md context file, and returns build/test commands. Run once when starting on a new project."));
    }
}
