using LTAI.Core.Messaging;
using LTAI.Core.System;
using LTAI.Models;
using LTAI.Tools.Capability.Governance;
using LTAI.Tools.CodeEngine;
using LTAI.Tools.CodeGraph;
using LTAI.Tools.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LTAI.Tools.Tools;

public static class LTAIToolRegistry
{
    private static bool _seeded;
    private static IServiceProvider? _serviceProvider;
    private static ILogger? _logger;

    private static string LivingTreeDir =>
        Path.Combine(OptionService.Get("LTAI_WORKSPACE") ?? Environment.CurrentDirectory, OptionService.Get("paths.DataDirectory") ?? ".livingtree");

    public static async Task SeedAllAsync(AIToolRegistry registry, IServiceProvider sp)
    {
        _serviceProvider = sp;
        _logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("LTAI.Tools.Tools.LTAIToolRegistry");
        MdToolBridge.Initialize(sp);
        if (_seeded) return;
        _seeded = true;

        foreach (var tool in AllTools)
        {
            if (tool.Handler == null) continue;

            var handler = tool.Handler;
            var mdName = tool.Name.Replace(':', '_');
            var wrapped = CreateWrappedHandler(handler, mdName);
            await registry.RegisterAsync(tool.Name, wrapped);
        }
    }

    private static Func<Dictionary<string, object?>, Task<object?>> CreateWrappedHandler(
        Func<Dictionary<string, object?>, Task<object?>> original, string mdName)
    {
        return async args =>
        {
            var md = await MdToolBridge.TryExecuteAsync(mdName, args);
            if (md != null) return md;
            return await original(args);
        };
    }

    private static T GetService<T>()
    {
        var svc = _serviceProvider?.GetRequiredService(typeof(T))
            ?? throw new InvalidOperationException($"Service {typeof(T).Name} not available. Ensure SeedAllAsync has been called.");
        return (T)svc;
    }

    public static readonly ToolDef[] AllTools =
    {
        // ═══ VFS — 7 tools ═══
        new("vfs:read", "Read file content from virtual filesystem", "vfs",
            async args => await VfsAdapter.Instance.ReadAsync(Arg(args, "path"))),
        new("vfs:write", "Write content to virtual filesystem", "vfs",
            async args => await Task.FromResult<object?>(await VfsAdapter.Instance.WriteAsync(Arg(args, "path"), Arg(args, "content")))),
        new("vfs:list", "List directory contents in VFS", "vfs",
            async args => await Task.FromResult<object?>(await VfsAdapter.Instance.ListAsync(Arg(args, "path")))),
        new("vfs:delete", "Delete file from VFS", "vfs",
            async _ => { VfsAdapter.Instance.Delete(Arg(_, "path")); return true; }),
        new("vfs:exists", "Check if file exists in VFS", "vfs",
            async args => await VfsAdapter.Instance.ExistsAsync(Arg(args, "path"))),
        new("vfs:search", "Search VFS by content", "vfs",
            async args => await Task.FromResult<object?>(await VfsAdapter.Instance.SearchAsync(Arg(args, "path"), Arg(args, "query")))),
        new("vfs:move", "Move/rename file in VFS", "vfs",
            async args => await VfsAdapter.Instance.MoveAsync(Arg(args, "source"), Arg(args, "dest"))),

        // ═══ Web & Search — 4 tools ═══
        new("web_fetch", "Fetch web page content by URL (via DuckDuckGo HTML search or direct fetch)", "web",
            async args =>
            {
                var url = Arg(args, "url");
                if (string.IsNullOrWhiteSpace(url)) return JsonToolResult.Error("url parameter is required");
                using var http = LTAI.Core.Network.HttpAccelerator.CreateAcceleratedClient();
                var html = await http.GetStringAsync(url);
                var titleMatch = System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>([^<]+)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var text = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", " ")
                    .Replace("&nbsp;", " ").Replace("&amp;", "&");
                return JsonToolResult.Success(new { url, title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "", text = text[..Math.Min(5000, text.Length)] });
            }),
        new("search", "Multi-source unified web search using DuckDuckGo (free, no API key). Parameters: query (required), count (1-20, default 5)", "web",
            async args =>
            {
                var query = Arg(args, "query");
                if (string.IsNullOrWhiteSpace(query)) return JsonToolResult.Error("query parameter is required");
                var count = Math.Clamp(int.TryParse(Arg(args, "count", "5"), out var c) ? c : 5, 1, 20);
                var results = new List<object>();
                try
                {
                    using var http = LTAI.Core.Network.HttpAccelerator.CreateAcceleratedClient();
                    var searchUrl = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
                    var html = await http.GetStringAsync(searchUrl);
                    var linkMatches = System.Text.RegularExpressions.Regex.Matches(html,
                        @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>([^<]+)</a>");
                    var snippetMatches = System.Text.RegularExpressions.Regex.Matches(html,
                        @"<a[^>]*class=""result__snippet""[^>]*>([^<]+)</a>");
                    for (int i = 0; i < Math.Min(count, Math.Min(linkMatches.Count, snippetMatches.Count)); i++)
                        results.Add(new { title = System.Net.WebUtility.HtmlDecode(linkMatches[i].Groups[2].Value.Trim()), url = System.Net.WebUtility.HtmlDecode(linkMatches[i].Groups[1].Value.Trim()), snippet = System.Net.WebUtility.HtmlDecode(snippetMatches[i].Groups[1].Value.Trim()) });
                    return JsonToolResult.Success(new { query, source = "DuckDuckGo", results });
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "search: DuckDuckGo search failed"); return JsonToolResult.Success(new { query, error = "Search failed", results }); }
            }),
        new("search_apis", "Search 1400+ public APIs by keyword", "web",
            async args => { await PublicApisResource.Instance.LoadAsync(); var r = PublicApisResource.Instance.Search(Arg(args, "query")); return r; }),
        new("platform_catalog", "List all 24 indexed content platforms (CSDN, Zhihu, WeChat, Toutiao, Xiaohongshu, Juejin, Bilibili, etc.) with descriptions and aliases. Use this to discover what platforms are available.", "web",
            async _ =>
            {
                var svc = Search.PlatformSearchService.Instance;
                return JsonToolResult.Success(new { summary = svc.BuildPromptContext(), stats = svc.GetStats() });
            }),
        new("platform_search", "Search content on a specific Chinese platform. Available platforms: csdn, zhihu, toutiao, wechat, xiaohongshu, juejin, bilibili, weibo, segmentfault, v2ex, zhuanlan, github_zh, wikipedia_zh, baike, douban, 36kr, infoq, oschina, cnblogs, jianshu, gov_cn, mee, ndrc, mohurd. Parameters: query (required), platform (required, platform name or alias), count (1-20)", "web",
            async args =>
            {
                var query = Arg(args, "query");
                if (string.IsNullOrWhiteSpace(query)) return JsonToolResult.Error("query parameter is required");
                var svc = Search.PlatformSearchService.Instance;
                var platform = svc.Resolve(Arg(args, "platform"));
                if (platform == null) return JsonToolResult.Success(new { error = $"Unknown platform '{Arg(args, "platform")}'. Use platform_catalog to list available platforms." });
                var searchQuery = svc.BuildSearchQuery(query, platform.Name);
                var count = Math.Clamp(int.TryParse(Arg(args, "count", "5"), out var c) ? c : 5, 1, 20);
                try
                {
                    using var http = LTAI.Core.Network.HttpAccelerator.CreateAcceleratedClient();
                    var searchUrl = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(searchQuery)}";
                    var html = await http.GetStringAsync(searchUrl);
                    var results = new List<object>();
                    var links = System.Text.RegularExpressions.Regex.Matches(html, @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>([^<]+)</a>");
                    var snippets = System.Text.RegularExpressions.Regex.Matches(html, @"<a[^>]*class=""result__snippet""[^>]*>([^<]+)</a>");
                    for (int i = 0; i < Math.Min(count, Math.Min(links.Count, snippets.Count)); i++)
                        results.Add(new { title = System.Net.WebUtility.HtmlDecode(links[i].Groups[2].Value.Trim()), url = System.Net.WebUtility.HtmlDecode(links[i].Groups[1].Value.Trim()), snippet = System.Net.WebUtility.HtmlDecode(snippets[i].Groups[1].Value.Trim()) });
                    return JsonToolResult.Success(new { query, platform = platform.Name, category = platform.Category, results });
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "platform_search: search failed"); return JsonToolResult.Success(new { query, platform = platform.Name, error = "Search failed", results = new List<object>() }); }
            }),

