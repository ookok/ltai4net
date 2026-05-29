using LiteDB;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LTAI.Agent.Vector;

/// <summary>
/// Multi-language Code Graph using TreeSitter AST parsing.
/// Embeddings via LLM + fast n-gram fallback. No local model required.
/// </summary>
public sealed class CodeGraph : AIContextProvider
{
    private readonly GraphStore _store;
    private readonly EmbeddingClient _embedder;
    private readonly ILogger<CodeGraph> _logger;
    private readonly string _ws;
    private bool _built;
    private readonly Dictionary<string, DateTime> _indexedFiles = new(StringComparer.OrdinalIgnoreCase);
    private Tools.TreeSitterParser? _parser;

    private static readonly HashSet<string> SourceExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".go", ".rs", ".java",
        ".sh", ".bash", ".json", ".html", ".css",
    };

    public CodeGraph(GraphStore store, EmbeddingClient embedder, ILogger<CodeGraph> logger, string ws)
        : base(null, null, null)
    {
        _store = store;
        _embedder = embedder;
        _logger = logger;
        _ws = ws;
    }

    public async Task<string> BuildAsync(string? directory = null)
    {
        var dir = directory ?? _ws;
        if (!Directory.Exists(dir)) return "Directory not found";

        _parser ??= new Tools.TreeSitterParser();

        var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => SourceExts.Contains(Path.GetExtension(f)))
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\") && !f.Contains("\\dist\\")
                     && !f.Contains("\\node_modules\\") && !f.Contains("\\.git\\"))
            .ToList();

        int sc = 0, na = 0;
        foreach (var file in files)
        {
            var lw = File.GetLastWriteTimeUtc(file);
            if (_indexedFiles.TryGetValue(file, out var pw) && pw >= lw) continue;

            var rel = Path.GetRelativePath(_ws, file).Replace('\\', '/');
            _store.DeleteSource(rel);

            try
            {
                var code = await File.ReadAllTextAsync(file);
                var ext = Path.GetExtension(file);
                var fileName = new FileInfo(file).Name;

                var fid = $"file:{rel}";
                _store.UpsertNode(fid, "file", fileName, FastEmb(code),
                    new() { ["path"] = rel, ["ext"] = ext, ["lines"] = code.Split('\n').Length }, rel);
                na++;

                var symbols = _parser.ExtractSymbols(code, ext);
                foreach (var (kind, name, line, _) in symbols)
                {
                    var id = $"{kind}:{Path.GetFileNameWithoutExtension(file)}:{name}";
                    if (_store.NodeExists(id)) continue;

                    var ctx = GetContext(code, line);
                    var emb = await EmbAsync(ctx);
                    _store.UpsertNode(id, kind, name, emb,
                        new() { ["file"] = rel, ["line"] = line }, rel);
                    _store.AddEdge(fid, id, "defines");
                    na++;
                }

                _indexedFiles[file] = lw;
                sc++;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed {File}", file); }
        }

        _built = true;
        var stale = _store.PruneStaleSources(_ws);
        if (stale > 0 || _indexedFiles.Count % 10 == 0)
        {
            var (p, b, a) = _store.RunMaintenance(_ws);
            _logger.LogInformation("GC: {P} stale, {B}B->{A}B", p, b, a);
        }
        return $"Built: {sc} files, {na} nodes\n{_store.Stats()}";
    }

    public async Task<string> QueryAsync(string query, int topK = 5)
    {
        await EnsureBuiltAsync();
        var results = _store.SearchNodes(await EmbAsync(query), topK);
        if (results.Count == 0) return "No relevant code found.";
        var lines = new List<string> { "## Relevant Code:\n" };
        foreach (var r in results)
        {
            var t = r["type"].AsString;
            var n = r["name"].AsString;
            var f = r.ContainsKey("file") ? r["file"].AsString : "";
            var ln = r.ContainsKey("line") ? "L" + r["line"].AsString : "";
            lines.Add($"- [{t}] {n} — {f}:{ln}");
            foreach (var nid in _store.TraverseBfs(r["_id"].AsString, maxDepth: 1, maxNodes: 5).Skip(1).Take(3))
            { var nd = _store.GetNode(nid); if (nd != null) lines.Add($"  L {nd["type"]}: {nd["name"]}"); }
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext ctx, CancellationToken ct = default)
    {
        var msgs = ctx.AIContext?.Messages;
        if (msgs == null) return ctx.AIContext!;
        var u = msgs.LastOrDefault(m => m.Role == ChatRole.User);
        if (u?.Text == null || u.Text.Length < 5) return ctx.AIContext!;
        try
        {
            if (!_built) await BuildAsync();
            var r = await QueryAsync(u.Text, 3);
            if (string.IsNullOrEmpty(r) || r.StartsWith("No relevant")) return ctx.AIContext!;
            _logger.LogInformation("CodeGraph injected");
            return new AIContext
            {
                Instructions = ctx.AIContext?.Instructions != null
                    ? ctx.AIContext.Instructions + "\n\n" + r : r,
                Messages = ctx.AIContext?.Messages,
                Tools = ctx.AIContext?.Tools,
            };
        }
        catch { return ctx.AIContext!; }
    }

    private async Task EnsureBuiltAsync() { if (!_built) await BuildAsync(); }

    private async Task<float[]> EmbAsync(string t) => await _embedder.GenerateAsync(t);
    private static float[] FastEmb(string t) => EmbeddingClient.FastEmb(t);

    private static string GetContext(string code, int lineNum)
    {
        var lines = code.Split('\n');
        var start = Math.Max(0, lineNum - 3);
        var end = Math.Min(lines.Length, lineNum + 2);
        return string.Join("\n", lines[start..end]);
    }

    public void Dispose() { _parser?.Dispose(); _store.Dispose(); }
}
