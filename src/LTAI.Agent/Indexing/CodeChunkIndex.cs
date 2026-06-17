using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Text;
using LTAI.Agent.Context;
using LTAI.Agent.Tools;
using LTAI.Agent.CodeAnalysis;
using LTAI.Agent.Utils;
using LTAI.Agent.Vector;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using LTAI.AI;

namespace LTAI.Agent.Indexing;

public sealed class CodeChunkIndex : AIContextProvider
{
    private readonly KgStore _store;
    private readonly TreeSitterParser? _parser;
    private readonly EmbeddingClient? _embedder;
    private readonly ILogger<CodeChunkIndex>? _logger;
    private readonly string _ws;
    private readonly ConcurrentDictionary<string, DateTime> _indexedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _built;
    private const int MaxChunkLines = 200;

    private static readonly HashSet<string> SourceExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".go", ".rs", ".java",
        ".sh", ".bash", ".json", ".html", ".css",
        ".mbt", ".mojo", "🔥", ".cj",
    };

    private static readonly Dictionary<string, HashSet<string>> ChunkDeclTypes = new()
    {
        ["c_sharp"] = new() { "class_declaration", "struct_declaration", "interface_declaration",
            "record_declaration", "method_declaration", "constructor_declaration",
            "enum_declaration", "property_declaration" },
        ["python"] = new() { "class_definition", "function_definition", "async_function_definition" },
        ["javascript"] = new() { "class_declaration", "function_declaration", "method_definition",
            "arrow_function", "generator_function_declaration" },
        ["typescript"] = new() { "class_declaration", "function_declaration", "method_definition",
            "arrow_function", "interface_declaration", "type_alias_declaration", "enum_declaration" },
        ["go"] = new() { "function_declaration", "method_declaration", "type_declaration",
            "struct_type", "interface_type" },
        ["rust"] = new() { "function_item", "struct_item", "impl_item", "trait_item",
            "enum_item", "type_item" },
        ["java"] = new() { "class_declaration", "interface_declaration", "method_declaration",
            "constructor_declaration", "enum_declaration", "record_declaration" },
        ["bash"] = new() { "function_definition" },
    };

    private static readonly Dictionary<string, string> ExtToLang = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "c_sharp", [".py"] = "python",
        [".js"] = "javascript", [".jsx"] = "javascript",
        [".ts"] = "typescript", [".tsx"] = "tsx",
        [".go"] = "go", [".rs"] = "rust", [".java"] = "java",
        [".sh"] = "bash", [".bash"] = "bash",
        [".json"] = "json", [".html"] = "html", [".css"] = "css",
        [".mbt"] = "moonbit", [".mojo"] = "mojo", ["🔥"] = "mojo",
        [".cj"] = "cangjie",
    };

    public CodeChunkIndex(KgStore store, TreeSitterParser? parser = null,
        EmbeddingClient? embedder = null,
        ILogger<CodeChunkIndex>? logger = null, string? ws = null)
        : base(null, null, null)
    {
        _store = store;
        _parser = parser;
        _embedder = embedder;
        _logger = logger;
        _ws = ws ?? Directory.GetCurrentDirectory();
    }

    public bool IsBuilt => _built;

    public async Task<string> BuildAsync(string? directory = null)
    {
        var dir = directory ?? _ws;
        if (!Directory.Exists(dir)) return "Directory not found";

        var files = DirectoryWalker.WalkToArray(dir,
            allowedExtensions: SourceExts,
            skipDirNames: new(StringComparer.OrdinalIgnoreCase)
                { "obj", "bin", "dist", "node_modules", ".git", "packages" });

        int chunkCount = 0, skippedCount = 0;
        var maxDop = Math.Max(1, Environment.ProcessorCount / 2);
        var sem = new SemaphoreSlim(1, 1);

        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = maxDop },
            async (file, ct) =>
        {
            var lw = File.GetLastWriteTimeUtc(file);
            if (_indexedFiles.TryGetValue(file, out var pw) && pw >= lw)
            {
                Interlocked.Increment(ref skippedCount);
                return;
            }

            var rel = Path.GetRelativePath(_ws, file).Replace('\\', '/');
            var ext = Path.GetExtension(file);

            try
            {
                var code = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(code) || code.Length < 10) return;

                var chunks = ExtractChunks(code, ext, rel);
                if (chunks.Count == 0) return;

                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _store.DeleteNodesByKindAndSource("chunk", rel).ConfigureAwait(false);

                    foreach (var chunk in chunks)
                    {
                        var nid = await _store.UpsertNode(
                            extId: chunk.ExtId,
                            kind: "chunk",
                            name: chunk.Name,
                            ns: rel,
                            signature: chunk.Signature,
                            source: rel,
                            props: new()
                            {
                                ["lang"] = chunk.Language,
                                ["kind"] = chunk.DeclKind,
                                ["start_line"] = chunk.StartLine,
                                ["end_line"] = chunk.EndLine
                            }).ConfigureAwait(false);

                        await _store.AddDoc(nid, chunk.Text, "code",
                            $"{rel}:L{chunk.StartLine}").ConfigureAwait(false);

                        var vec = _embedder != null
                            ? await _embedder.GenerateAsync(chunk.Text, ct).ConfigureAwait(false)
                            : EmbeddingClient.FastEmb(chunk.Text);
                        await _store.InsertVectorAsync(nid, vec).ConfigureAwait(false);
                    }
                }
                finally { sem.Release(); }

                _indexedFiles[file] = lw;
                Interlocked.Add(ref chunkCount, chunks.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "CodeChunkIndex: failed to index {Rel}", rel);
            }
        }).ConfigureAwait(false);

        await _store.RebuildCentroidsAsync().ConfigureAwait(false);
        _built = true;
        return $"Indexed {chunkCount} chunks across {files.Length} files ({skippedCount} skipped).";
    }

    [Description("语义搜索代码库。输入自然语言描述，返回最匹配的代码片段。优先于 ReadFileContent：先试用此工具理解代码逻辑，需要完整文件时才用 ReadFileContent。")]
    public async Task<string> SemanticCodeSearch(
        [Description("自然语言查询，描述你想寻找的代码功能，如 'how are sessions created'")] string query,
        [Description("结果数量限制")] int limit = 5)
    {
        if (!_built)
        {
            var buildResult = await BuildAsync().ConfigureAwait(false);
            _logger?.LogInformation("CodeChunkIndex: lazy build: {BuildResult}", buildResult);
        }

        try
        {
            var qvec = _embedder != null
                ? await _embedder.GenerateAsync(query).ConfigureAwait(false)
                : EmbeddingClient.FastEmb(query);

            var hits = await _store.SearchVector(qvec, Math.Clamp(limit, 1, 20),
                kindFilter: "chunk").ConfigureAwait(false);

            if (hits.Count == 0) return "No matching code chunks found.";

            var sb = new StringBuilder();
            foreach (var (nodeId, distance) in hits)
            {
                var node = await _store.GetNode(nodeId).ConfigureAwait(false);
                if (node == null) continue;

                var docs = await _store.GetDocs(nodeId).ConfigureAwait(false);
                var text = docs.FirstOrDefault()?.Text ?? "";
                var score = 1.0f - Math.Clamp(distance, 0, 1);
                var lines = node.Signature ?? "?";
                var props = node.GetProps();
                var lang = props?.GetValueOrDefault("lang") as string ?? "";
                var kind = props?.GetValueOrDefault("kind") as string ?? "";

                sb.AppendLine($"📄 {node.Source}:{lines}  score={score:F2}");
                sb.Append($"▔▔▔▔ {kind} {node.Name}");
                if (!string.IsNullOrEmpty(lang))
                {
                    sb.AppendLine($"  [{lang}]");
                    sb.AppendLine($"```{lang}");
                    sb.AppendLine(text.Length > 800 ? text[..800] + "..." : text);
                    sb.AppendLine("```");
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine(text.Length > 800 ? text[..800] + "..." : text);
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"Search failed: {ex.Message}";
        }
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        if (context.AIContext.IsProviderSkipped("CodeChunkIndex"))
            return context.AIContext!;

        var msgs = context.AIContext?.Messages;
        if (msgs == null || !msgs.Any()) return context.AIContext!;

        var userMsg = msgs.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text == null || userMsg.Text.Length < 10) return context.AIContext!;

        if (!IsCodeQuery(userMsg.Text)) return context.AIContext!;

        // Skip code chunk search when ExpertRouterAgent already injected aggregated context
        foreach (var m in msgs.Reverse())
        {
            if (m.Role == ChatRole.System && m.Text?.StartsWith("## Expert Context") == true)
                return context.AIContext!;
        }

        var result = await SemanticCodeSearch(userMsg.Text, limit: 3).ConfigureAwait(false);
        if (result.StartsWith("No matching") || result.StartsWith("Search failed"))
            return context.AIContext!;

        return new AIContext
        {
            Instructions = context.AIContext!.Instructions != null
                ? context.AIContext.Instructions + "\n\n## 相关代码片段\n" + result
                : "## 相关代码片段\n" + result,
            Messages = context.AIContext.Messages,
        };
    }

    private sealed record Chunk(string ExtId, string Name, string DeclKind, string Language,
        string Signature, int StartLine, int EndLine, string Text);

    private List<Chunk> ExtractChunks(string code, string ext, string rel)
    {
        var langId = ExtToLang.GetValueOrDefault(ext);
        var chunks = new List<Chunk>();

        if (langId != null && _parser != null && ChunkDeclTypes.ContainsKey(langId))
        {
            var declTypes = ChunkDeclTypes[langId];
            var lines = code.Split('\n');
            var tree = _parser.Parse(code, ext);
            if (tree != null)
            {
                ExtractDeclarations(tree.RootNode, declTypes, lines, rel, langId, chunks);
            }
        }

        if (chunks.Count == 0)
        {
            var lines = code.Split('\n');
            var lineCount = lines.Length;
            const int chunkSize = 30;
            for (int i = 0; i < lineCount; i += chunkSize)
            {
                var end = Math.Min(i + chunkSize, lineCount);
                var text = string.Join('\n', lines, i, end - i);
                if (text.Length < 10) continue;

                chunks.Add(new Chunk(
                    $"chunk:{rel}:L{i + 1}",
                    Path.GetFileName(rel),
                    "block",
                    langId ?? "text",
                    $"L{i + 1}-L{end}",
                    i + 1, end, text));
            }
        }

        return chunks;
    }

    private void ExtractDeclarations(TreeSitter.Node node, HashSet<string> declTypes,
        string[] sourceLines, string rel, string langId, List<Chunk> chunks)
    {
        if (declTypes.Contains(node.Type))
        {
            var startLine = node.StartPosition.Row + 1;
            var endLine = Math.Min(node.EndPosition.Row + 1, sourceLines.Length);
            if (endLine - startLine > MaxChunkLines) return;

            var lineCount = endLine - startLine + 1;
            var text = string.Join('\n', sourceLines, startLine - 1, lineCount);
            if (text.Length < 10) return;

            var name = ExtractName(node) ?? "?";
            var kind = MapDeclKind(node.Type);

            chunks.Add(new Chunk(
                $"chunk:{rel}:L{startLine}",
                name, kind, langId,
                $"L{startLine}-L{endLine}",
                startLine, endLine, text));

            return;
        }

        foreach (var child in node.Children)
            ExtractDeclarations(child, declTypes, sourceLines, rel, langId, chunks);
    }

    private static string? ExtractName(TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.IsNamed && child.Type is "identifier" or "name")
                return child.Text;
        }
        return null;
    }

    private static string MapDeclKind(string tsType) => tsType switch
    {
        "class_declaration" or "class_definition" => "class",
        "method_declaration" or "method_definition" => "method",
        "function_declaration" or "function_definition" or "function_item" => "function",
        "interface_declaration" or "interface_type" => "interface",
        "struct_declaration" or "struct_item" or "struct_type" => "struct",
        "enum_declaration" or "enum_item" => "enum",
        "constructor_declaration" => "constructor",
        "property_declaration" => "property",
        "record_declaration" => "record",
        "type_alias_declaration" or "type_item" or "type_declaration" => "type",
        "trait_item" => "trait",
        "impl_item" => "impl",
        "arrow_function" => "lambda",
        _ => "block",
    };

    private static bool IsCodeQuery(string text)
    {
        if (text.Length < 10) return false;
        var lower = text.ToLowerInvariant();
        var codeKeywords = new[]
        {
            "code", "function", "method", "class", "implement",
            "find", "search", "where", "how", "what", "show",
            "代码", "函数", "方法", "类", "实现", "查找", "搜索",
            "call", "invoke", "use", "define", "create",
            "file", "directory", "folder", "project",
            "bug", "fix", "error",
        };
        return codeKeywords.Any(k => lower.Contains(k));
    }
}
