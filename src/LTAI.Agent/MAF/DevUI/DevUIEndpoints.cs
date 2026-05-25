using LTAI.AI.Governors;
using LTAI.Core.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using LTAI.Tools.CodeGraph;

namespace LTAI.Agent;

public static class DevUIEndpoints
{
    private static readonly JsonSerializerOptions _jsonIndentedCamelCase = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions _jsonCamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void MapDevUIEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/devui", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(DevUIHtml.Page).ConfigureAwait(false);
        });

        endpoints.MapGet("/api/devui/state", async (HttpContext context) =>
        {
            var sp = context.RequestServices;
            var system = sp.GetService<LivingTreeSystem>();
            var toolRegistry = sp.GetService<AIToolRegistry>();

            var state = new
            {
                session = new
                {
                    id = Guid.NewGuid().ToString("N")[..8],
                    started_at = DateTime.UtcNow.ToString("o"),
                    mode = system?.Mode.ToString() ?? "uninitialized",
                    dna_enabled = system?.DNAEnabled ?? false,
                    task_pipeline = new
                    {
                        submissions = system?.TaskPipeline.TotalSubmissions ?? 0,
                        completions = system?.TaskPipeline.TotalCompletions ?? 0
                    }
                },
                agents = new object[]
                {
                    new {
                        name = "LivingTreeSystem",
                        role = "5-layer Governor Pipeline",
                        status = system is not null ? "active" : "inactive",
                        dna_phase = system?.DNAStatus?.EvolutionPhase.ToString() ?? "disabled",
                        awareness = system?.DNAStatus?.AwarenessScore ?? 0
                    },
                    new {
                        name = "AgentMesh",
                        role = "Keyword-based Intent Routing",
                        status = "active",
                        routes = new[] { "code", "eia", "reasoning", "chat" }
                    }
                },
                workflows = WorkflowRegistry.GetAll(),
                governance = Governance.ActionGovernor.Instance.GetStats(),
                storage = Hosting.ChatHistoryManager.Instance.DescribeBackends(),
                tools = new
                {
                    total = toolRegistry?.ListTools().Count() ?? 0,
                    sample = toolRegistry?.ListTools().Take(8).ToArray() ?? Array.Empty<string>()
                },
                graph = new
                {
                    nodes = new[]
                    {
                        new { id = "input", label = "User Input", group = "io" },
                        new { id = "governor", label = "Governor (AGT)", group = "pipeline" },
                        new { id = "livingtree", label = "LivingTree Pipeline", group = "pipeline" },
                        new { id = "tools", label = $"Tools ({toolRegistry?.ListTools().Count() ?? 0})", group = "tool" },
                        new { id = "mesh", label = "Agent Mesh", group = "orchestra" },
                        new { id = "agui", label = "AG-UI Stream", group = "io" },
                        new { id = "output", label = "Response", group = "io" }
                    },
                    edges = new[]
                    {
                        new { from = "input", to = "governor" },
                        new { from = "governor", to = "livingtree" },
                        new { from = "livingtree", to = "tools" },
                        new { from = "livingtree", to = "mesh" },
                        new { from = "tools", to = "mesh" },
                        new { from = "mesh", to = "agui" },
                        new { from = "agui", to = "output" }
                    }
                }
            };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(state, _jsonIndentedCamelCase)).ConfigureAwait(false);
        });

        endpoints.MapGet("/api/devui/graph", async (HttpContext context) =>
        {
            var graphService = context.RequestServices.GetService<LTAI.Knowledge.Core.KnowledgeGraph>();
            var projectRoot = AppContext.BaseDirectory;
            var nodes = new List<object>();
            var edges = new List<object>();
            var seenIds = new HashSet<string>();

            foreach (var file in Directory.GetFiles(projectRoot, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                if (relativePath.StartsWith(".git/") || relativePath.StartsWith("bin/") || 
                    relativePath.StartsWith("obj/") || relativePath.StartsWith(".livingtree/")) continue;

                var ext = Path.GetExtension(file);
                var group = ext switch
                {
                    ".cs" => "code", ".java" => "code", ".go" => "code", ".rs" => "code",
                    ".cpp" => "code", ".c" => "code", ".h" => "code", ".hpp" => "code",
                    ".swift" => "code", ".kt" => "code", ".scala" => "code", ".rb" => "code",
                    ".csproj" => "config", ".json" => "config", ".xml" => "config",
                    ".yaml" => "config", ".yml" => "config", ".toml" => "config",
                    ".md" => "doc", ".rst" => "doc", ".txt" => "doc",
                    ".sln" => "config", ".py" => "script", ".js" => "code", ".ts" => "code",
                    ".jsx" => "code", ".tsx" => "code", ".vue" => "code", ".svelte" => "code",
                    ".html" => "ui", ".css" => "ui", ".scss" => "ui", ".less" => "ui",
                    ".ps1" => "script", ".sh" => "script", ".bat" => "script",
                    _ => "other"
                };

                if (seenIds.Add(relativePath))
                {
                    nodes.Add(new { id = relativePath, label = Path.GetFileName(file), group, path = relativePath, ext });

                    var parent = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(parent) && parent != ".")
                    {
                        edges.Add(new { from = parent, to = relativePath });
                        if (!seenIds.Contains(parent))
                        {
                            seenIds.Add(parent);
                            nodes.Add(new { id = parent, label = Path.GetFileName(parent) + "/", group = "config", path = parent, ext = "" });
                        }
                    }
                }
            }

            // Merge KnowledgeGraph semantic triplets
            if (graphService != null)
            {
                var triplets = graphService.GetTriplets();
                foreach (var triplet in triplets)
                {
                    var subjId = $"kg:{triplet.Subject}";
                    var objId = $"kg:{triplet.Object}";
                    if (seenIds.Add(subjId))
                        nodes.Add(new { id = subjId, label = triplet.Subject, group = "concept", path = "", ext = "" });
                    if (seenIds.Add(objId))
                        nodes.Add(new { id = objId, label = triplet.Object, group = "concept", path = "", ext = "" });
                    edges.Add(new { from = subjId, to = objId, label = triplet.Predicate });
                }
            }

            // Merge CodeGraph semantic code entities
            var codeGraph = context.RequestServices.GetService<CodeGraphEnhanced>();
            if (codeGraph != null)
            {
                try
                {
                    var cgNodes = codeGraph.GetAllNodes();
                    var cgEdges = codeGraph.GetAllEdges();
                    foreach (var node in cgNodes)
                    {
                        var cgId = $"cg:{node.Id}";
                        if (seenIds.Add(cgId))
                            nodes.Add(new { id = cgId, label = node.Name, group = node.Kind switch { "function" => "code", "method" => "code", "class" => "code", _ => "other" }, path = node.File, ext = "", line = node.Line });
                    }
                    foreach (var edge in cgEdges)
                    {
                        edges.Add(new { from = $"cg:{edge.SourceId}", to = $"cg:{edge.TargetId}", label = edge.Relation });
                    }
                }
                catch { }
            }

            var result = new { nodes, edges, project = Path.GetFileName(projectRoot.TrimEnd('/', '\\')), total_files = nodes.Count(n => !((dynamic)n).id.EndsWith("/")) };

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(result, _jsonCamelCase)).ConfigureAwait(false);
        });

        endpoints.MapGet("/api/devui/impact", async (HttpContext context) =>
        {
            var codeGraph = context.RequestServices.GetService<CodeGraphEnhanced>();
            var projectRoot = AppContext.BaseDirectory;
            var changedFiles = new List<string>();
            ImpactResult? impact = null;
            double score = 0;
            int affectedNodes = 0;

            if (codeGraph != null)
            {
                try
                {
                    var status = codeGraph.GetStatus();
                    affectedNodes = (int)(status.GetValueOrDefault("total_nodes", 0) as int? ?? 0);

                    var nodes = codeGraph.GetAllNodes();
                    if (nodes.Count > 0)
                    {
                        var topNode = nodes.OrderByDescending(n => n.CalleeCount).FirstOrDefault();
                        if (topNode != null)
                            impact = codeGraph.GetImpactRadius(topNode.Id, maxDepth: 2);
                    }
                }
                catch { }
            }

            if (Directory.Exists(Path.Combine(projectRoot, ".git")))
            {
                try
                {
                    using var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "git", Arguments = "diff --name-only HEAD~1", WorkingDirectory = projectRoot,
                            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                        }
                    };
                    process.Start();
                    var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    changedFiles = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToList();
                }
                catch { }
            }

            var affectedDirs = new HashSet<string>();
            foreach (var f in changedFiles)
                { var dir = Path.GetDirectoryName(f)?.Replace('\\', '/'); if (dir != null) affectedDirs.Add(dir); }

            score = Math.Min(1.0, (changedFiles.Count * 0.05) + (affectedDirs.Count * 0.03) +
                (impact != null ? impact.TransitiveCallers * 0.02 : 0));

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                score, changed_files = changedFiles.Count,
                blast_radius = impact != null ? new { radius = impact.Radius, direct_callers = impact.DirectCallers, transitive = impact.TransitiveCallers, affected_files = impact.AffectedFiles, affected_tests = impact.AffectedTests } : null,
                affected_nodes = affectedNodes,
                files = changedFiles.Take(20)
            }, _jsonCamelCase)).ConfigureAwait(false);
        });

        endpoints.MapGet("/api/devui/tour", async (HttpContext context) =>
        {
            var projectRoot = AppContext.BaseDirectory;
            var steps = new List<object>();
            var order = 0;

            string[] priorityExts = { ".sln", ".csproj", ".json", ".cs", ".java", ".go", ".rs", ".cpp", ".py", ".js", ".ts", ".jsx", ".tsx", ".md", ".html", ".css", ".xml", ".yaml", ".yml" };
            var sorted = Directory.GetFiles(projectRoot, "*.*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(projectRoot, f).Replace('\\', '/').StartsWith(".git/") &&
                            !Path.GetRelativePath(projectRoot, f).Replace('\\', '/').StartsWith("bin/") &&
                            !Path.GetRelativePath(projectRoot, f).Replace('\\', '/').StartsWith("obj/"))
                .OrderBy(f => Array.IndexOf(priorityExts, Path.GetExtension(f)) is int i && i >= 0 ? i : 99)
                .ThenBy(f => Path.GetRelativePath(projectRoot, f).Count(c => c == '/'))
                .Take(50);

            foreach (var file in sorted)
            {
                var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                var ext = Path.GetExtension(file);
                var description = ext switch
                {
                    ".sln" => "Solution file defining project structure",
                    ".csproj" => "Project configuration: framework, packages, references",
                    ".json" => "JSON data or configuration",
                    ".xml" => "XML configuration or data",
                    ".yaml" or ".yml" => "YAML configuration",
                    ".cs" => "C# source code",
                    ".java" => "Java source code",
                    ".go" => "Go source code",
                    ".rs" => "Rust source code",
                    ".cpp" or ".c" or ".h" => "C/C++ source code",
                    ".py" => "Python script",
                    ".js" or ".jsx" => "JavaScript module",
                    ".ts" or ".tsx" => "TypeScript module",
                    ".md" => "Documentation",
                    ".html" => "Web template",
                    ".css" or ".scss" or ".less" => "Stylesheet",
                    _ => "Project file"
                };
                steps.Add(new { order = ++order, file = rel, description, ext });
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { steps, total = steps.Count }, _jsonCamelCase)).ConfigureAwait(false);
        });

        endpoints.MapGet("/api/devui/agui-stream", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var hub = AGUI.AgUiStreamHub.Instance;
            var tcs = new TaskCompletionSource<bool>();
            context.RequestAborted.Register(() => tcs.TrySetResult(true));

            hub.Subscribe(async evt =>
            {
                try
                {
                    var sse = hub.RenderSseEvent(evt);
                    await context.Response.WriteAsync(sse, context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
                catch { }
            });

            await tcs.Task.ConfigureAwait(false);
        });
    }
}

public static class WorkflowRegistry
{
    private static readonly List<object> _workflows = new();
    private static readonly object _lock = new();

    public static void Record(string id, string type, int steps, string status, long latencyMs)
    {
        lock (_lock)
        {
            _workflows.Add(new { id = id, type = type, steps = steps, status = status, latencyMs = latencyMs, ts = DateTime.UtcNow.ToString("HH:mm:ss") });
            if (_workflows.Count > 50) _workflows.RemoveAt(0);
        }
    }

    public static List<object> GetAll()
    {
        lock (_lock) { return _workflows.ToList(); }
    }
}