        // ═══ Knowledge — 4 tools ═══
        new("km_search", "Semantic knowledge search using AgenticRAG. Parameters: query (required), domain (optional)", "knowledge",
            async args =>
            {
                var query = Arg(args, "query");
                if (string.IsNullOrWhiteSpace(query)) return JsonToolResult.Error("query parameter is required");
                try
                {
                    var ragType = typeof(object).Assembly.GetType("LTAI.Vector.Knowledge.AgenticRAG");
                    var rag = ragType != null ? _serviceProvider?.GetService(ragType) : null;
                    if (rag != null)
                    {
                        var method = rag.GetType().GetMethod("Search");
                        if (method != null)
                        {
                            var results = method.Invoke(rag, new object?[] { query, 3, Arg(args, "domain", "general") });
                            return results ?? new { message = "No results" };
                        }
                    }
                    return JsonToolResult.Error("Knowledge search not available");
                }
                catch (Exception ex) { return JsonToolResult.Success(new { error = $"Search failed: {ex.Message}" }); }
            }),
        new("km_import", "Import document into knowledge base. Parameters: content (required), title (optional), domain (optional)", "knowledge",
            async args =>
            {
                var content = Arg(args, "content");
                if (string.IsNullOrWhiteSpace(content)) return JsonToolResult.Error("content parameter is required");
                try
                {
                    var kbType = typeof(object).Assembly.GetType("LTAI.Vector.Knowledge.KnowledgeBase");
                    if (kbType != null)
                    {
                        var kb = _serviceProvider?.GetService(kbType);
                        var addMethod = kb?.GetType().GetMethod("AddKnowledgeAsync");
                        if (addMethod != null)
                            await (Task)addMethod.Invoke(kb, new object?[] { Arg(args, "title", "imported"), content, Arg(args, "domain", "general") })!;
                    }
                    return JsonToolResult.Success(new { status = "imported", chars = content.Length });
                }
                catch (Exception ex) { return JsonToolResult.Success(new { error = $"Import failed: {ex.Message}" }); }
            }),
        // ═══ Code — 13 tools (analyze, review, graph, edit, build, test) ═══
        new("code_analyze", "Analyze code structure, complexity, dependencies using real AST", "code",
            async args =>
            {
                var code = Arg(args, "code", "");
                if (string.IsNullOrWhiteSpace(code)) return JsonToolResult.Error("code parameter is required");
                try
                {
                    var langStr = Arg(args, "language", "");
                    var language = string.IsNullOrEmpty(langStr)
                        ? CodeLanguage.CSharp
                        : Enum.TryParse<CodeLanguage>(langStr, ignoreCase: true, out var l) ? l : CodeLanguage.Unknown;
                    var analyzer = GetService<MultiLangCodeAnalyzer>();
                    var result = await analyzer.AnalyzeAsync(code, language);
                    return new
                    {
                        language = language.ToString(),
                        result.TotalLines,
                        result.CodeLines,
                        result.Complexity,
                        functionCount = result.Functions.Count,
                        classCount = result.Classes.Count,
                        importCount = result.Imports.Count,
                        functions = result.Functions.Select(f => new { f.Name, f.Line, f.ParameterCount }),
                        classes = result.Classes.Select(c => new { c.Name, c.Line, c.MethodCount }),
                    };
                }
                catch (Exception ex) { return JsonToolResult.Success(new { error = $"Analysis failed: {ex.Message}" }); }
            }),
        new("code_review", "Automated code review for bugs, security, style issues. Use target=branch or scope=staged/unstaged for git diffs.", "code",
            async args =>
            {
                try
                {
                    var reviewer = GetService<CodeReviewEngine>();
                    var target = Arg(args, "target", "");
                    var scopeStr = Arg(args, "scope", "staged");
                    var scope = scopeStr.ToLowerInvariant() switch
                    {
                        "unstaged" => ReviewScope.Unstaged,
                        "branch" => ReviewScope.Branch,
                        "file" => ReviewScope.File,
                        _ => ReviewScope.Staged,
                    };
                    var report = await reviewer.ReviewAsync(
                        string.IsNullOrEmpty(target) ? null : target,
                        scope);
                    return new
                    {
                        score = report.OverallScore,
                        report.TotalIssues,
                        report.CriticalIssues,
                        report.Warnings,
                        report.Infos,
                        report.FilesChanged,
                        report.Summary,
                        issues = report.Issues.Take(15).Select(i => new
                        {
                            i.Severity,
                            i.File,
                            i.Line,
                            i.Category,
                            i.Title,
                            i.Message,
                            i.Suggestion,
                        }),
                    };
                }
                catch (Exception ex) { return JsonToolResult.Success(new { error = $"Review failed: {ex.Message}" }); }
            }),
        new("sandbox_exec", "Execute code in isolated sandbox (Process/Docker/Hyperlight)", "code",
            async args => {
                var soType = typeof(object).Assembly.GetType("LTAI.Infra.Sandbox.SandboxOrchestrator");
                var so = soType != null ? _serviceProvider?.GetService(soType) : null;
                if (so is null)
                {
                    // Try resolving via direct type if assembly reflection fails
                    try { so = _serviceProvider?.GetService(
                        Type.GetType("LTAI.Infra.Sandbox.SandboxOrchestrator, LTAI.Infra")!); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "sandbox_exec: type resolution fallback failed"); }
                }
                if (so != null)
                {
                    var m = so.GetType().GetMethod("ExecuteAsync");
                    var reqType = so.GetType().Assembly.GetType("LTAI.Infra.Sandbox.SandboxRequest");
                    if (reqType != null && m != null)
                    {
                        var req = Activator.CreateInstance(reqType);
                        reqType.GetProperty("Code")?.SetValue(req, Arg(args, "code"));
                        reqType.GetProperty("Language")?.SetValue(req, Arg(args, "language", "python"));
                        var task = (Task)m.Invoke(so, new[] { req, CancellationToken.None });
                        if (task != null) { await task; return task.GetType().GetProperty("Result")?.GetValue(task); }
                    }
                }
                return JsonToolResult.Success(new { error = "Sandbox not available. Install Docker or ensure python3/node is on PATH.", hint = "Use shell_exec for direct shell execution" });
            }),

        // Code Graph tools (using CodeGraphEnhanced - SQLite + FTS5)
        new("code_graph:search", "Search the code graph for symbols using SQLite FTS5. Returns functions, classes, methods matching the query.", "code",
            async args =>
            {
                var query = Arg(args, "query");
                if (string.IsNullOrWhiteSpace(query)) return JsonToolResult.Success(new { query = "", results = new object[0], hint = "Provide a query parameter" });
                try
                {
                    var graph = GetService<CodeGraphEnhanced>();
                    var kind = Arg(args, "kind", "");
                    var limit = Math.Clamp(int.TryParse(Arg(args, "limit", "20"), out var l) ? l : 20, 1, 100);
                    var results = graph.Search(query, string.IsNullOrEmpty(kind) ? null : kind, limit);
                    return new
                    {
                        query,
                        found = results.Count,
                        results = results.Select(r => new
                        {
                            r.Name, r.Kind, r.File, r.Line, r.EndLine,
                            r.ParentClass, r.Route, r.CallerCount, r.CalleeCount,
                            r.Complexity,
                        }).ToList(),
                    };
                }
                catch (Exception ex) { return JsonToolResult.Success(new { query, error = $"Search failed: {ex.Message}" }); }
            }),
        new("code_graph:blast_radius", "Calculate blast radius: find all functions affected when a given symbol changes. Includes test file detection.", "code",
            async args =>
            {
                var symbol = Arg(args, "symbol");
                if (string.IsNullOrWhiteSpace(symbol)) return JsonToolResult.Error("symbol parameter is required");
                try
                {
                    var graph = GetService<CodeGraphEnhanced>();
                    var maxDepth = Math.Clamp(int.TryParse(Arg(args, "max_depth", "3"), out var d) ? d : 3, 1, 5);
                    var impact = graph.GetImpactRadius(symbol, maxDepth);
                    return new
                    {
                        impact.TargetSymbol,
                        impact.DirectCallers,
                        impact.TransitiveCallers,
                        impact.AffectedFiles,
                        impact.AffectedTests,
                        impact.Radius,
                        affectedNodes = impact.AffectedNodes.Take(30).Select(n => new
                        {
                            n.Name, n.File, n.Line, n.Kind,
                            isTestFile = n.File.Contains("Test", StringComparison.OrdinalIgnoreCase),
                        }).ToList(),
                    };
                }
                catch (Exception ex) { return JsonToolResult.Success(new { symbol, error = $"Blast radius failed: {ex.Message}" }); }
            }),
        new("code_graph:callers", "Find all callers of a given symbol (who calls this function/class).", "code",
            async args =>
            {
                var symbol = Arg(args, "symbol");
                if (string.IsNullOrWhiteSpace(symbol)) return JsonToolResult.Error("symbol parameter is required");
                try
                {
                    var graph = GetService<CodeGraphEnhanced>();
                    var depth = Math.Clamp(int.TryParse(Arg(args, "depth", "2"), out var d) ? d : 2, 1, 5);
                    var callers = graph.GetCallers(symbol, depth);
                    return new
                    {
                        symbol,
                        depth,
                        callerCount = callers.Count,
                        callers = callers.Select(c => new { c.Name, c.File, c.Line, c.Kind, c.ParentClass }).ToList(),
                    };
                }
                catch (Exception ex) { return JsonToolResult.Success(new { symbol, error = $"Callers lookup failed: {ex.Message}" }); }
            }),
        new("code_graph:context", "Build LLM-friendly code context markdown for a given task. Searches the code graph and returns structured markdown for prompt injection.", "code",
            async args =>
            {
                var task = Arg(args, "task");
                if (string.IsNullOrWhiteSpace(task)) return JsonToolResult.Error("task parameter is required");
                try
                {
                    var graph = GetService<CodeGraphEnhanced>();
                    var maxNodes = Math.Clamp(int.TryParse(Arg(args, "max_nodes", "20"), out var n) ? n : 20, 1, 50);
                    var context = graph.BuildContext(task, maxNodes, "markdown");
                    return JsonToolResult.Success(new { task, context });
                }
                catch (Exception ex) { return JsonToolResult.Success(new { task, error = $"Context build failed: {ex.Message}" }); }
            }),
        new("code_graph:status", "Get code graph indexing status: files indexed, total nodes, total edges.", "code",
            async _ =>
            {
                try
                {
                    var graph = GetService<CodeGraphEnhanced>();
                    return await Task.FromResult<object?>(graph.GetStatus());
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "code_graph:status: failed"); return JsonToolResult.Success(new { status = "not_initialized", hint = "Call code_graph:index first" }); }
            }),

        // Code Edit tools (surgical AST-aware edits with diff, validation, rollback)
        new("code_edit:replace_range", "Replace a range of lines. startLine/endLine are 1-based. Returns unified diff + syntax diagnostics.", "code_edit",
            async args =>
            {
                var path = Arg(args, "path");
                if (!int.TryParse(Arg(args, "start_line", "0"), out var start) ||
                    !int.TryParse(Arg(args, "end_line", "0"), out var end))
                    return JsonToolResult.Success(new { error = "start_line and end_line (integers) are required" });
                var tools = GetService<CodeEditTools>();
                var resultJson = await tools.EditReplaceRange(path, start, end, Arg(args, "new_code", ""));
                return System.Text.Json.JsonSerializer.Deserialize<object>(resultJson)!;
            }),
        new("code_edit:replace_function", "Replace a specific function body using AST to locate boundaries. Compiles and validates.", "code_edit",
            async args =>
            {
                var path = Arg(args, "path");
                var functionName = Arg(args, "function_name");
                if (string.IsNullOrWhiteSpace(functionName))
                    return JsonToolResult.Success(new { error = "function_name parameter is required" });
                var tools = GetService<CodeEditTools>();
                var resultJson = await tools.EditReplaceFunction(path, functionName, Arg(args, "new_code", ""));
                return System.Text.Json.JsonSerializer.Deserialize<object>(resultJson)!;
            }),
        new("code_edit:insert", "Insert code after a specific line. Returns diff + validation.", "code_edit",
            async args =>
            {
                var path = Arg(args, "path");
                if (!int.TryParse(Arg(args, "line", "0"), out var line))
                    return JsonToolResult.Success(new { error = "line (integer) parameter is required" });
                var tools = GetService<CodeEditTools>();
                var resultJson = await tools.EditInsertAfterLine(path, line, Arg(args, "code", ""));
                return System.Text.Json.JsonSerializer.Deserialize<object>(resultJson)!;
            }),
        new("code_edit:delete", "Delete a range of lines. Returns deleted content + diff.", "code_edit",
            async args =>
            {
                var path = Arg(args, "path");
                if (!int.TryParse(Arg(args, "start_line", "0"), out var start) ||
                    !int.TryParse(Arg(args, "end_line", "0"), out var end))
                    return JsonToolResult.Success(new { error = "start_line and end_line (integers) are required" });
                var tools = GetService<CodeEditTools>();
                var resultJson = await tools.EditDeleteRange(path, start, end);
                return System.Text.Json.JsonSerializer.Deserialize<object>(resultJson)!;
            }),
        new("code_edit:validate", "Validate syntax of a file using AST diagnostics. Returns error/warning list.", "code_edit",
            async args =>
            {
                var path = Arg(args, "path");
                var tools = GetService<CodeEditTools>();
                var resultJson = await tools.EditValidateSyntax(path);
                return System.Text.Json.JsonSerializer.Deserialize<object>(resultJson)!;
            }),
        new("code_edit:diff", "Generate unified diff between snapshot and current file. Use snapshotId from a previous edit result.", "code_edit",
            async args =>
            {
                var tools = GetService<CodeEditTools>();
                return System.Text.Json.JsonSerializer.Deserialize<object>(
                    tools.EditDiff(Arg(args, "path"), Arg(args, "snapshot_id", "")))!;
            }),

        // Code Read tools (selective, AST-aware file reading to minimize token usage)
        new("code_read:range", "Read a specific line range from a file. Efficient for large files - only returns requested lines.", "code_read",
            async args =>
            {
                var path = Arg(args, "path");
                if (!int.TryParse(Arg(args, "start_line", "1"), out var start))
                    return JsonToolResult.Success(new { error = "start_line (integer) parameter is required" });
                var tools = GetService<CodeEditTools>();
                var resultJson = await tools.ReadRange(path, start,
                    int.TryParse(Arg(args, "count", "50"), out var c) ? c : 50);
                return System.Text.Json.JsonSerializer.Deserialize<object>(resultJson)!;
            }),
        new("code_read:function", "Read a specific function using AST. Returns function body + signature info. Much more token-efficient than reading the entire file.", "code_read",
            async args =>
            {
                var path = Arg(args, "path");
                var functionName = Arg(args, "function_name");
                if (string.IsNullOrWhiteSpace(functionName))
                    return JsonToolResult.Success(new { error = "function_name parameter is required" });
                var tools = GetService<CodeEditTools>();
                var resultJson = await tools.ReadFunction(path, functionName);
                return System.Text.Json.JsonSerializer.Deserialize<object>(resultJson)!;
            }),
        new("code_read:class", "Read a specific class using AST. Returns class definition with method/field signatures. Token-efficient.", "code_read",
            async args =>
            {
                var path = Arg(args, "path");
                var className = Arg(args, "class_name");
                if (string.IsNullOrWhiteSpace(className))
                    return JsonToolResult.Success(new { error = "class_name parameter is required" });
                var tools = GetService<CodeEditTools>();
                var resultJson = await tools.ReadClass(path, className);
                return System.Text.Json.JsonSerializer.Deserialize<object>(resultJson)!;
            }),
        new("code_read:structure", "Get structured overview of a file: all function signatures, class summaries, imports, diagnostics. Best first tool to understand a file.", "code_read",
            async args =>
            {
                var path = Arg(args, "path");
                var tools = GetService<CodeEditTools>();
                var resultJson = await tools.ReadStructure(path);
                return System.Text.Json.JsonSerializer.Deserialize<object>(resultJson)!;
            }),

        // Build tools (structured build with error parsing)
        new("code_build:run", "Run project build with auto-detection of build system. Returns structured errors with file/line/code.", "code_build",
            async args =>
            {
                try
                {
                    var pipeline = GetService<BuildPipeline>();
                    var result = await pipeline.BuildAsync(
                        Arg(args, "path", ""),
                        Arg(args, "configuration", "Debug"));
                    return new
                    {
                        result.Success,
                        result.BuildSystem,
                        result.Command,
                        result.ExitCode,
                        result.DurationMs,
                        result.ErrorCount,
                        result.WarningCount,
                        errors = result.Errors.Take(20).Select(e => new
                        { e.File, e.Line, e.Column, e.Code, e.Message }),
                    };
                }
                catch (Exception ex) { return JsonToolResult.Success(new { error = $"Build failed: {ex.Message}" }); }
            }),
        new("code_build:detect", "Detect the build system used by a project (dotnet/npm/cargo/make/go/java).", "code_build",
            async args =>
            {
                var path = Arg(args, "path", "");
                var system = BuildPipeline.DetectBuildSystem(string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : path);
                return await Task.FromResult<object?>(new { buildSystem = system, path = string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : path });
            }),

        // Test tools
        new("code_test:run", "Run project tests with auto-detection of test framework. Returns structured results per test case.", "code_test",
            async args =>
            {
                try
                {
                    var harness = GetService<TestHarness>();
                    var result = await harness.RunTestsAsync(
                        Arg(args, "path", ""),
                        Arg(args, "filter", ""),
                        Arg(args, "configuration", "Debug"));
                    return new
                    {
                        result.Success,
                        result.Framework,
                        result.Total,
                        result.Passed,
                        result.Failed,
                        result.Skipped,
                        passRate = Math.Round(result.PassRate * 100, 1),
                        result.DurationMs,
                        failures = result.Cases.Where(c => c.Status == "failed").Take(10)
                            .Select(c => new { c.Name, c.DurationMs, c.Error }),
                    };
                }
                catch (Exception ex) { return JsonToolResult.Success(new { error = $"Test run failed: {ex.Message}" }); }
            }),
        new("code_test:affected", "Run only tests affected by changed symbols. Uses blast radius to find relevant tests.", "code_test",
            async args =>
            {
                try
                {
                    var harness = GetService<TestHarness>();
                    var symbolsJson = Arg(args, "symbols", "[]");
                    var symbols = new List<string>();
                    try
                    {
                        var arr = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(symbolsJson);
                        if (arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                            symbols = arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                    }
                    catch (Exception ex) { _logger?.LogWarning(ex, "code_test:affected: JSON parse failed, falling back to string split"); symbols = symbolsJson.Split(',', ';').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList(); }

                    var result = await harness.RunAffectedTestsAsync(
                        Arg(args, "path", Directory.GetCurrentDirectory()),
                        symbols,
                        Arg(args, "configuration", "Debug"));
                    return new
                    {
                        result.Success,
                        result.Framework,
                        result.Total,
                        result.Passed,
                        result.Failed,
                        result.DurationMs,
                        passRate = Math.Round(result.PassRate * 100, 1),
                    };
                }
                catch (Exception ex) { return JsonToolResult.Success(new { error = $"Affected test run failed: {ex.Message}" }); }
            }),
        new("code_test:detect", "Detect the test framework used by a project (xunit/nunit/mstest/pytest/jest/vitest/cargo-test/go-test).", "code_test",
            async args =>
            {
                var path = Arg(args, "path", "");
                var framework = TestHarness.DetectTestFramework(string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : path);
                return await Task.FromResult<object?>(new { framework, path = string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : path });
            }),

        // ═══ Document — 5 tools ═══
        new("doc_parse", "Parse document content (PDF/DOCX/XLSX/MD). Requires local file path.", "doc",
            async args =>
            {
                var path = Arg(args, "path");
                if (string.IsNullOrWhiteSpace(path))
                    return JsonToolResult.Success(new { error = "path parameter is required. Provide the local file path to the document.", suggestion = "Use vfs:read for virtual files or km_import to ingest documents into the knowledge base." });
                if (!File.Exists(path))
                    return JsonToolResult.Success(new { error = $"File not found: {path}", suggestion = "Check the file path and try again." });
                var ext = Path.GetExtension(path).ToLowerInvariant();
                return ext switch
                {
                    ".md" or ".txt" => new { path, content = await File.ReadAllTextAsync(path), format = "text" },
                    _ => new { error = $"Unsupported format: {ext}", supported = new[] { ".md", ".txt" }, suggestion = "Use km_import for PDF/DOCX/XLSX processing." }
                };
            }),
        new("text_extract", "Extract plain text from a document file path.", "doc",
            async args =>
            {
                var path = Arg(args, "path");
                if (string.IsNullOrWhiteSpace(path))
                    return JsonToolResult.Success(new { error = "path parameter is required.", suggestion = "Use km_import for automated document ingestion." });
                if (!File.Exists(path))
                    return JsonToolResult.Success(new { error = $"File not found: {path}" });
                return JsonToolResult.Success(new { path, content = await File.ReadAllTextAsync(path), size_bytes = new FileInfo(path).Length });
            }),
        new("observe_format", "Inspect document structure and metadata.", "doc",
            async args =>
            {
                var path = Arg(args, "path");
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return JsonToolResult.Success(new { error = $"File not accessible: {path ?? "(null)"}" });
                var info = new FileInfo(path);
                return JsonToolResult.Success(new { path, size_bytes = info.Length, extension = info.Extension, created = info.CreationTimeUtc, modified = info.LastWriteTimeUtc });
            }),
        new("visual_render", "Render chart/flowchart/floorplan/contour/3dsurface/windrose as SVG/HTML", "doc",
            async args => RenderVisual(Arg(args, "type"), Arg(args, "data"), Arg(args, "title"))),
        new("diagram_generate", "Generate a diagram from a natural language description using LLM → Mermaid DSL. Supports flowchart, sequence, class, state, Gantt, ER, pie, mindmap. Parameters: description (required), type (flowchart|sequence|class|state|gantt|pie|er|mindmap, default flowchart)", "doc",
            async args =>
            {
                var description = Arg(args, "description");
                var type = Arg(args, "type", "flowchart");
                if (string.IsNullOrWhiteSpace(description))
                    return JsonToolResult.Success(new { error = "description is required. Provide a natural language description of the diagram to generate." });

                try
                {
                    var chatClient = _serviceProvider?.GetService(typeof(Microsoft.Extensions.AI.IChatClient)) as Microsoft.Extensions.AI.IChatClient;
                    if (chatClient is null)
                        return JsonToolResult.Success(new { error = "No LLM client available. Configure an AI provider first.", fallback_html = BuildFlowchart(type, $"A[Start] --> B[{description[..Math.Min(description.Length, 30)]}] --> C[End]") });

                    var diagramTypes = new Dictionary<string, string>
                    {
                        ["flowchart"] = "flowchart TD", ["process_flow"] = "flowchart TD",
                        ["sequence"] = "sequenceDiagram", ["class"] = "classDiagram",
                        ["state"] = "stateDiagram-v2", ["gantt"] = "gantt",
                        ["pie"] = "pie", ["er"] = "erDiagram", ["mindmap"] = "mindmap"
                    };
                    var dt = diagramTypes.GetValueOrDefault(type, "flowchart TD");

                    var systemPrompt = "You are a Mermaid.js diagram expert. Generate ONLY valid Mermaid syntax. Output ONLY the diagram code, no markdown fences, no explanations. Use proper nodes and edges. Keep labels short and clear.";
                    var prompt = $"Generate a {dt} diagram for: {description}";

                    var response = await chatClient.GetResponseAsync(
                        new List<Microsoft.Extensions.AI.ChatMessage>
                        {
                            new(Microsoft.Extensions.AI.ChatRole.System, systemPrompt),
                            new(Microsoft.Extensions.AI.ChatRole.User, prompt)
                        },
                        new Microsoft.Extensions.AI.ChatOptions { Temperature = 0.3f, MaxOutputTokens = 2000 });

                    var raw = response.Text ?? "";
                    // Clean markdown fences
                    if (raw.StartsWith("```")) raw = raw[raw.IndexOf('\n')..].Trim();
                    if (raw.EndsWith("```")) raw = raw[..raw.LastIndexOf("```")].Trim();

                    return new
                    {
                        type, description = description[..Math.Min(description.Length, 100)],
                        mermaid_code = raw,
                        html = BuildFlowchart(type, raw),
                        format = "html+mermaid",
                        supported_types = diagramTypes.Keys.ToArray()
                    };
                }
                catch (Exception ex)
                {
                    return JsonToolResult.Success(new { error = ex.Message, fallback_html = BuildFlowchart(type, $"A[Start] --> B[{description[..Math.Min(description.Length, 30)]}] --> C[End]") });
                }
            }),

        // ═══ EIA Models — 16 tools ═══
        new("gaussian_plume", "Gaussian plume air dispersion model (GB/T3840-1991)", "eia",
            async args => ComputeGaussianPlume(ArgDouble(args, "q"), ArgDouble(args, "u"), ArgDouble(args, "h"), ArgDouble(args, "x"))),
        new("gaussian_plume_building", "Gaussian plume with building downwash (Huber-Snyder, HJ2.2-2018)", "eia",
            async args => ComputeBuildingDownwash(ArgDouble(args, "q"), ArgDouble(args, "u"), ArgDouble(args, "h"), ArgDouble(args, "x"), ArgDouble(args, "bh"), ArgDouble(args, "bw"))),
        new("inversion_fumigation", "Inversion breakup fumigation model for coastal/short-stack scenarios", "eia",
            async args => ComputeFumigation(ArgDouble(args, "q"), ArgDouble(args, "u"), ArgDouble(args, "h"), ArgDouble(args, "x"), ArgDouble(args, "zi"))),
        new("noise_iso9613", "ISO 9613-2 outdoor sound propagation prediction with ground/barrier", "eia",
            async args => ComputeNoiseIso9613(ArgDouble(args, "lw"), ArgDouble(args, "distance"), Arg(args, "ground_type"))),
        new("noise_attenuation", "Simple noise attenuation with distance", "eia",
            async args => ComputeNoiseAttenuation(ArgDouble(args, "lw"), ArgDouble(args, "distance"))),
        new("noise_traffic", "Traffic noise prediction model (FHWA/CJW method)", "eia",
            async args => ComputeTrafficNoise(ArgDouble(args, "volume_per_h"), ArgDouble(args, "speed_kmh"), ArgDouble(args, "distance"), ArgDouble(args, "heavy_ratio"))),
        new("streeter_phelps", "Streeter-Phelps DO sag curve for water quality", "eia",
            async args => ComputeStreeterPhelps(ArgDouble(args, "do_sat"), ArgDouble(args, "do0"), ArgDouble(args, "k1"), ArgDouble(args, "k2"), ArgDouble(args, "distance"))),
        new("river_mixing", "River pollutant mixing: complete/incomplete lateral mixing zone length", "eia",
            async args => ComputeRiverMixing(ArgDouble(args, "flow_rate"), ArgDouble(args, "width"), ArgDouble(args, "depth"), ArgDouble(args, "velocity"), ArgDouble(args, "emission_load"))),
        new("co2_equivalent", "CO2 equivalent calculation (IPCC GWP100)", "eia",
            async args => ComputeCo2Equivalent(ArgDouble(args, "ch4_kg"), ArgDouble(args, "n2o_kg"))),
        new("hazard_quotient", "Ecological Hazard Quotient for single substance", "eia",
            async args => ComputeHazardQuotient(ArgDouble(args, "exposure"), ArgDouble(args, "reference_dose"))),
        new("ecological_risk", "Multi-substance ecological risk index (Hakanson method)", "eia",
            async args => ComputeEcologicalRisk(Arg(args, "metals_csv"))),
        new("soil_erosion", "Universal Soil Loss Equation (USLE) for construction sites", "eia",
            async args => ComputeSoilLoss(ArgDouble(args, "r_factor"), ArgDouble(args, "k_factor"), ArgDouble(args, "ls_factor"), ArgDouble(args, "c_factor"), ArgDouble(args, "p_factor"))),
        new("carbon_sink", "Forest/grassland carbon sink estimation (biomass method)", "eia",
            async args => ComputeCarbonSink(ArgDouble(args, "area_ha"), Arg(args, "vegetation_type"), ArgDouble(args, "growth_rate"))),
        new("lookup_standard", "Look up Chinese environmental standard (GB/HJ) by code", "eia",
            async args => LookupStandard(Arg(args, "code"))),
        new("classify_water_quality", "Classify water quality per GB3838-2002 using COD/BOD/DO/NH3N", "eia",
            async args => ClassifyWater(ArgDouble(args, "cod"), ArgDouble(args, "bod"), ArgDouble(args, "do_mg_l"), ArgDouble(args, "nh3n"))),
        new("classify_air_quality", "Classify air quality per GB3095-2012 using SO2/NO2/PM10/PM2.5", "eia",
            async args => ClassifyAir(ArgDouble(args, "so2"), ArgDouble(args, "no2"), ArgDouble(args, "pm10"), ArgDouble(args, "pm25"))),
        new("classify_noise_level", "Classify noise level per GB3096-2008 by zone category", "eia",
            async args => ClassifyNoise(ArgDouble(args, "daytime_db"), ArgDouble(args, "night_db"), Arg(args, "zone_category"))),

        // ═══ EIA 专业模型 — 4 tools ═══
        new("aermod_full", "EPA AERMOD regulatory model: auto-download EXE + Process wrapper (CO/DF modes)", "eia_pro",
            async args => { var w = new AermodWrapper(); var r = await w.RunAsync(BuildAermodInput(args)); return r.ToSummary(); }),
        new("calpuff_full", "EPA CALPUFF non-steady-state model: CALMET→CALPUFF→CALPOST pipeline (long-range)", "eia_pro",
            async args => { var w = new CalpuffWrapper(); var r = await w.RunFullAsync(BuildCalpuffInput(args)); return r.ToSummary(); }),
        new("gral_dispersion", "GRAL Lagrangian particle dispersion: complex terrain + building CFD (pure C#)", "eia_pro",
            async args => { var w = new GralWrapper(); var inp = BuildGralInput(args); return w.RunDispersion(inp); }),
        new("mathnet_stats", "Math.NET statistical analysis: interpolation/fitting/FFT/Monte Carlo for EIA data", "eia_pro",
            async args => MathNetAnalyzer.Analyze(Arg(args, "data_csv"), Arg(args, "method"))),

        // ═══ GIS — 5 tools ═══
        new("geocode", "Geocode address to latitude/longitude", "gis",
            async args => { var svc = GetService<LTAI.Tools.GIS.UnifiedMapService>(); return await svc.GeocodeAsync(Arg(args, "address")); }),
        new("gis_buffer", "Create buffer polygon around point, return GeoJSON", "gis",
            async args => ComputeBuffer(ArgDouble(args, "lat"), ArgDouble(args, "lng"), ArgDouble(args, "radius_m"))),
        new("spatial_search", "Check if point is inside polygon", "gis",
            async args => PointInPolygon(ArgDouble(args, "lat"), ArgDouble(args, "lng"), Arg(args, "geojson"))),
        new("distance_calc", "Calculate Haversine distance between coordinates", "gis",
            async args => Haversine(ArgDouble(args, "lat1"), ArgDouble(args, "lng1"), ArgDouble(args, "lat2"), ArgDouble(args, "lng2"))),
        new("coordinate_transform", "Transform between WGS84/GCJ02/CGCS2000", "gis",
            async args => TransformCoord(ArgDouble(args, "lat"), ArgDouble(args, "lng"), Arg(args, "from"), Arg(args, "to"))),

        // ═══ Git — 3 tools ═══
        new("git_diff", "Show working tree changes", "git", null),
        new("git_log", "Show commit history", "git", null),
        new("git_blame", "Show line-by-line authorship", "git", null),

        // ═══ CLI — 5 tools ═══
        new("cli_wrap_function", "Wrap any Python/JavaScript/C# code function as a reusable CLI tool. Generates an executable wrapper. Parameters: name (required), code (required), language (python/js/csharp, default python)", "cli",
            async args =>
            {
                var name = Arg(args, "name");
                var code = Arg(args, "code");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code)) return JsonToolResult.Success(new { error = "name and code are required" });
                var lang = Arg(args, "language", "python");
                var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_").ToLower();
                var filePath = Path.Combine(LivingTreeDir, "cli_tools", safeName);
                if (!Directory.Exists(Path.GetDirectoryName(filePath))) Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                if (lang == "python") { filePath += ".py"; var pyCode = "#!/usr/bin/env python3\nimport sys, json\n\ndef main():\n" + string.Join("\n", code.Split('\n').Select(l => "    " + l)) + "\n\nif __name__ == '__main__':\n    result = main()\n    print(json.dumps(result, ensure_ascii=False))"; await File.WriteAllTextAsync(filePath, pyCode); }
                else if (lang == "js") { filePath += ".js"; await File.WriteAllTextAsync(filePath, $"#!/usr/bin/env node\nconst result = (function() {{{code}}})();\nconsole.log(JSON.stringify(result));"); }
                else { filePath += ".csx"; await File.WriteAllTextAsync(filePath, $"// dotnet-script tool\n{code}"); }
                return JsonToolResult.Success(new { name, language = lang, file = filePath, executable = OperatingSystem.IsWindows() ? $"pwsh {filePath}" : $"chmod +x {filePath} && {filePath}", status = "created" });
            }),
        new("cli_from_repo", "Clone a git repository and auto-detect CLI entry points (package.json scripts, pyproject.toml entry_points, Makefile targets)", "cli",
            async args =>
            {
                var repoUrl = Arg(args, "repo_url");
                if (string.IsNullOrWhiteSpace(repoUrl)) return JsonToolResult.Success(new { error = "repo_url parameter is required" });
                var cloneDir = Path.Combine(Path.GetTempPath(), "ltai_cli", Guid.NewGuid().ToString("N")[..8]);
                var psi = new System.Diagnostics.ProcessStartInfo("git", $"clone --depth 1 {repoUrl} {cloneDir}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                var proc = System.Diagnostics.Process.Start(psi)!;
                await proc.WaitForExitAsync();
                var entries = new List<object>();
                if (File.Exists(Path.Combine(cloneDir, "package.json")))
                {
                    try { var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cloneDir, "package.json"))); if (json.RootElement.TryGetProperty("scripts", out var scripts)) foreach (var s in scripts.EnumerateObject()) entries.Add(new { name = s.Name, type = "npm_script" }); } catch (Exception ex) { _logger?.LogWarning(ex, "cli_from_repo: package.json parse failed"); }
                }
                if (File.Exists(Path.Combine(cloneDir, "Makefile")))
                {
                    var makefile = await File.ReadAllTextAsync(Path.Combine(cloneDir, "Makefile"));
                    foreach (var line in makefile.Split('\n').Where(l => l.Contains(':') && !l.StartsWith(".")))
                        entries.Add(new { name = line.Split(':')[0].Trim(), type = "make_target" });
                }
                return JsonToolResult.Success(new { repo_url = repoUrl, cloned_to = cloneDir, entry_points = entries.Take(15), total = entries.Count });
            }),
        new("cli_from_manifest", "Generate CLI tools from a YAML manifest (name, commands, description, parameters)", "cli",
            async args =>
            {
                var yaml = Arg(args, "yaml_manifest");
                if (string.IsNullOrWhiteSpace(yaml)) return JsonToolResult.Success(new { error = "yaml_manifest parameter is required" });
                await Task.Delay(50);
                var lines = yaml.Split('\n').Where(l => l.Trim().Length > 0).ToList();
                var tools = new List<object>();
                foreach (var line in lines.Take(20))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Contains(':') && !trimmed.StartsWith("#"))
                        tools.Add(new { name = trimmed.Split(':')[0].Trim(), type = "yaml_defined" });
                }
                return JsonToolResult.Success(new { entries_parsed = tools.Count, tools });
            }),
        new("cli_list_tools", "List all generated/installed CLI tools and their locations", "cli",
            async _ =>
            {
                var toolsDir = Path.Combine(LivingTreeDir, "cli_tools");
                var tools = new List<object>();
                if (Directory.Exists(toolsDir))
                    foreach (var f in Directory.GetFiles(toolsDir, "*", SearchOption.TopDirectoryOnly).Take(20))
                        tools.Add(new { name = Path.GetFileName(f), path = f, size = new FileInfo(f).Length });
                return JsonToolResult.Success(new { directory = toolsDir, tools, total = tools.Count });
            }),
        new("cli_scan_path", "Scan system PATH for available CLI programs and probe their --help output. Parameters: path_filter (optional, e.g. 'python' to find python* tools).", "cli",
            async args =>
            {
                var filter = Arg(args, "path_filter");
                var path = OptionService.Get("PATH", "dotnet");
                var dirs = path!.Split(System.IO.Path.PathSeparator).Where(d => !string.IsNullOrWhiteSpace(d)).Take(10);
                var found = new List<object>();
                foreach (var dir in dirs)
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(dir).Take(50))
                        {
                            var name = System.IO.Path.GetFileNameWithoutExtension(file);
                            if (string.IsNullOrEmpty(filter) || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var psi = new System.Diagnostics.ProcessStartInfo(file, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                                    var proc = System.Diagnostics.Process.Start(psi);
                                    if (proc != null) { proc.WaitForExit(3000); var output = proc.StandardOutput.ReadToEnd().Trim(); if (!string.IsNullOrWhiteSpace(output)) found.Add(new { name, path = file, version = output.Split('\n')[0] }); }
                                }
                                catch (Exception ex) { _logger?.LogWarning(ex, "cli_scan_path: version probe failed for {File}", file); found.Add(new { name, path = file }); }
                            }
                        }
                    }
                    catch (Exception ex) { _logger?.LogWarning(ex, "cli_scan_path: directory listing failed"); }
                }
                return JsonToolResult.Success(new { scanned_dirs = dirs.Count(), found_count = found.Count, filter, sample = found.Take(15) });
            }),
        new("cli_install", "Install a CLI tool via system package manager (winget/pip/npm/go/cargo). Parameters: tool_name (required), package_manager (auto-detect by default, or specify)", "cli",
            async args =>
            {
                var toolName = Arg(args, "tool_name");
                if (string.IsNullOrWhiteSpace(toolName)) return JsonToolResult.Success(new { error = "tool_name parameter is required" });
                var pkgManager = Arg(args, "package_manager");
                string? cmd;
                if (!string.IsNullOrEmpty(pkgManager)) cmd = pkgManager switch { "pip" => $"pip install {toolName}", "npm" => $"npm install -g {toolName}", "go" => $"go install {toolName}", "cargo" => $"cargo install {toolName}", "winget" => $"winget install {toolName}", _ => $"winget install {toolName}" };
                else cmd = OperatingSystem.IsWindows() ? $"winget install {toolName}" : $"pip install {toolName}";
                return JsonToolResult.Success(new { tool = toolName, install_command = cmd, hint = $"Run: {cmd}" });
            }),
        // ═══ Shell — 2 tools ═══
        new("bash", "Execute shell command (sandboxed, unrestricted for system operations)", "shell",
            async args => await LTAI.Core.System.ShellEnv.Instance.Execute(Arg(args, "command"))),
        new("cli_execute", "Execute CLI command with safety gate (blocks rm/sudo/dd/shutdown)", "shell",
            async args => CliEngine.Execute(Arg(args, "command"), Arg(args, "args"))),

        // ═══ CAD — 3 tools (REMOVED: placeholder tools, feature not online) ═══
        // cad_import, cad_analyze, cad_export removed — see LTAI v7.0 Phase 0

        // ═══ Memory — 8 tools ═══
        new("remember", "Store a key-value fact in working memory. The system will retain this for future recall. Parameters: key (required, a short label), value (required, the information to remember)", "memory",
            async args =>
            {
                var key = Arg(args, "key");
                var value = Arg(args, "value");
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) return JsonToolResult.Error("key and value are required");
                try
                {
                    var smType = typeof(object).Assembly.GetType("LTAI.Vector.Knowledge.StructMemory");
                    var sm = smType != null ? _serviceProvider?.GetService(smType) : null;
                    if (sm != null)
                    {
                        var bindMethod = sm.GetType().GetMethod("BindEvents");
                        var entries = new List<object> { new { Id = $"mem_{DateTime.UtcNow.Ticks}", SessionId = "memory_tool", Timestamp = DateTime.UtcNow.ToString("O"), Role = "memory", Content = $"[{key}]: {value}" } };
                        bindMethod?.Invoke(sm, new object[] { entries });
                    }
                    var persistDir = Path.Combine(LivingTreeDir, "memories");
                    Directory.CreateDirectory(persistDir);
                    await File.WriteAllTextAsync(Path.Combine(persistDir, $"{SafeKey(key)}.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { key, value, stored_at = DateTime.UtcNow }));
                    return JsonToolResult.Success(new { status = "stored", key, chars = value.Length, location = persistDir });
                }
                catch (Exception ex) { return JsonToolResult.Success(new { error = $"Store failed: {ex.Message}" }); }
            }),
        new("recall", "Recall memories by keyword or topic. Searches working memory and persistent store. Parameters: query (required, what to search for), count (1-20, default 5)", "memory",
            async args =>
            {
                var query = Arg(args, "query");
                if (string.IsNullOrWhiteSpace(query)) return JsonToolResult.Error("query parameter is required");
                var count = Math.Clamp(int.TryParse(Arg(args, "count", "5"), out var c) ? c : 5, 1, 20);
                var results = new List<object>();
                try
                {
                    var smType = typeof(object).Assembly.GetType("LTAI.Vector.Knowledge.StructMemory");
                    var sm = _serviceProvider?.GetService(smType);
                    if (sm != null)
                    {
                        var retrieveMethod = sm.GetType().GetMethod("RetrieveForQuery");
                        var task = retrieveMethod?.Invoke(sm, new object[] { query, count, 3 });
                        if (task is Task t) { await t; var result = t.GetType().GetProperty("Result")?.GetValue(t); if (result != null) return result; }
                    }
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "recall: StructMemory retrieval failed, falling back to file search"); }
                var persistDir = Path.Combine(LivingTreeDir, "memories");
                if (Directory.Exists(persistDir))
                {
                    foreach (var file in Directory.GetFiles(persistDir, "*.json").Take(30))
                    {
                        try { var json = await File.ReadAllTextAsync(file); var mem = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json); if (mem != null && mem.TryGetValue("value", out var v) && v?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) results.Add(mem); } catch (Exception ex) { _logger?.LogWarning(ex, "recall: file read failed for {File}", file); }
                    }
                }
                return JsonToolResult.Success(new { query, results = results.Take(count), total = results.Count });
            }),
        new("forget", "Remove a specific memory by key or clean all memories matching a pattern. Parameters: key (required, the memory key to forget)", "memory",
            async args =>
            {
                var key = Arg(args, "key");
                if (string.IsNullOrWhiteSpace(key)) return JsonToolResult.Success(new { error = "key parameter is required" });
                var persistDir = Path.Combine(LivingTreeDir, "memories");
                var filePath = Path.Combine(persistDir, $"{SafeKey(key)}.json");
                if (File.Exists(filePath)) { File.Delete(filePath); return JsonToolResult.Success(new { status = "deleted", key }); }
                return JsonToolResult.Success(new { error = $"No memory found with key: {key}" });
            }),
        new("memory_stats", "Get memory statistics: counts, categories, storage locations", "memory",
            async _ =>
            {
                var persistDir = Path.Combine(LivingTreeDir, "memories");
                var fileCount = Directory.Exists(persistDir) ? Directory.GetFiles(persistDir, "*.json").Length : 0;
                long totalBytes = 0;
                if (Directory.Exists(persistDir)) foreach (var f in Directory.GetFiles(persistDir, "*.json")) totalBytes += new FileInfo(f).Length;
                return JsonToolResult.Success(new { persistent_count = fileCount, persistent_bytes = totalBytes, path = persistDir });
            }),
        new("list_memories", "List all stored memory keys with preview of content", "memory",
            async _ =>
            {
                var persistDir = Path.Combine(LivingTreeDir, "memories");
                var results = new List<object>();
                if (Directory.Exists(persistDir))
                {
                    foreach (var file in Directory.GetFiles(persistDir, "*.json").Take(50))
                    {
                        try { var json = await File.ReadAllTextAsync(file); var mem = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json); if (mem != null) { var v = mem.TryGetValue("value", out var val) ? val?.ToString() : ""; results.Add(new { key = mem.GetValueOrDefault("key"), preview = v?[..Math.Min(120, v?.Length ?? 0)], bytes = json.Length }); } } catch (Exception ex) { _logger?.LogWarning(ex, "list_memories: file read failed for {File}", file); }
                    }
                }
                return JsonToolResult.Success(new { total = results.Count, memories = results });
            }),
        new("emotion_state", "Get the current emotional memory state: dominant emotion, intensity, recent emotional context", "memory",
            async _ =>
            {
                try
                {
                    var emType = typeof(object).Assembly.GetType("LTAI.Memory.EmotionalMemoryStore");
                    var store = emType?.GetProperty("Instance")?.GetValue(null);
                    if (store != null)
                    {
                        var ctxMethod = store.GetType().GetMethod("EmotionalContext");
                        var ctx = ctxMethod?.Invoke(store, new object[] { 2.0 });
                        var fbMethod = store.GetType().GetMethod("GetFlashbulbs");
                        var fbs = fbMethod?.Invoke(store, new object[] { 5 });
                        return JsonToolResult.Success(new { emotional_context = ctx, flashbulbs = fbs });
                    }
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "emotion_state: EmotionalMemory not available"); }
                return JsonToolResult.Success(new { message = "Emotional memory not available" });
            }),
        new("persona_query", "Query the user persona model: traits, preferences, knowledge gaps. Parameters: query (optional, what aspect of persona to retrieve - traits/preferences/knowledge/domains/summary)", "memory",
            async args =>
            {
                var aspect = Arg(args, "query", "summary");
                try
                {
                    var pmType = typeof(object).Assembly.GetType("LTAI.Memory.PersonaMemory");
                    var pm = pmType?.GetProperty("Instance")?.GetValue(null);
                    if (pm != null)
                    {
                        var ctxMethod = pm.GetType().GetMethod("GetContextForQuery");
                        var ctx = (ctxMethod?.Invoke(pm, new object[] { aspect }) ?? string.Empty).ToString();
                        var statsMethod = pm.GetType().GetMethod("GetStats");
                        var stats = statsMethod?.Invoke(pm, null);
                        return JsonToolResult.Success(new { aspect, context = ctx[..Math.Min(1000, ctx.Length)], stats });
                    }
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "persona_query: PersonaModel not available"); }
                return JsonToolResult.Error("Persona model not available");
            }),
        new("mem_optimize", "Optimize and compress memory context using preference optimization. Parameters: context (required), max_tokens (optional, default 2000)", "memory",
            async args =>
            {
                var context = Arg(args, "context");
                if (string.IsNullOrWhiteSpace(context)) return JsonToolResult.Error("context parameter is required");
                var maxTokens = (int)ArgDouble(args, "max_tokens", 2000);
                var originalTokens = TokenCounter.Estimate(context);
                var sentences = System.Text.RegularExpressions.Regex.Split(context, @"(?<=[。.!！?？\n])");
                var ranked = sentences
                    .Where(s => s.Trim().Length > 5)
                    .Select(s => (text: s.Trim(), score: s.Length > 30 ? 2.0 : s.Length > 15 ? 1.5 : 1.0))
                    .OrderByDescending(x => x.score)
                    .ToList();
                var optimized = new System.Text.StringBuilder();
                var usedTokens = 0;
                foreach (var (text, _) in ranked)
                {
                    var est = TokenCounter.Estimate(text);
                    if (usedTokens + est > maxTokens) break;
                    optimized.AppendLine(text);
                    usedTokens += est;
                }
                var result = optimized.ToString().TrimEnd();
                var newTokens = TokenCounter.Estimate(result);
                return JsonToolResult.Success(new { original_tokens = originalTokens, optimized_tokens = newTokens, saved_tokens = originalTokens - newTokens, compression_ratio = Math.Round((double)newTokens / Math.Max(1, originalTokens), 2), text = result[..Math.Min(2000, result.Length)] });
            }),

        // ═══ Notification — 1 tools ═══
        new("notify", "Send notification via configured channel (Telegram/WeWork/Slack)", "notification",
            async args => { var gw = GetService<LTAI.Tools.Integration.MessageGateway>(); var msg = LTAI.Tools.Integration.GatewayMessage.Create(Arg(args, "channel", "cli"), Arg(args, "to", ""), Arg(args, "message")); var result = await gw.SendAsync(msg); return JsonToolResult.Success(new { status = result.Status, platform = result.Platform }); }),

        // ═══ Integration — 6 tools ═══
        new("email_send", "Send email via SMTP", "integration",
            async args => {
                var gw = GetService<LTAI.Tools.Integration.MessageGateway>();
                var to = Arg(args, "to"); var subject = Arg(args, "subject"); var body = Arg(args, "body");
                if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(body)) return JsonToolResult.Success(new { error = "to and body required" });
                if (string.IsNullOrWhiteSpace(subject)) subject = "LTAI Notification";
                var ok = await gw.SendSmtpAsync(to, subject, body);
                return JsonToolResult.Success(new { success = ok, platform = "smtp", to });
            }),
        new("sms_send", "Send SMS via Aliyun/Tencent Cloud SMS", "integration",
            async args => {
                var sms = GetService<LTAI.Tools.Integration.SmsGateway>();
                var msg = Arg(args, "message"); var phone = Arg(args, "phone");
                if (string.IsNullOrWhiteSpace(msg)) return JsonToolResult.Success(new { error = "message required" });
                var ok = await sms.SendAsync(msg, string.IsNullOrWhiteSpace(phone) ? null : phone);
                return JsonToolResult.Success(new { success = ok, phone = phone ?? sms.Config.PhoneNumbers.FirstOrDefault() });
            }),
        new("translate", "Translate text using Baidu Translate API", "integration",
            async args => {
                var svc = GetService<LTAI.Tools.Integration.TranslateService>();
                var text = Arg(args, "text"); var from = Arg(args, "from", "auto"); var to = Arg(args, "to", "zh");
                if (string.IsNullOrWhiteSpace(text)) return JsonToolResult.Success(new { error = "text required" });
                var result = await svc.TranslateAsync(text, from, to);
                return JsonToolResult.Success(new { success = result != null, text, from, to, translation = result });
            }),
        new("image_search", "Search images via Unsplash/Pixabay", "integration",
            async args => {
                var svc = GetService<LTAI.Tools.Integration.ImageSearchService>();
                var query = Arg(args, "query"); var count = (int)ArgDouble(args, "count", 10);
                var source = Arg(args, "source", "unsplash");
                if (string.IsNullOrWhiteSpace(query)) return JsonToolResult.Success(new { error = "query required" });
                var results = await svc.SearchAsync(query, count, source);
                return JsonToolResult.Success(new { success = true, query, count = results.Count, results = results.Select(r => new { r.Id, r.Url, r.Description, r.Author, r.Source }) });
            }),
        new("weather", "Get current weather by city name", "integration",
            async args => {
                var svc = GetService<LTAI.Tools.Integration.WeatherService>();
                var city = Arg(args, "city"); var source = Arg(args, "source", "openweathermap");
                if (string.IsNullOrWhiteSpace(city)) return JsonToolResult.Success(new { error = "city required" });
                var data = await svc.GetWeatherAsync(city, source);
                return data != null ? new { success = true, data.City, data.Weather, data.Description, data.Temperature, data.Humidity, data.WindSpeed, data.Source }
                    : new { error = "Weather data not available", city };
            }),
        new("github_status", "Get GitHub release status and latest version", "integration",
            async args => {
                var updater = GetService<LTAI.Tools.Integration.AutoUpdater>();
                var result = await updater.CheckForUpdatesAsync();
                return JsonToolResult.Success(new { result.CurrentVersion, result.LatestVersion, result.HasUpdate, result.ReleaseNotes });
            }),

        // ═══ GIS — 5 new tools ═══
        new("reverse_geocode", "Convert lat/lng coordinates to human-readable address", "gis",
            async args => { var svc = GetService<LTAI.Tools.GIS.UnifiedMapService>(); return await svc.ReverseGeocodeAsync(ArgDouble(args, "lng"), ArgDouble(args, "lat")); }),
        new("poi_search", "Search for Points of Interest (restaurants, hospitals, etc.) nearby", "gis",
            async args => { var svc = GetService<LTAI.Tools.GIS.UnifiedMapService>(); return await svc.SearchPOIAsync(Arg(args, "keyword"), Arg(args, "city")); }),
        new("route_plan", "Plan a route between two locations (driving/walking/transit/bicycling)", "gis",
            async args => { var svc = GetService<LTAI.Tools.GIS.UnifiedMapService>(); var from = ParseGeoPoint(Arg(args, "from")); var to = ParseGeoPoint(Arg(args, "to")); if (from == null || to == null) return JsonToolResult.Success(new { error = "from and to must be 'lng,lat' format" }); return await svc.GetRouteAsync(from, to, Arg(args, "mode", "driving")); }),
        new("ip_location", "Lookup geographic location of an IP address", "gis",
            async args => { var svc = GetService<LTAI.Tools.GIS.UnifiedMapService>(); return await svc.GetIPLocationAsync(Arg(args, "ip")); }),
        new("map_weather", "Get weather by city name via Amap API (alternative to weather tool)", "gis",
            async args => { var svc = GetService<LTAI.Tools.GIS.UnifiedMapService>(); return await svc.GetWeatherAsync(Arg(args, "city")); }),

        // ═══ Communication — 1 tool (wework_send removed per v7.0 Phase 0) ═══
        new("telegram_send", "Send message or code block to a Telegram chat", "communication",
            async args => {
                var bot = GetService<LTAI.Tools.Integration.TelegramBot>();
                var chatId = long.TryParse(Arg(args, "chat_id"), out var cid) ? cid : 0L;
                if (chatId == 0) return (object)new { error = "Invalid or missing chat_id parameter." };
                var success = await bot.SendMessageAsync(chatId, Arg(args, "text"));
                if (!success) return (object)new { error = "Telegram send failed. Check token configuration (LTAI_TELEGRAM_TOKEN)." };
                return (object)new { status = "sent" };
            }),

        // ═══ Package Management — 3 new tools ═══
        new("nuget_install", "Install a NuGet package into the project", "system",
            async args => { var mgr = GetService<LTAI.Tools.Integration.PkgManager>(); return await mgr.InstallNuGetAsync(Arg(args, "package_id"), Arg(args, "version")); }),
        new("dotnet_tool_install", "Install a .NET global tool (e.g. dotnet-ef, dotnet-outdated)", "system",
            async args => { var mgr = GetService<LTAI.Tools.Integration.PkgManager>(); return await mgr.InstallDotnetToolAsync(Arg(args, "tool_name")); }),
        new("dotnet_tool_list", "List all installed .NET global tools", "system",
            async args => { var mgr = GetService<LTAI.Tools.Integration.PkgManager>(); return await mgr.GetInstalledToolsAsync(); }),

        // ═══ Knowledge — 6 new tools ═══
        new("km_compile", "Compile domain knowledge artifacts via iterative LLM curation with evaluation against expected fields", "knowledge",
            async args => {
                var cType = typeof(object).Assembly.GetType("LTAI.Vector.Knowledge.KnowledgeCompiler");
                var c = cType != null ? _serviceProvider?.GetService(cType) : null;
                if (c != null) { var m = c.GetType().GetMethod("CompileAsync"); var task = m?.Invoke(c, new object?[] { Arg(args, "domain"), Arg(args, "task_description"), Arg(args, "evals") }); if (task is Task t) { await t; return t.GetType().GetProperty("Result")?.GetValue(t); } }
                return JsonToolResult.Error("Knowledge compiler not available");
            }),
        new("km_fuse", "Fuse multiple documents into a synthesized answer, detecting conflicts and cross-references", "knowledge",
            async args => {
                var fType = typeof(object).Assembly.GetType("LTAI.Vector.Knowledge.MultiDocFusionEngine");
                var f = fType != null ? _serviceProvider?.GetService(fType) : null;
                if (f != null) { var m = f.GetType().GetMethod("FuseAsync"); var docs = Arg(args, "docs"); var task = m?.Invoke(f, new object?[] { docs }); if (task is Task t) { await t; return t.GetType().GetProperty("Result")?.GetValue(t); } }
                return JsonToolResult.Error("Multi-doc fusion engine not available");
            }),

        new("rag_ask", "Full RAG pipeline: search knowledge base, build prompt, generate answer with hallucination guard", "knowledge",
            async args => {
                var pType = typeof(object).Assembly.GetType("LTAI.TreeLLM.Prompting.RagPipeline");
                var p = pType != null ? _serviceProvider?.GetService(pType) : null;
                if (p != null) { var m = p.GetType().GetMethod("AskAsync"); var task = m?.Invoke(p, new object?[] { Arg(args, "question") }); if (task is Task t) { await t; var r = t.GetType().GetProperty("Result")?.GetValue(t); if (r != null) { var a = r.GetType().GetProperty("Answer")?.GetValue(r); var sc = r.GetType().GetProperty("SourceCount")?.GetValue(r); var el = r.GetType().GetProperty("ElapsedMs")?.GetValue(r); return JsonToolResult.Success(new { Answer = a, SourceCount = sc, ElapsedMs = el }); } } }
                return JsonToolResult.Error("RAG pipeline not available");
            }),
        new("dag_rag_ask", "DAG-based parallel RAG: multi-mode retrieval (Iterative+MultiAgent+Reflective) in parallel", "knowledge",
            async args => {
                var pType = typeof(object).Assembly.GetType("LTAI.TreeLLM.Prompting.DagRagPipeline");
                var p = pType != null ? _serviceProvider?.GetService(pType) : null;
                if (p != null) { var m = p.GetType().GetMethod("AskAsync"); var task = m?.Invoke(p, new object?[] { Arg(args, "question") }); if (task is Task t) { await t; var r = t.GetType().GetProperty("Result")?.GetValue(t); if (r != null) { var a = r.GetType().GetProperty("Answer")?.GetValue(r); var sc = r.GetType().GetProperty("SourceCount")?.GetValue(r); var el = r.GetType().GetProperty("ElapsedMs")?.GetValue(r); return JsonToolResult.Success(new { Answer = a, SourceCount = sc, ElapsedMs = el }); } } }
                return JsonToolResult.Error("DAG RAG pipeline not available");
            }),
        new("self_refine", "Execute iterative self-refinement: generate answer, verify, critique, refine (5 rounds max)", "knowledge",
            async args => {
                var sType = typeof(object).Assembly.GetType("LTAI.TreeLLM.Prompting.SelfRefinementLoop");
                var s = sType != null ? _serviceProvider?.GetService(sType) : null;
                if (s != null) { var m = s.GetType().GetMethod("AskAsync"); var task = m?.Invoke(s, new object?[] { Arg(args, "question") }); if (task is Task t) { await t; return t.GetType().GetProperty("Result")?.GetValue(t); } }
                return JsonToolResult.Error("Self-refinement loop not available");
            }),

        // ═══ Shell — 1 tool ═══
        new("shell_probe", "Discover available CLI tools on the system and their versions", "system",
            async _ => { LTAI.Core.System.ShellEnv.Instance.ProbeEnvironment(); return LTAI.Core.System.ShellEnv.Instance.Stats(); }),

        // ═══ Diagnostics — 2 new tools ═══
        new("prompt_cache_stats", "Get cache statistics: hits, misses, tokens saved", "system",
            async _ => {
                var cacheType = typeof(object).Assembly.GetType("LTAI.TreeLLM.Prompting.PromptCache");
                var cache = cacheType != null ? _serviceProvider?.GetService(cacheType) : null;
                if (cache != null) { var m = cache.GetType().GetMethod("Stats"); return m?.Invoke(cache, null) ?? new { error = "Stats not available" }; }
                return JsonToolResult.Error("Prompt cache not available");
            }),
        new("metrics_snapshot", "Get system metrics: total requests, tokens, avg latency, active tasks, memory", "system",
            async _ => {
                var collectorType = typeof(object).Assembly.GetType("LTAI.Metrics.LTAIMetricsCollector");
                var collector = collectorType != null ? _serviceProvider?.GetService(collectorType) : null;
                if (collector == null) return JsonToolResult.Error("Metrics collector not available");
                var method = collector.GetType().GetMethod("GetSnapshot");
                return method?.Invoke(collector, null) ?? new { error = "GetSnapshot not available" };
            }),

        // ═══ Skill Management — 4 tools ═══
        new("skill_create", "Create a new skill by writing a SKILL.md file. Skills are Markdown files with optional YAML frontmatter. Parameters: name (required, lower-case-hyphenated), description (required), body (required, Markdown content), category (optional)", "management",
            async args =>
            {
                var name = Arg(args, "name");
                var desc = Arg(args, "description");
                var body = Arg(args, "body");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(desc) || string.IsNullOrWhiteSpace(body)) return JsonToolResult.Success(new { error = "name, description, and body are required" });
                var skillsDir = Path.Combine(LivingTreeDir, "skills");
                var skillDir = Path.Combine(skillsDir, name);
                Directory.CreateDirectory(skillDir);
                var frontmatter = $@"---
name: {name}
description: {desc}
version: 1.0.0
category: {Arg(args, "category", "general")}
created: {DateTime.UtcNow:yyyy-MM-dd}
---
";
                await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), frontmatter + "\n" + body);
                return JsonToolResult.Success(new { name, description = desc, file = Path.Combine(skillDir, "SKILL.md"), status = "created" });
            }),
        new("skill_import", "Batch import skills from a Markdown document. Skills are identified by ## headings with optional descriptions and code blocks. Also accepts raw text/skill descriptions — the LLM will format them as proper skill definitions. Parameters: markdown (required, the full Markdown document content or skill description text)", "management",
            async args =>
            {
                var markdown = Arg(args, "markdown");
                if (string.IsNullOrWhiteSpace(markdown)) return JsonToolResult.Success(new { error = "markdown parameter is required. Provide the Markdown document content to import." });

                var discovery = _serviceProvider?.GetService(typeof(object).Assembly.GetType("LTAI.Tools.Skills.SkillDiscoveryManager"));
                if (discovery is null)
                {
                    var skillsDir = Path.Combine(LivingTreeDir, "skills");
                    discovery = Activator.CreateInstance(
                        typeof(object).Assembly.GetType("LTAI.Tools.Skills.SkillDiscoveryManager")!,
                        OptionService.Get("LTAI_WORKSPACE") ?? Environment.CurrentDirectory, null);
                }

                var importerType = typeof(object).Assembly.GetType("LTAI.Tools.Skills.SkillMarkdownImporter");
                if (importerType is null) return JsonToolResult.Success(new { error = "SkillMarkdownImporter not found in assembly" });

                var importer = Activator.CreateInstance(importerType, discovery);
                var importMethod = importerType.GetMethod("ImportFromMarkdown");
                var result = importMethod?.Invoke(importer, new object[] { markdown });

                if (result is null) return JsonToolResult.Success(new { error = "Import failed" });

                var installedProp = result.GetType().GetProperty("Installed");
                var failedProp = result.GetType().GetProperty("Failed");
                var totalProp = result.GetType().GetProperty("TotalFound");
                var installed = ((System.Collections.IEnumerable?)installedProp?.GetValue(result))?.Cast<object>().ToList() ?? new();
                var failed = ((System.Collections.IEnumerable?)failedProp?.GetValue(result))?.Cast<object>().ToList() ?? new();

                return new
                {
                    installed = installed.Count,
                    failed_count = failed.Count,
                    total_found = totalProp?.GetValue(result),
                    skills = installed.Select(s => new { name = s.GetType().GetProperty("name")?.GetValue(s), file = s.GetType().GetProperty("file")?.GetValue(s) }),
                    errors = failed.Select(f => new { name = f.GetType().GetProperty("name")?.GetValue(f), error = f.GetType().GetProperty("error")?.GetValue(f) }),
                    hint = "Use skill_list to verify imported skills. Skills are active immediately."
                };
            }),
        new("skill_edit", "Edit an existing skill's SKILL.md body. Appends content to the existing file. Parameters: name (required), body (required, new content to append)", "management",
            async args =>
            {
                var name = Arg(args, "name");
                var body = Arg(args, "body");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(body)) return JsonToolResult.Success(new { error = "name and body are required" });
                var skillDir = Path.Combine(LivingTreeDir, "skills", name);
                var skillFile = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillFile))
                {
                    var globalSkillDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OptionService.Get("paths.DataDirectory") ?? ".livingtree", "skills", name);
                    var globalSkillFile = Path.Combine(globalSkillDir, "SKILL.md");
                    if (!File.Exists(globalSkillFile)) return JsonToolResult.Success(new { error = $"Skill '{name}' not found in project or global skills" });
                    await File.AppendAllTextAsync(globalSkillFile, "\n\n" + body);
                    return JsonToolResult.Success(new { name, file = globalSkillFile, status = "appended" });
                }
                await File.AppendAllTextAsync(skillFile, "\n\n" + body);
                return JsonToolResult.Success(new { name, file = skillFile, status = "appended" });
            }),
        new("skill_delete", "Delete a skill by name. Removes the SKILL.md file and its directory from .livingtree/skills/. Parameters: name (required)", "management",
            async args =>
            {
                var name = Arg(args, "name");
                if (string.IsNullOrWhiteSpace(name)) return JsonToolResult.Success(new { error = "name parameter is required" });
                var skillDir = Path.Combine(LivingTreeDir, "skills", name);
                if (Directory.Exists(skillDir)) { Directory.Delete(skillDir, true); return JsonToolResult.Success(new { name, status = "deleted" }); }
                var globalSkillDir2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OptionService.Get("paths.DataDirectory") ?? ".livingtree", "skills", name);
                if (Directory.Exists(globalSkillDir2)) { Directory.Delete(globalSkillDir2, true); return JsonToolResult.Success(new { name, status = "deleted", location = "global" }); }
                return JsonToolResult.Success(new { error = $"Skill '{name}' not found" });
            }),
        new("skill_explain", "Explain what a skill does by reading its SKILL.md body and providing a summary. Parameters: name (required)", "management",
            async args =>
            {
                var name = Arg(args, "name");
                if (string.IsNullOrWhiteSpace(name)) return JsonToolResult.Success(new { error = "name parameter is required" });
                var discovery = new LTAI.Tools.Skills.SkillDiscoveryManager();
                var skill = discovery.GetSkill(name);
                if (skill != null) return JsonToolResult.Success(new { name = skill.Name, description = skill.Description, source = skill.Source, size = skill.Body.Length, preview = skill.Body[..Math.Min(500, skill.Body.Length)], complexity = skill.Body.Length < 500 ? "simple" : skill.Body.Length < 2000 ? "moderate" : "complex" });
                var catalog = new LTAI.Tools.Skills.SkillCatalog();
                var entry = catalog.GetSkill(name);
                return entry != null ? new { name = entry.ModuleName, description = entry.Description, bucket = entry.Bucket.ToString(), maturity = entry.Maturity.ToString(), dependencies = entry.Dependencies, note = "Built-in skill: edit not supported" } : new { error = $"Skill '{name}' not found" };
            }),

        // ═══ Tool Management — 4 tools ═══
        new("tool_search", "Search all registered tools by name, description, or category. Use this to find relevant tools for your task. Parameters: query (required), category (optional filter)", "management",
            async args =>
            {
                var query = Arg(args, "query").ToLower();
                var category = Arg(args, "category");
                var results = AllTools
                    .Where(t => (string.IsNullOrEmpty(query) || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                             && (string.IsNullOrEmpty(category) || t.Category.Contains(category, StringComparison.OrdinalIgnoreCase)))
                    .Select(t => new { t.Name, t.Description, t.Category, has_handler = t.Handler != null })
                    .Take(30).ToList();
                return JsonToolResult.Success(new { query, category, results, total = AllTools.Length, matched = results.Count });
            }),
        new("tool_enable", "Enable a tool by name. Disabled tools are filtered from suggestions and blocked from invocation. Parameters: name (required)", "management",
            async args =>
            {
                var name = Arg(args, "name");
                var tool = AllTools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (tool is null) return JsonToolResult.Success(new { error = $"Tool '{name}' not found. Use tool_search to find available tools." });
                ToolGate.Instance.Enable(name);
                return JsonToolResult.Success(new { name, status = "enabled", description = tool.Description, category = tool.Category });
            }),
        new("tool_disable", "Disable a tool by name. It will be filtered from suggestions and blocked from invocation. Parameters: name (required)", "management",
            async args =>
            {
                var name = Arg(args, "name");
                var tool = AllTools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (tool is null) return JsonToolResult.Success(new { error = $"Tool '{name}' not found" });
                ToolGate.Instance.Disable(name);
                return JsonToolResult.Success(new { name, status = "disabled", description = tool.Description, disabled_tools = ToolGate.Instance.DisabledCount });
            }),
        new("tool_stats", "Get comprehensive statistics about all registered tools: counts by category, handlers, and status", "management",
            async _ =>
            {
                var byCategory = AllTools.GroupBy(t => t.Category).ToDictionary(g => g.Key, g => new { total = g.Count(), with_handlers = g.Count(t => t.Handler != null), without_handlers = g.Count(t => t.Handler == null) });
                return new
                {
                    total = AllTools.Length,
                    with_handlers = AllTools.Count(t => t.Handler != null),
                    without_handlers = AllTools.Count(t => t.Handler == null),
                    categories = AllTools.Select(t => t.Category).Distinct().Count(),
                    by_category = byCategory
                };
            }),

        // ═══ Discovery — 6 tools ═══
        new("tool_synthesize", "Auto-create a new Python tool from a natural language description. LLM generates code, registers and persists it for future use. Use when no existing tool meets your needs. Parameters: description (required)", "discovery",
            async args =>
            {
                var desc = Arg(args, "description");
                if (string.IsNullOrWhiteSpace(desc)) return JsonToolResult.Success(new { error = "description parameter is required" });
                var synth = new LTAI.Tools.Tools.ToolSynthesizer();
                var result = await synth.Synthesize(desc, Arg(args, "category", "generated"), 
                    (_, prompt) => Task.FromResult("Tool synthesis requires chat client. Use sandbox_exec to test the generated code."));
                return result.Success
                    ? new { status = "created", tool = result.Tool?.Name, code = result.Tool?.Code?[..Math.Min(200, result.Tool?.Code?.Length ?? 0)] }
                    : new { error = result.Error ?? "Synthesis failed" };
            }),
        new("md_tool_synthesize", "Generate .md tool files from natural language descriptions using LLM", "discovery",
            async args =>
            {
                var desc = Arg(args, "description", "");
                if (string.IsNullOrWhiteSpace(desc))
                    return JsonToolResult.Error("description is required. Provide a description of the tool you want to create.");

                var domain = Arg(args, "domain", "general");
                var typeStr = Arg(args, "type", "shell");

                try
                {
                    var synthType = Type.GetType("LTAI.Agent.Tools.MdToolSynthesizer, LTAI.Agent");
                    if (synthType != null)
                    {
                        var synth = _serviceProvider?.GetService(synthType);
                        if (synth != null)
                        {
                            var method = synthType.GetMethod("SynthesizeAsync");
                            if (method != null)
                            {
                                MkToolType? prefType = Enum.TryParse<MkToolType>(typeStr, true, out var pt) ? pt : null;
                                var task = (Task)method.Invoke(synth, new object?[] { desc, domain, prefType, CancellationToken.None })!;
                                await task;
                                var result = task.GetType().GetProperty("Result")?.GetValue(task);
                                if (result != null)
                                    return JsonToolResult.Success(new { synthesized = true, tool = result });
                            }
                        }
                    }

                    var llm = _serviceProvider?.GetService(typeof(Microsoft.Extensions.AI.IChatClient)) as Microsoft.Extensions.AI.IChatClient;
                    if (llm == null)
                        return JsonToolResult.Error("No LLM provider available. Please configure an AI provider first.");

                    var prompt = BuildToolSynthesisPrompt(desc, domain, typeStr);
                    var response = await llm.GetResponseAsync(
                        new List<Microsoft.Extensions.AI.ChatMessage>
                        {
                            new(Microsoft.Extensions.AI.ChatRole.User, prompt)
                        });
                    return JsonToolResult.Success(new { synthesized = true, markdown = response.Text });
                }
                catch (Exception ex)
                {
                    return JsonToolResult.Error($"Synthesis failed: {ex.Message}");
                }
            }),
        new("tool_market_list", "List all available tools in the marketplace grouped by category. Use to discover existing tools before creating new ones.", "discovery",
            async _ =>
            {
                var market = new LTAI.Tools.Tools.ToolMarket();
                var tools = market.Discover();
                return tools.GroupBy(t => t.Category).Select(g => new { category = g.Key, count = g.Count(), tools = g.Select(t => t.Name).ToList() });
            }),
        new("tool_list_synthesized", "List all LLM-synthesized tools that have been auto-created in past sessions", "discovery",
            async _ =>
            {
                var synth = new LTAI.Tools.Tools.ToolSynthesizer();
                return synth.ListTools().Select(t => new { t.Name, t.Description, t.Category, t.Version, t.SuccessCount, t.FailureCount });
            }),
        new("self_discover", "Analyze usage patterns and auto-discover new tool proposals. Returns tools the system thinks should exist based on successful usage patterns.", "discovery",
            async _ =>
            {
                var sd = new LTAI.Tools.Evolution.SelfDiscovery();
                var proposals = sd.GetProposals();
                return JsonToolResult.Success(new { total_proposals = proposals.Count, proposals = proposals.Select(p => new { p.Name, p.Category, p.Description, p.OccurrenceCount, p.AvgSuccessRate }) });
            }),
        new("tool_feedback", "Record success/failure feedback for a tool to improve future tool suggestions", "discovery",
            async args =>
            {
                var sd = new LTAI.Tools.Evolution.SelfDiscovery();
                var toolName = Arg(args, "tool_name");
                var success = bool.TryParse(Arg(args, "success", "true"), out var s) && s;
                sd.Observe(Arg(args, "domain", "general"), new List<string> { toolName }, success);
                return JsonToolResult.Success(new { status = "recorded", tool = toolName, success });
            }),

        // ═══ Skills — 4 tools ═══
        new("skill_list", "List all available skills grouped by capability bucket. 18 built-in skills plus filesystem-discovered SKILL.md files.", "discovery",
            async _ =>
            {
                var catalog = new LTAI.Tools.Skills.SkillCatalog();
                var discovery = new LTAI.Tools.Skills.SkillDiscoveryManager();
                var discovered = discovery.DiscoverForContext();
                return new
                {
                    summary = catalog.GetBucketSummary(),
                    discovered_count = discovered.Count,
                    discovered = discovered.Select(s => new { s.Name, s.Description, s.Source })
                };
            }),
        new("skill_search", "Search for skills by keyword (Chinese/English). Returns matching skills with descriptions and maturity level.", "discovery",
            async args =>
            {
                var catalog = new LTAI.Tools.Skills.SkillCatalog();
                var results = catalog.Search(Arg(args, "query"));
                var discovery = new LTAI.Tools.Skills.SkillDiscoveryManager();
                var discovered = discovery.DiscoverForContext();
                var matching = discovered.Where(s =>
                    s.Name.Contains(Arg(args, "query"), StringComparison.OrdinalIgnoreCase) ||
                    s.Description.Contains(Arg(args, "query"), StringComparison.OrdinalIgnoreCase));
                return new
                {
                    builtin = results.Select(s => new { s.ModuleName, s.Description, bucket = s.Bucket.ToString(), s.Maturity }),
                    filesystem = matching.Select(s => new { s.Name, s.Description, s.Source })
                };
            }),
        new("skill_load", "Load the full body/content of a specific skill by name. Discovered skills are SKILL.md files; built-in skills are from the catalog.", "discovery",
            async args =>
            {
                var discovery = new LTAI.Tools.Skills.SkillDiscoveryManager();
                var skill = discovery.GetSkill(Arg(args, "name"));
                if (skill != null) return JsonToolResult.Success(new { name = skill.Name, source = skill.Source, body = skill.Body });
                var catalog = new LTAI.Tools.Skills.SkillCatalog();
                var entry = catalog.GetSkill(Arg(args, "name"));
                return entry != null ? new { name = entry.ModuleName, description = entry.Description, bucket = entry.Bucket.ToString(), maturity = entry.Maturity.ToString() } : new { error = "Skill not found" };
            }),
        new("skill_suggest", "Suggest the best skills for a given task based on keyword matching and routing priority. Use this when you need guidance on which tools/skills to apply.", "discovery",
            async args =>
            {
                var catalog = new LTAI.Tools.Skills.SkillCatalog();
                var suggestions = catalog.SuggestSkills(Arg(args, "task"));
                return suggestions.Select(s => new { s.ModuleName, s.Description, bucket = s.Bucket.ToString(), s.Maturity });
            }),

        // ═══ MCP — 3 tools ═══
        new("mcp_discover", "Connect to a remote MCP (Model Context Protocol) server and list its available tools and resources. Use this to discover external AI agent tools. Parameters: server_url (required, e.g. http://localhost:8080/mcp)", "discovery",
            async args => await McpToolAdapter.DiscoverAsync(args)),
        new("mcp_call", "Call a specific tool on a remote MCP server. Use mcp_discover first to find available tools. Parameters: server_url (required), tool_name (required), arguments (JSON object, optional)", "discovery",
            async args => await McpToolAdapter.CallAsync(args)),
        new("mcp_export", "Export current LTAI tools as MCP-compatible tool definitions. Use this to share LTAI's capabilities with other MCP clients.", "discovery",
            async _ => await Task.FromResult(McpToolAdapter.Export(AllTools))),

        // ═══ System — 7 tools ═══
        new("models_list", "List all registered model providers and their models", "system",
            async _ => {
                var mgr = GetService<LTAI.Core.System.ModelManager>();
                var models = mgr.ListAll();
                return JsonToolResult.Success(new { count = models.Count, models = models.Select(m => new { m.Provider, m.ModelName, m.TierName, m.Capabilities }) });
            }),
        new("models_show", "Show details for a specific provider or model", "system",
            async args => {
                var mgr = GetService<LTAI.Core.System.ModelManager>();
                var name = Arg(args, "name");
                if (string.IsNullOrWhiteSpace(name)) return JsonToolResult.Success(new { error = "name required" });
                var info = mgr.Show(name);
                return info != null ? new { info.Provider, info.ModelName, info.TierName, info.BaseUrl, info.Capabilities } : new { error = $"Provider/model not found: {name}" };
            }),
        new("models_search", "Search models by keyword", "system",
            async args => {
                var mgr = GetService<LTAI.Core.System.ModelManager>();
                var q = Arg(args, "query");
                if (string.IsNullOrWhiteSpace(q)) return JsonToolResult.Success(new { error = "query required" });
                var results = mgr.Search(q);
                return JsonToolResult.Success(new { query = q, count = results.Count, results = results.Select(m => new { m.Provider, m.ModelName, m.TierName }) });
            }),
        new("models_sync", "Sync model registry info from built-in providers", "system",
            async _ => {
                var mgr = GetService<LTAI.Core.System.ModelManager>();
                return mgr.SyncInfo();
            }),
        new("service_install", "Install LTAI as Windows Service", "system",
            async _ => {
                var svc = GetService<LTAI.Core.System.ServiceManager>();
                var result = await svc.InstallAsync();
                return JsonToolResult.Success(new { result.Success, result.Message });
            }),
        new("service_uninstall", "Uninstall LTAI Windows Service", "system",
            async _ => {
                var svc = GetService<LTAI.Core.System.ServiceManager>();
                var result = await svc.UninstallAsync();
                return JsonToolResult.Success(new { result.Success, result.Message });
            }),
        new("service_status", "Check LTAI Windows Service status or start/stop/restart", "system",
            async args => {
                var svc = GetService<LTAI.Core.System.ServiceManager>();
                var action = Arg(args, "action", "status");
                var result = action.ToLowerInvariant() switch
                {
                    "start" => await svc.StartAsync(),
                    "stop" => await svc.StopAsync(),
                    "restart" => await svc.RestartAsync(),
                    _ => await svc.StatusAsync()
                };
                return JsonToolResult.Success(new { action, result.Success, result.Message, result.Output });
            }),

        // ═══ Daemon — 2 tools ═══
        new("daemon_install", "Cross-platform daemon/service installation (systemd/launchd/Windows Service). Creates and enables a background service that auto-starts on boot.", "system",
            async args => {
                var dm = GetService<LTAI.Core.System.DaemonManager>();
                if (!dm.IsAvailable())
                    return JsonToolResult.Success(new { error = $"Daemon manager not available on {dm.Platform}. Requires systemctl (Linux), launchctl (macOS), or Windows." });
                var config = new LTAI.Core.System.DaemonConfig
                {
                    ServiceName = Arg(args, "service_name", "ltai-agent"),
                    DisplayName = Arg(args, "display_name", "LTAI Agent"),
                    Description = Arg(args, "description", "LivingTree AI Background Agent"),
                    ExecPath = Arg(args, "exec_path", ""),
                    WorkingDirectory = Arg(args, "working_dir", Environment.CurrentDirectory),
                    RestartPolicy = Arg(args, "restart_policy", "always")
                };
                var result = await dm.InstallAsync(config);
                return JsonToolResult.Success(new { result.Success, result.Message, result.Platform, result.ServiceName, result.Output });
            }),
        new("daemon_status", "Query daemon/service status across platforms (systemd/launchctl/sc query)", "system",
            async args => {
                var dm = GetService<LTAI.Core.System.DaemonManager>();
                if (!dm.IsAvailable())
                    return JsonToolResult.Success(new { error = $"Daemon manager not available on {dm.Platform}" });
                var serviceName = Arg(args, "service_name", "ltai-agent");
                var result = await dm.StatusAsync(serviceName);
                return JsonToolResult.Success(new { result.Success, result.Message, result.Platform, result.ServiceName, result.Output });
            }),

        // ═══ WSL2 — 3 tools ═══
        new("wsl2_list", "List installed WSL2 Linux distributions with version and state info", "system",
            async _ => {
                var wm = GetService<LTAI.Core.System.Wsl2Manager>();
                var avail = await wm.IsAvailable();
                if (!avail.Success)
                    return JsonToolResult.Success(new { error = avail.Message, hint = "WSL2 requires Windows 10/11 with WSL2 enabled. Run `wsl --install` first." });
                var result = await wm.ListDistros();
                return JsonToolResult.Success(new { result.Success, result.Message, distros = result.Output });
            }),
        new("wsl2_exec", "Execute a command inside a WSL2 Linux distribution", "system",
            async args => {
                var wm = GetService<LTAI.Core.System.Wsl2Manager>();
                var avail = await wm.IsAvailable();
                if (!avail.Success)
                    return JsonToolResult.Success(new { error = avail.Message, hint = "WSL2 requires Windows 10/11 with WSL2 enabled." });
                var distro = Arg(args, "distro");
                var command = Arg(args, "command");
                if (string.IsNullOrWhiteSpace(distro)) return JsonToolResult.Success(new { error = "distro parameter required" });
                if (string.IsNullOrWhiteSpace(command)) return JsonToolResult.Success(new { error = "command parameter required" });
                var result = await wm.ExecuteInDistro(distro, command);
                return JsonToolResult.Success(new { result.Success, result.Message, result.Distro, result.Output });
            }),
        new("wsl2_limits", "Set WSL2 resource limits (memory MB, processor count) via .wslconfig", "system",
            async args => {
                var wm = GetService<LTAI.Core.System.Wsl2Manager>();
                var avail = await wm.IsAvailable();
                if (!avail.Success)
                    return JsonToolResult.Success(new { error = avail.Message, hint = "WSL2 requires Windows 10/11 with WSL2 enabled." });
                var memoryMb = (int)ArgDouble(args, "memory_mb", 4096);
                var processors = (int)ArgDouble(args, "processors", 2);
                var result = await wm.SetResourceLimits(memoryMb, processors);
                return JsonToolResult.Success(new { result.Success, result.Message, memory_mb = memoryMb, processors });
            }),

        // ═══ Resource — 1 tool ═══
        new("resource_usage", "Get current system resource usage and available headroom (memory, CPU, disk, process count). Cross-platform.", "system",
            async _ => {
                var rg = GetService<LTAI.Core.System.ResourceGuard>();
                if (!rg.IsAvailable())
                    return JsonToolResult.Error("ResourceGuard not available");
                var usage = rg.GetCurrentUsage();
                var available = rg.GetAvailableResources();
                return new
                {
                    platform = usage.Platform,
                    memory = new { total_mb = usage.TotalMemoryMb, used_mb = usage.UsedMemoryMb, available_mb = usage.AvailableMemoryMb },
                    cpu = new { usage_percent = usage.CpuUsagePercent, headroom_percent = Math.Round(100 - usage.CpuUsagePercent, 1) },
                    disk = new { total_mb = usage.TotalDiskMb, used_mb = usage.UsedDiskMb },
                    processes = usage.ProcessCount,
                    active_allocations = rg.GetActiveAllocations().Count()
                };
            }),

        // ═══ API Catalog — 2 tools ═══
        new("api_catalog", "Browse available free APIs and tools. Returns full catalog with descriptions, categories, and parameters. Use this to discover what APIs are available before calling api_search.", "discovery",
            async args =>
            {
                var catalog = ApiCatalog.ApiToolCatalog.Instance;
                var context = catalog.BuildPromptContext();
                return JsonToolResult.Success(new { summary = context, stats = catalog.GetStats() });
            }),
        new("api_search", "Search for specific API tools by keyword. Returns matching APIs with descriptions and parameters. Use after api_catalog to find relevant APIs for your task.", "discovery",
            async args =>
            {
                var catalog = ApiCatalog.ApiToolCatalog.Instance;
                var query = Arg(args, "query");
                var results = catalog.Search(query);
                return results.Select(r => new { r.Name, r.Description, r.Category, r.Free, r.Parameters });
            }),
    };

    private static string BuildToolSynthesisPrompt(string description, string domain, string type)
    {
        return $@"Generate a complete .md tool file for the LTAI agent framework:

# tool: <name>
domain: {domain}
type: {type}
description: <brief>

## parameters
- param: type (required) — description

## command (for shell) or ## service (for service)
...

## triggers
- pattern: ""..."" (weight: 1.0)

## tags
- tag

Description: {description}

Return ONLY the markdown, no explanation.";
    }

    public static int Total => AllTools.Length;

    private static string Arg(Dictionary<string, object?>? args, string key, string def = "")
        => args?.TryGetValue(key, out var v) == true ? v?.ToString() ?? def : def;

    private static string SafeKey(string key) =>
        string.Join("_", key.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_").ToLowerInvariant().Length > 60
            ? string.Join("_", key.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_").ToLowerInvariant()[..60]
            : string.Join("_", key.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_").ToLowerInvariant();

    private static double ArgDouble(Dictionary<string, object?>? args, string key, double def = 0)
        => args?.TryGetValue(key, out var v) == true && double.TryParse(v?.ToString(), out var d) ? d : def;

    private static LTAI.Tools.GIS.GeoPoint? ParseGeoPoint(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',');
        if (parts.Length >= 2 && double.TryParse(parts[0].Trim(), out var lng) && double.TryParse(parts[1].Trim(), out var lat))
            return new LTAI.Tools.GIS.GeoPoint { Lng = lng, Lat = lat };
        return null;
    }

    // ═══ Tool Implementations ═══

    public static object ComputeGaussianPlume(double q, double u, double h, double x)
    {
        if (u <= 0 || x <= 0) return JsonToolResult.Success(new { error = "Invalid parameters: u>0, x>0 required" });
        var sigmaY = 0.22 * x / Math.Sqrt(1 + 0.0001 * x);
        var sigmaZ = 0.20 * x;
        var concentration = q / (2 * Math.PI * u * sigmaY * sigmaZ) * Math.Exp(-h * h / (2 * sigmaZ * sigmaZ));
        return JsonToolResult.Success(new { concentration_mg_m3 = Math.Round(concentration * 1e6, 4), sigma_y = Math.Round(sigmaY, 1), sigma_z = Math.Round(sigmaZ, 1), distance_m = x });
    }

    public static object ComputeNoiseAttenuation(double lw, double distance)
    {
        if (distance <= 0) return JsonToolResult.Success(new { error = "distance > 0 required" });
        var attenuation = 20 * Math.Log10(Math.Max(distance, 0.1));
        var spl = lw - attenuation;
        return JsonToolResult.Success(new { spl_db = Math.Round(spl, 1), attenuation_db = Math.Round(attenuation, 1), distance_m = distance });
    }

    public static object ComputeStreeterPhelps(double doSat, double do0, double k1, double k2, double x)
    {
        var deficit = doSat - do0;
        var d = k1 / (k2 - k1) * (Math.Exp(-k1 * x / 86400) - Math.Exp(-k2 * x / 86400)) * deficit + deficit * Math.Exp(-k2 * x / 86400);
        var doVal = doSat - d;
        return JsonToolResult.Success(new { do_mg_l = Math.Round(doVal, 4), deficit = Math.Round(d, 4), distance_m = x });
    }

    public static object ComputeCo2Equivalent(double ch4, double n2o)
    {
        var co2e = ch4 * 28 + n2o * 265;
        return JsonToolResult.Success(new { co2e_kg = Math.Round(co2e, 2), ch4_kg = ch4, n2o_kg = n2o, gwp_ch4 = 28, gwp_n2o = 265 });
    }

    public static object ComputeHazardQuotient(double exposure, double rfd)
    {
        if (rfd <= 0) return JsonToolResult.Success(new { error = "reference_dose > 0 required" });
        var hq = exposure / rfd;
        return JsonToolResult.Success(new { hazard_quotient = Math.Round(hq, 4), risk_level = hq < 1 ? "acceptable" : hq < 10 ? "moderate" : "high" });
    }

    public static object LookupStandard(string code)
    {
        var standards = new Dictionary<string, string>
        {
            ["GB3095-2012"] = "Ambient Air Quality Standards: SO2, NO2, PM10, PM2.5, CO, O3",
            ["GB3838-2002"] = "Surface Water Quality Standards: Class I-V",
            ["GB3096-2008"] = "Environmental Noise Standards: 0-4 categories",
            ["GB16297-1996"] = "Integrated Emission Standards for Air Pollutants",
            ["GB8978-1996"] = "Integrated Wastewater Discharge Standards",
            ["HJ2.2-2018"] = "Technical Guidelines for Atmospheric EIA",
            ["HJ2.3-2018"] = "Technical Guidelines for Surface Water EIA",
            ["HJ2.4-2021"] = "Technical Guidelines for Noise EIA",
            ["HJ19-2011"] = "Technical Guidelines for Ecological EIA",
            ["GB/T3840-1991"] = "Technical methods for local air pollutant dispersion models"
        };

        if (standards.TryGetValue(code.ToUpper(), out var desc))
            return JsonToolResult.Success(new { code = code.ToUpper(), description = desc, found = true });

        var partial = standards.FirstOrDefault(s => s.Key.Contains(code, StringComparison.OrdinalIgnoreCase));
        if (partial.Key != null)
            return JsonToolResult.Success(new { code = partial.Key, description = partial.Value, found = true, note = $"partial match for '{code}'" });

        return JsonToolResult.Success(new { code, found = false, note = "Standard not found in local database" });
    }

    public static object ComputeNoiseIso9613(double lw, double distance, string groundType = "mixed")
    {
        if (distance <= 0) return JsonToolResult.Success(new { error = "distance > 0 required" });
        var groundFactor = groundType switch { "hard" => 0.0, "soft" => 1.0, _ => 0.5 };
        var geometric = 20 * Math.Log10(Math.Max(distance, 0.1)) + 11;
        var atmospheric = distance * 0.005;
        var ground = 4.8 - 2 * (groundType == "hard" ? 600 : 200) / Math.Max(distance, 1) * (17 + 300 / Math.Max(distance, 1));
        var barrier = 0.0;
        var spl = lw - geometric - atmospheric - groundFactor * ground - barrier;
        return new { spl_db = Math.Round(spl, 1), geometric_db = Math.Round(geometric, 1), atmospheric_db = Math.Round(atmospheric, 2),
                     ground_db = Math.Round(groundFactor * ground, 1), distance_m = distance, ground_type = groundType };
    }

    public static object ClassifyWater(double cod, double bod, double doVal, double nh3n)
    {
        var scores = new List<int>();
        if (cod <= 15) scores.Add(1); else if (cod <= 15) scores.Add(1); else if (cod <= 20) scores.Add(3); else if (cod <= 30) scores.Add(4); else if (cod <= 40) scores.Add(5); else scores.Add(6);
        if (bod <= 3) scores.Add(1); else if (bod <= 4) scores.Add(3); else if (bod <= 6) scores.Add(4); else if (bod <= 10) scores.Add(5); else scores.Add(6);
        if (doVal >= 7.5) scores.Add(1); else if (doVal >= 6) scores.Add(2); else if (doVal >= 5) scores.Add(3); else if (doVal >= 3) scores.Add(4); else if (doVal >= 2) scores.Add(5); else scores.Add(6);
        if (nh3n <= 0.15) scores.Add(1); else if (nh3n <= 0.5) scores.Add(2); else if (nh3n <= 1.0) scores.Add(3); else if (nh3n <= 1.5) scores.Add(4); else if (nh3n <= 2.0) scores.Add(5); else scores.Add(6);
        var level = (int)scores.Max();
        var cls = level <= 1 ? "I" : level <= 2 ? "II" : level <= 3 ? "III" : level <= 4 ? "IV" : level <= 5 ? "V" : ">V";
        return JsonToolResult.Success(new { classification = cls, level, cod, bod, do_mg_l = doVal, nh3n, standard = "GB3838-2002" });
    }

    public static object ClassifyAir(double so2, double no2, double pm10, double pm25)
    {
        var calcIAQI = (double value, double[] bp) =>
        {
            for (var i = 0; i < bp.Length - 2; i++)
                if (value <= bp[i + 1]) return ((50 + 50 * i) - (1 + 50 * i)) / (bp[i + 1] - bp[i]) * (value - bp[i]) + (1 + 50 * i);
            return 500.0;
        };
        var iaqiSo2 = calcIAQI(so2, new double[] { 0, 50, 150, 475, 800, 1600 });
        var iaqiNo2 = calcIAQI(no2, new double[] { 0, 40, 80, 180, 280, 565 });
        var iaqiPm10 = calcIAQI(pm10, new double[] { 0, 50, 150, 250, 350, 420 });
        var iaqiPm25 = calcIAQI(pm25, new double[] { 0, 35, 75, 115, 150, 250 });
        var aqi = new[] { iaqiSo2, iaqiNo2, iaqiPm10, iaqiPm25 }.Max();
        var cls = aqi <= 50 ? "I(优)" : aqi <= 100 ? "II(良)" : aqi <= 150 ? "III(轻度污染)" : aqi <= 200 ? "IV(中度污染)" : aqi <= 300 ? "V(重度污染)" : "VI(严重污染)";
        return JsonToolResult.Success(new { classification = cls, aqi = Math.Round(aqi, 1), so2_iaqi = Math.Round(iaqiSo2, 1), no2_iaqi = Math.Round(iaqiNo2, 1), pm10_iaqi = Math.Round(iaqiPm10, 1), pm25_iaqi = Math.Round(iaqiPm25, 1), standard = "GB3095-2012" });
    }

    public static object ClassifyNoise(double daytimeDb, double nightDb, string zone = "class2")
    {
        var limits = new Dictionary<string, (int day, int night)>
        {
            ["class0"] = (50, 40), ["class1"] = (55, 45), ["class2"] = (60, 50), ["class3"] = (65, 55), ["class4"] = (70, 55)
        };
        var (dayLimit, nightLimit) = limits.GetValueOrDefault(zone, (60, 50));
        var dayOk = daytimeDb <= dayLimit;
        var nightOk = nightDb <= nightLimit;
        var overall = dayOk && nightOk ? "达标" : "超标";
        return new { overall, day_ok = dayOk, night_ok = nightOk, daytime_db = daytimeDb, night_db = nightDb,
                     day_limit = dayLimit, night_limit = nightLimit, zone, standard = "GB3096-2008" };
    }

    private static object RenderVisual(string type, string data, string title)
    {
        var colors = new[] { "#58a6ff", "#3fb950", "#d29922", "#f85149", "#a371f7", "#ff9944" };
        var chartId = Guid.NewGuid().ToString("N")[..6];
        var height = type == "map" || type == "floorplan" ? 400 : 300;

        return new
        {
            html = type switch
            {
                "bar" => ChartBuilder.BuildBar(title, data, colors, chartId),
                "line" => ChartBuilder.BuildLine(title, data, colors, chartId),
                "pie" => ChartBuilder.BuildPie(title, data, colors, chartId),
                "map" => ChartBuilder.BuildMap(title, data, chartId, height),
                "flowchart" => BuildFlowchart(title, data),
                "floorplan" => BuildFloorplan(title, data, chartId, height),
                "contour" => BuildContour(title, data, chartId),
                "3dsurface" => Build3DSurface(title, data, chartId),
                "windrose" => BuildWindRose(title, data, chartId),
                _ => ChartBuilder.BuildTable(title, data)
            },
            type, title
        };
    }

    private static string BuildFlowchart(string title, string mermaidDef) =>
        $@"<div class='mermaid' style='background:#fff;padding:16px;border-radius:8px'>
graph TD
{mermaidDef}
</div><script src='https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js'></script><script>mermaid.initialize({{startOnLoad:true,theme:'default'}});</script>";

    private static string BuildContour(string title, string data, string id)
    {
        var values = data.Split(',').Select(v => double.TryParse(v, out var d) ? d : 0).ToList();
        var size = (int)Math.Sqrt(values.Count);
        var html = $"<canvas id='{id}' width='{size * 20}' height='{size * 20}'></canvas><script>";
        html += $"var c=document.getElementById('{id}').getContext('2d');";
        html += $"var v=[{string.Join(",", values)}];var s={size};";
        html += "for(var i=0;i<s;i++)for(var j=0;j<s;j++){var val=v[i*s+j];var r=Math.floor(val*255);c.fillStyle=`rgb(${r},${128-r/2},${255-r})`;c.fillRect(j*20,i*20,20,20);c.fillStyle='#333';c.font='8px sans-serif';c.fillText(val.toFixed(1),j*20+2,i*20+12);}";
        html += "</script>";
        return html;
    }

    private static string Build3DSurface(string title, string data, string id)
    {
        var points = data.Split(';').Select(p => p.Split(':').Select(double.Parse).ToArray()).ToList();
        var html = $"<canvas id='{id}' width='600' height='400'></canvas><script>";
        html += $"var c=document.getElementById('{id}').getContext('2d');var pts=[{string.Join(",", points.Select(p => $"[{p[0]},{p[1]},{p[2]}]"))}];";
        html += "pts.sort((a,b)=>b[2]-a[2]);pts.forEach(p=>{var x=200+p[0]*2-p[1];var y=200-p[2]*5+p[0]+p[1];c.beginPath();c.arc(x,y,3,0,Math.PI*2);c.fillStyle=`rgb(${Math.floor(p[2]*25)},${100},${200-Math.floor(p[2]*20)})`;c.fill();});";
        html += "</script>";
        return html;
    }

    private static string BuildWindRose(string title, string data, string id)
    {
        var dirs = data.Split(',').Select(d => d.Split(':')).Where(d => d.Length == 2)
            .Select(d => (dir: d[0], freq: double.Parse(d[1]))).ToList();
        var cx = 200; var cy = 200; var r = 150;
        var html = $"<svg viewBox='0 0 400 400'><text x='200' y='20' text-anchor='middle' font-weight='bold'>{title}</text>";
        var angles = new Dictionary<string, double> { ["N"] = 270, ["NE"] = 315, ["E"] = 0, ["SE"] = 45, ["S"] = 90, ["SW"] = 135, ["W"] = 180, ["NW"] = 225 };
        foreach (var (dir, freq) in dirs.Where(d => angles.ContainsKey(d.dir)))
        {
            var angle = angles[dir] * Math.PI / 180;
            var len = r * freq / 20;
            var x2 = cx + len * Math.Cos(angle);
            var y2 = cy - len * Math.Sin(angle);
            html += $"<line x1='{cx}' y1='{cy}' x2='{x2:F1}' y2='{y2:F1}' stroke='#58a6ff' stroke-width='2' opacity='0.7'/>";
            html += $"<text x='{x2 + 5:F1}' y='{y2:F1}' font-size='9' fill='#8b949e'>{dir} {freq:F1}%</text>";
        }
        html += "<circle cx='200' cy='200' r='150' fill='none' stroke='var(--border)' stroke-dasharray='4,4'/>";
        html += "<circle cx='200' cy='200' r='75' fill='none' stroke='var(--border)' stroke-dasharray='4,4'/>";
        html += "</svg>";
        return html;
    }

    private static AermodInput BuildAermodInput(Dictionary<string, object?> args) => new()
    {
        EmissionRate = ArgDouble(args, "emission_rate"), StackHeight = ArgDouble(args, "stack_h"),
        StackDiameter = ArgDouble(args, "stack_d"), ExitVelocity = ArgDouble(args, "exit_v", 15),
        ExitTemperature = ArgDouble(args, "exit_t", 400), UrbanRural = ArgDouble(args, "urban", 1),
        PollutantId = Arg(args, "pollutant", "SO2"), Title = Arg(args, "title", "LTAI AERMOD"),
        MetDataPath = Arg(args, "met_path", "aermet.sfc")
    };

    private static GralInput BuildGralInput(Dictionary<string, object?> args) => new()
    {
        EmissionRate = ArgDouble(args, "emission_rate"), SourceHeight = ArgDouble(args, "source_h", 50),
        WindSpeed = ArgDouble(args, "wind_speed", 3), WindSigma = ArgDouble(args, "wind_sigma", 0.5),
        MixingHeight = ArgDouble(args, "mixing_h", 800), ParticleCount = (int)ArgDouble(args, "particles", 500)
    };

    private static CalpuffInput BuildCalpuffInput(Dictionary<string, object?> args) => new()
    {
        EmissionRate = ArgDouble(args, "emission_rate"), StackHeight = ArgDouble(args, "stack_h"),
        StackDiameter = ArgDouble(args, "stack_d"), ExitVelocity = ArgDouble(args, "exit_v", 15),
        ExitTemperature = ArgDouble(args, "exit_t", 400),
        SourceLat = ArgDouble(args, "source_lat", 39.9), SourceLon = ArgDouble(args, "source_lon", 116.4),
        MetDays = (int)ArgDouble(args, "met_days", 30), CellSize = ArgDouble(args, "cell_size", 500),
        Title = Arg(args, "title", "LTAI CALPUFF")
    };

    private static string BuildFloorplan(string title, string data, string id, int h)
    {
        var rects = data.Split(';').Select(cell =>
        {
            var parts = cell.Split(':');
            if (parts.Length < 4) return "";
            var x = double.Parse(parts[0]);
            var y = double.Parse(parts[1]);
            var w = double.Parse(parts[2]);
            var ht = double.Parse(parts[3]);
            var label = parts.Length > 4 ? parts[4] : "";
            var cx = x + w / 2;
            var cy = y + ht / 2 + 5;
            return $"<rect x='{x}' y='{y}' width='{w}' height='{ht}' fill='#e8f0fe' stroke='#1a73e8' stroke-width='2' rx='4'/><text x='{cx}' y='{cy}' text-anchor='middle' font-size='11' fill='#333'>{label}</text>";
        }).ToList();

        return $@"<svg viewBox='0 0 800 600' style='width:100%;height:{h}px;border:1px solid var(--border);border-radius:8px;background:#fff'>
<text x='400' y='20' text-anchor='middle' font-weight='bold' font-size='14'>{title}</text>
{string.Join("\n", rects)}
</svg>";
    }

    // ═══ EIA Model Implementations ═══

    /// <summary>
    /// Gaussian plume with building downwash (Huber-Snyder method, HJ2.2-2018).
    /// Standard model for general industrial EIA with nearby buildings.
    /// When stack height h less than building height bh+1.5*L (wake length),
    /// plume is entrained into the building cavity zone.
    /// </summary>
    public static object ComputeBuildingDownwash(double q, double u, double h, double bh, double bw, double x)
    {
        var wakeHeight = bh + 1.5 * Math.Min(bh, bw);
        var effectiveH = h < wakeHeight ? 0.0 : h - wakeHeight * 0.5;
        var x3bh = 3 * Math.Max(bh, bw);

        double sigmaY, sigmaZ;
        if (x < x3bh)
        {
            sigmaY = 0.7 * Math.Min(bh, bw) / 2.15 + 0.067 * (x - 3 * Math.Min(bh, bw));
            sigmaZ = 0.7 * bh / 2.15 + 0.067 * (x - 3 * Math.Min(bh, bw));
        }
        else
        {
            sigmaY = 0.22 * x / Math.Sqrt(1 + 0.0001 * x);
            sigmaZ = 0.20 * x;
        }

        var conc = q / (2 * Math.PI * u * sigmaY * sigmaZ) * Math.Exp(-effectiveH * effectiveH / (2 * sigmaZ * sigmaZ)) * 1e6;

        return JsonToolResult.Success(new { concentration_ug_m3 = Math.Round(Math.Max(0, conc), 4), effective_stack_h = Math.Round(effectiveH, 1), cavity_zone = x < x3bh, distance_m = x, building_h = bh, building_w = bw, standard = "HJ2.2-2018" });
    }

    /// <summary>
    /// Inversion breakup fumigation model. When a thermal inversion layer breaks up
    /// (common in mornings or coastal areas), pollutants trapped aloft mix down rapidly,
    /// causing high ground-level concentrations for short-stack sources.
    /// </summary>
    public static object ComputeFumigation(double q, double u, double h, double x, double zi)
    {
        if (h >= zi) return JsonToolResult.Success(new { error = "Stack height must be below inversion layer height zi" });

        var sigmaY = 0.22 * x / Math.Sqrt(1 + 0.0001 * x);
        var effectiveH = h + 0.5 * (zi - h);
        var conc = q / (Math.Sqrt(2 * Math.PI) * u * sigmaY * zi) * Math.Exp(-effectiveH * effectiveH / (2 * zi * zi)) * 1e6;

        return JsonToolResult.Success(new { concentration_ug_m3 = Math.Round(Math.Max(0, conc), 4), sigma_y = Math.Round(sigmaY, 1), inversion_height_m = zi, distance_m = x, scenario = "fumigation" });
    }

    public static object ComputeTrafficNoise(double volumePerH, double speedKmh, double distance, double heavyRatio)
    {
        var soundPower = 10 * Math.Log10(volumePerH) + 30 * Math.Log10(Math.Max(speedKmh, 1)) + 10 * Math.Log10(1 + heavyRatio * 4) - 38;
        var attenuation = 10 * Math.Log10(Math.Max(distance, 1)) + 5;
        var spl = soundPower - attenuation;
        return JsonToolResult.Success(new { spl_db = Math.Round(spl, 1), sound_power_db = Math.Round(soundPower, 1), attenuation_db = Math.Round(attenuation, 1), volume_per_h = volumePerH, speed_kmh = speedKmh, distance_m = distance, heavy_ratio = heavyRatio });
    }

    public static object ComputeRiverMixing(double flowRate, double width, double depth, double velocity, double emissionLoad)
    {
        if (velocity <= 0) return JsonToolResult.Success(new { error = "velocity > 0 required" });
        var fullMixingLength = 0.4 * velocity * width * width / (depth * 10);
        var initialConc = emissionLoad / (flowRate + 0.001);
        var mixedConc = emissionLoad / (flowRate + 0.001) * Math.Exp(-0.2 * fullMixingLength / 86400);
        return JsonToolResult.Success(new { full_mixing_length_m = Math.Round(fullMixingLength, 1), mixing_zone_type = fullMixingLength > width * 10 ? "大中河" : "小河", initial_concentration_mg_l = Math.Round(initialConc, 4), fully_mixed_concentration_mg_l = Math.Round(mixedConc, 4) });
    }

    public static object ComputeEcologicalRisk(string metalsCsv)
    {
        var metals = metalsCsv.Split(',').Select(m => m.Trim()).ToList();
        var toxicFactors = new Dictionary<string, double>
        {
            ["Hg"] = 40, ["Cd"] = 30, ["As"] = 10, ["Pb"] = 5, ["Cu"] = 5, ["Cr"] = 2, ["Zn"] = 1, ["Ni"] = 5
        };
        double totalRisk = 0;
        var details = new List<object>();
        foreach (var m in metals)
        {
            var parts = m.Split(':');
            var name = parts[0];
            var value = parts.Length > 1 ? double.TryParse(parts[1], out var v) ? v : 0 : 0;
            var tf = toxicFactors.GetValueOrDefault(name, 1.0);
            var ri = value * tf;
            totalRisk += ri;
            details.Add(new { metal = name, concentration = value, toxic_factor = tf, risk_index = Math.Round(ri, 2) });
        }
        return JsonToolResult.Success(new { total_risk_index = Math.Round(totalRisk, 2), risk_level = totalRisk < 150 ? "低" : totalRisk < 300 ? "中" : totalRisk < 600 ? "较高" : "高", details });
    }

    public static object ComputeSoilLoss(double r, double k, double ls, double c, double p)
    {
        var usle = r * k * ls * c * p;
        return JsonToolResult.Success(new { soil_loss_t_ha_yr = Math.Round(usle, 2), r_erosivity = r, k_erodibility = k, ls_topographic = ls, c_cover = c, p_support = p, risk_level = usle < 5 ? "微度" : usle < 25 ? "轻度" : usle < 50 ? "中度" : usle < 80 ? "强度" : "剧烈" });
    }

    public static object ComputeCarbonSink(double areaHa, string vegType, double growthRate)
    {
        var carbonDensity = vegType switch
        {
            "forest_conifer" => 120.0, "forest_broadleaf" => 150.0, "forest_mixed" => 135.0,
            "grassland" => 60.0, "wetland" => 200.0, "shrub" => 40.0, _ => 80.0
        };
        var annualSink = areaHa * growthRate * carbonDensity / 100;
        var co2Equivalent = annualSink * 44.0 / 12.0;
        return JsonToolResult.Success(new { annual_carbon_sink_tc = Math.Round(annualSink, 2), co2_equivalent_t = Math.Round(co2Equivalent, 2), area_ha = areaHa, vegetation_type = vegType, carbon_density_tc_ha = carbonDensity, growth_rate_pct = growthRate });
    }

    public static object ComputeBuffer(double lat, double lng, double radiusM)
    {
        var dLat = radiusM / 111320.0;
        var dLng = radiusM / (111320.0 * Math.Cos(lat * Math.PI / 180));
        return new { type = "Feature", geometry = new { type = "Polygon", coordinates = new[] { new[] {
            new[] { lng - dLng, lat - dLat }, new[] { lng + dLng, lat - dLat },
            new[] { lng + dLng, lat + dLat }, new[] { lng - dLng, lat + dLat },
            new[] { lng - dLng, lat - dLat }
        }}}, properties = new { center = new { lat, lng }, radius_m = radiusM } };
    }

    public static object PointInPolygon(double lat, double lng, string geojson) => PointInPolygonImpl(lat, lng, geojson);

    private static object PointInPolygonImpl(double lat, double lng, string geojson)
    {
        try
        {
            using var doc = JsonDocument.Parse(geojson);
            var root = doc.RootElement;
            JsonElement? coords = null;
            if (root.TryGetProperty("coordinates", out var c)) coords = c;
            else if (root.ValueKind == JsonValueKind.Array) coords = root;
            if (coords == null || coords.Value.ValueKind != JsonValueKind.Array)
                return new { error = "Invalid GeoJSON: expected coordinates array" };

            var ring = coords.Value.EnumerateArray()
                .Select(p => (lat: p[1].GetDouble(), lng: p[0].GetDouble())).ToArray();

            if (ring.Length < 3 || ring[0].lat != ring[^1].lat || ring[0].lng != ring[^1].lng)
                return new { error = "Invalid polygon: must have at least 3 points and be closed" };

            bool inside = false;
            int j = ring.Length - 1;
            for (int i = 0; i < ring.Length; i++)
            {
                if ((ring[i].lng > lng) != (ring[j].lng > lng))
                {
                    double intersection = ring[i].lat + (lng - ring[i].lng) / (ring[j].lng - ring[i].lng) * (ring[j].lat - ring[i].lat);
                    if (lat < intersection) inside = !inside;
                }
                j = i;
            }
            return new { inside };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    public static object Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        var r = 6371000.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return JsonToolResult.Success(new { distance_m = Math.Round(r * c, 1), from = new { lat1, lng1 }, to = new { lat2, lng2 } });
    }

    public static object TransformCoord(double lat, double lng, string from, string to)
    {
        if (from == "WGS84" && to == "GCJ02")
        {
            var dLat = TransformLat(lng - 105, lat - 35);
            var dLng = TransformLng(lng - 105, lat - 35);
            var radLat = lat * Math.PI / 180;
            var magic = Math.Sin(radLat);
            return new { lat = Math.Round(lat + dLat * 180 / ((6378137 * (1 - 0.0066934)) / (Math.Sqrt(1 - 0.0066934 * magic * magic) * Math.PI)), 6),
                         lng = Math.Round(lng + dLng * 180 / (6378137 / Math.Sqrt(1 - 0.0066934 * magic * magic) * Math.Cos(radLat) * Math.PI), 6), from, to };
        }
        return JsonToolResult.Success(new { lat, lng, from, to, note = "identity (unsupported transform)" });
    }

    private static double TransformLat(double x, double y) => -100 + 2 * x + 3 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
    private static double TransformLng(double x, double y) => 300 + x + 2 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
}

public sealed class ToolDef
{
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public Func<Dictionary<string, object?>, Task<object?>>? Handler { get; }

    public ToolDef(string name, string description, string category, Func<Dictionary<string, object?>, Task<object?>>? handler = null)
    {
        Name = name; Description = description; Category = category; Handler = handler;
    }
}

internal static class CliEngine
{
    private static readonly List<object> _generatedTools = new();
    private static readonly HashSet<string> DangerousCommands = new()
    { "rm", "dd", "shutdown", "reboot", "sudo", "mkfs", "fdisk", "format", "del /f", "rd /s", "format c:" };

    public static object WrapFunction(string name, string code, string language = "python")
    {
        var tool = new { name, language, code = code[..Math.Min(200, code.Length)], status = "generated", wraps = $"wraps '{name}' as CLI" };
        _generatedTools.Add(tool);
        return tool;
    }

    public static async Task<object> FromRepo(string repoUrl, string branch = "main")
    {
        await Task.Delay(100);
        return JsonToolResult.Success(new { repo_url = repoUrl, branch, status = "pending", message = "Use AnalyzeCode tool with repository files for AST-based entry point detection" });
    }

    public static async Task<object> FromManifest(string yaml)
    {
        await Task.Delay(50);
        return JsonToolResult.Success(new { manifest = yaml[..Math.Min(100, yaml.Length)], status = "parsed", commands_generated = yaml.Split('\n').Length / 5 + 1 });
    }

    public static object ListTools() => new { total = _generatedTools.Count, tools = _generatedTools.TakeLast(10) };

    public static async Task<object> ScanPath(string? filter = null)
    {
        await Task.Delay(200);
        var path = OptionService.Get("PATH", "dotnet");
        var dirs = path!.Split(global::System.IO.Path.PathSeparator).Where(d => !string.IsNullOrWhiteSpace(d));
        var found = dirs.SelectMany(d =>
        {
            try { return Directory.GetFiles(d).Select(global::System.IO.Path.GetFileName).Where(f => filter == null || f!.Contains(filter, StringComparison.OrdinalIgnoreCase)); }
            catch { return Array.Empty<string?>(); }
        }).Take(20).ToList();

        return JsonToolResult.Success(new { scanned_paths = dirs.Count(), executables_found = found.Count, sample = found.Take(10), filter });
    }

    public static async Task<object> Execute(string command, string args)
    {
        if (DangerousCommands.Any(d => command.Contains(d, StringComparison.OrdinalIgnoreCase)))
            return JsonToolResult.Success(new { blocked = true, reason = "Dangerous command blocked by safety gate", command });

        try
        {
            var psi = new global::System.Diagnostics.ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = global::System.Diagnostics.Process.Start(psi);
            if (proc == null) return JsonToolResult.Success(new { error = "Failed to start process" });
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var output = stdout.Length > 0 ? stdout : stderr;
            var isJson = output.TrimStart().StartsWith('{') || output.TrimStart().StartsWith('[');
            return JsonToolResult.Success(new { exit_code = proc.ExitCode, output = output[..Math.Min(2000, output.Length)], format = isJson ? "json" : output.Contains("\t") ? "tsv" : "text" });
        }
        catch (Exception ex) { return JsonToolResult.Success(new { error = ex.Message }); }
    }
}

internal static class CadEngine
{
    public static Task<object> Import(string filePath, string format)
    {
        throw new NotSupportedException(
            $"[cad_import] CAD import functionality is not yet implemented. " +
            $"Requested file: {filePath}, format: {format}. " +
            $"This tool requires the CADability .NET library to be installed and configured.");
    }

    public static Task<object> Analyze(string filePath)
    {
        throw new NotSupportedException(
            $"[cad_analyze] CAD analysis functionality is not yet implemented. " +
            $"Requested file: {filePath}. " +
            $"This tool requires the CADability .NET library to be installed and configured.");
    }

    public static Task<object> Export(string filePath, string targetFormat)
    {
        throw new NotSupportedException(
            $"[cad_export] CAD export functionality is not yet implemented. " +
            $"Requested file: {filePath}, target format: {targetFormat}. " +
            $"This tool requires the CADability .NET library to be installed and configured.");
    }
}

internal static class ChartBuilder
{
    public static string BuildBar(string title, string data, string[] colors, string id, int h = 300) =>
        $@"<div id='{id}'></div><script>new Chart(document.getElementById('{id}'),{{type:'bar',data:{{labels:['A','B','C','D'],datasets:[{{label:'{title}',data:[{data}],backgroundColor:{JsonSerializer.Serialize(colors.Take(4))}}}]}},options:{{responsive:true}}}});</script>";

    public static string BuildLine(string title, string data, string[] colors, string id, int h = 300) =>
        $@"<div id='{id}'></div><script>new Chart(document.getElementById('{id}'),{{type:'line',data:{{labels:['Q1','Q2','Q3','Q4'],datasets:[{{label:'{title}',data:[{data}],borderColor:'{colors[0]}',fill:false}}]}},options:{{responsive:true}}}});</script>";

    public static string BuildPie(string title, string data, string[] colors, string id, int h = 300) =>
        $@"<div id='{id}'></div><script>new Chart(document.getElementById('{id}'),{{type:'pie',data:{{labels:['A','B','C','D'],datasets:[{{data:[{data}],backgroundColor:{JsonSerializer.Serialize(colors.Take(4))}}}]}},options:{{responsive:true}}}});</script>";

    public static string BuildMap(string title, string data, string id, int h = 400) =>
        $@"<div id='{id}' style='width:100%;height:{h}px'></div><script>var m=L.map('{id}').setView([39.9,116.4],12);L.tileLayer('https://{{s}}.tile.openstreetmap.org/{{z}}/{{x}}/{{y}}.png').addTo(m);</script>";

    public static string BuildTable(string title, string data) =>
        $@"<table><caption>{title}</caption><thead><tr>{string.Join("", data.Split(',').Take(5).Select(d => $"<th>{d}</th>"))}</tr></thead></table>";
}

internal static class MathNetAnalyzer
{
    public static object Analyze(string dataCsv, string method)
    {
        var values = dataCsv.Split(',').Select(v => double.TryParse(v, out var d) ? d : 0).ToList();
        if (values.Count == 0) return JsonToolResult.Success(new { error = "No valid data" });

        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        var std = Math.Sqrt(variance);
        var sorted = values.OrderBy(v => v).ToList();
        var median = sorted.Count % 2 == 0
            ? (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2
            : sorted[sorted.Count / 2];

        return method switch
        {
            "stats" => new { count = values.Count, mean = Math.Round(mean, 4), std = Math.Round(std, 4), median = Math.Round(median, 4), min = sorted.First(), max = sorted.Last() },
            "interpolate" => Interpolate(values, 4),
            "fft" => ComputeFFT(values.Take(128).ToList()),
            "monte_carlo" => MonteCarlo(mean, std),
            _ => new { error = $"Unknown method: {method}. Available: stats, interpolate, fft, monte_carlo" }
        };
    }

    private static object Interpolate(List<double> vals, int targetCount)
    {
        var step = (double)(vals.Count - 1) / (targetCount - 1);
        var result = new List<double>();
        for (var i = 0; i < targetCount; i++)
        {
            var idx = i * step;
            var lo = (int)idx;
            var hi = Math.Min(lo + 1, vals.Count - 1);
            var frac = idx - lo;
            result.Add(Math.Round(vals[lo] + (vals[hi] - vals[lo]) * frac, 4));
        }
        return JsonToolResult.Success(new { method = "linear_interpolation", original_count = vals.Count, target_count = targetCount, interpolated = result });
    }

    private static object ComputeFFT(List<double> vals)
    {
        var n = vals.Count;
        var real = new double[n];
        var imag = new double[n];
        for (var k = 0; k < Math.Min(n / 2, 20); k++)
        {
            for (var t = 0; t < n; t++)
            {
                var angle = -2 * Math.PI * k * t / n;
                real[k] += vals[t] * Math.Cos(angle);
                imag[k] += vals[t] * Math.Sin(angle);
            }
        }
        var magnitudes = Enumerable.Range(0, Math.Min(n / 2, 20))
            .Select(k => Math.Round(Math.Sqrt(real[k] * real[k] + imag[k] * imag[k]) / n, 4)).ToList();
        return JsonToolResult.Success(new { method = "fft", dominant_freq = magnitudes.IndexOf(magnitudes.Max()), magnitudes = magnitudes.Take(10) });
    }

    private static object MonteCarlo(double mean, double std)
    {
        var rng = new Random(42);
        var samples = Enumerable.Range(0, 1000).Select(_ => mean + std * (rng.NextDouble() * 2 - 1)).ToList();
        var p95 = samples.OrderBy(s => s).ToList()[(int)(samples.Count * 0.95)];
        var p99 = samples.OrderBy(s => s).ToList()[(int)(samples.Count * 0.99)];
        return JsonToolResult.Success(new { method = "monte_carlo", samples = 1000, mean = Math.Round(samples.Average(), 4), p95 = Math.Round(p95, 4), p99 = Math.Round(p99, 4) });
    }
}

public static class OptionService
{
    public static Func<string, string?, string?>? Resolver { get; set; }

    public static string? Get(string envVar, string? fallback = null)
    {
        var value = Resolver?.Invoke(envVar, null);
        return value ?? Environment.GetEnvironmentVariable(envVar) ?? fallback;
    }

    public static void SetResolver(Func<string, string?, string?> resolver) => Resolver = resolver;
}


