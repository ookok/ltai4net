// Copyright (c) LTAI. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using LTAI.Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LTAI.Agent.Vector;

/// <summary>
/// Knowledge Base Graph (SQLite + FTS5).
/// Pipeline: LLM query rewrite → BM25 recall → CTE BFS expansion → context injection.
/// </summary>
public sealed class KbGraph : AIContextProvider
{
    private readonly KgStore _store;
    private readonly IChatClient? _rewriter;
    private readonly Reranker? _reranker;
    private readonly ILogger<KbGraph> _logger;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="store">SQLite KgStore.</param>
    /// <param name="rewriter">Optional LLM for query→keyword rewriting. If null, raw query is used as-is.</param>
    /// <param name="reranker">Optional two-stage reranker (embeddings + LLM rescore).</param>
    /// <param name="logger">Logger.</param>
    public KbGraph(KgStore store, IChatClient? rewriter = null,
        Reranker? reranker = null, ILogger<KbGraph>? logger = null)
        : base(null, null, null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _rewriter = rewriter;
        _reranker = reranker;
        _logger = logger ?? NullLogger<KbGraph>.Instance;
    }

    // ═══════════════════════════════════════════
    //  Public query
    // ═══════════════════════════════════════════

    public async Task<List<string>> QueryAsync(string query, int topK = 10,
        bool expandGraph = true, CancellationToken ct = default)
    {
        // Stage 1: Query expansion — skip LLM rewriter for simple queries and dev mode
        // (FastEmb intent classification already filtered casual chat earlier)
        // Skip LLM-based query expansion for very simple queries
        string expanded;
        if (query.Length <= 8 || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2)
        {
            // Use original query directly for simple queries
            expanded = query;
        }
        else
        {
            expanded = await ExpandQueryAsync(query, ct).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(expanded)) expanded = query;

        if (!string.Equals(query, expanded, StringComparison.Ordinal))
            _logger.LogInformation("KbGraph: \"{Q}\" → expanded: \"{E}\"", query, expanded);

        // Stage 2: FTS5 BM25 recall (weighted by node kind)
        var ftsHits = await _store.SearchFts(expanded, topN: topK * 3).ConfigureAwait(false);

        // Stage 2b: Optional hybrid search (FTS5 + sqlite-vec RRF)
        // Uses LocalEmbedder (BGE ONNX) for vector embeddings, no API key required.
        if (_reranker != null && ftsHits.Count > 0)
        {
            try
            {
                var localEmb = GetSharedEmbedder();
                if (localEmb.Available)
                {
                    var queryEmb = localEmb.Generate(query);
                    var vecHits = await _store.SearchVector(queryEmb, topN: topK * 3).ConfigureAwait(false);

                    // RRF fusion: combine FTS5 BM25 + vector cosine distance ranks
                    var rrf = new Dictionary<long, double>();
                    int k = 60;
                    int rank = 0;
                    foreach (var h in ftsHits)
                        rrf[h.nodeId] = 1.0 / (k + rank++);
                    rank = 0;
                    foreach (var (nid, _) in vecHits)
                        rrf[nid] = rrf.GetValueOrDefault(nid) + 1.0 / (k + rank++);

                    var fusedIds = rrf.OrderByDescending(x => x.Value)
                                      .Take(topK * 2)
                                      .Select(x => x.Key)
                                      .ToList();
                    // 重建 ftsHits 为 fusedIds 的并集：
                    // - BM25+vector 都命中的 → 保留 BM25 元数据
                    // - 仅 vector 命中的 → 创建占位条目（后续走 node lookup）
                    var ftsMap = ftsHits.ToDictionary(h => h.nodeId);
                    ftsHits = fusedIds
                        .Select(id => ftsMap.TryGetValue(id, out var hit) ? hit : (id, "", 0.0, ""))
                        .ToList();
                    _logger.LogInformation("KbGraph: FTS5+Vector RRF fusion, {N} results", ftsHits.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KbGraph: hybrid search failed, using FTS5 only");
            }
        }

        // Stage 3: CTE BFS expansion
        HashSet<long> resultIds;
        if (expandGraph && ftsHits.Count > 0)
        {
            var startIds = ftsHits.Take(3).Select(h => h.nodeId).ToList();
            var bfsNodes = await _store.TraverseBfs(startIds, maxDepth: 2, maxNodes: 10).ConfigureAwait(false);
            resultIds = new HashSet<long>(bfsNodes.Select(n => n.Id));
            foreach (var h in ftsHits) resultIds.Add(h.nodeId);
        }
        else
        {
            resultIds = new HashSet<long>(ftsHits.Select(h => h.nodeId));
        }

        // Stage 4: Format output
        var seen = new HashSet<long>();
        var output = new List<string>();
        foreach (var nodeId in resultIds.Take(topK))
        {
            if (!seen.Add(nodeId)) continue;
            var node = await _store.GetNode(nodeId).ConfigureAwait(false);
            if (node == null) continue;

            output.Add(FormatNode(node));

            // Show related docs
            foreach (var doc in (await _store.GetDocs(nodeId).ConfigureAwait(false)).Take(2))
            {
                var snippet = doc.Text.Length > 200 ? doc.Text[..200] + "…" : doc.Text;
                output.Add($"  └─ {snippet}");
            }

            // Show neighbor edges
            foreach (var edge in (await _store.GetEdges(nodeId).ConfigureAwait(false)).Take(3))
            {
                var neighborId = edge.Src == nodeId ? edge.Dst : edge.Src;
                var neighbor = await _store.GetNode(neighborId).ConfigureAwait(false);
                if (neighbor != null)
                    output.Add($"  ══ {edge.Relation} ══ [{neighbor.Kind}] {neighbor.Name}");
            }
        }
        return output;
    }

    // ═══════════════════════════════════════════
    //  AIContextProvider
    // ═══════════════════════════════════════════

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var msgs = context.AIContext?.Messages;
        if (msgs == null) return context.AIContext!;

        var userMsg = msgs.LastOrDefault(m => m.Role == ChatRole.User);
        if (userMsg?.Text == null || userMsg.Text.Length < 5)
            return context.AIContext!;

        // Skip KG query for casual chat — embedding-based intent classification
        if (!IsKnowledgeQuery(userMsg.Text))
        {
            _logger.LogDebug("KbGraph: skipped casual query \"{Q}\"", userMsg.Text);
            return context.AIContext!;
        }

        try
        {
            var results = await QueryAsync(userMsg.Text, topK: 5, ct: ct).ConfigureAwait(false);
            if (results.Count == 0) return context.AIContext!;

            var block = "## Relevant Knowledge:\n" + string.Join("\n", results.Select(r => "- " + r));
            _logger.LogInformation("KbGraph: injected {N} items", results.Count);

            return new AIContext
            {
                Instructions = context.AIContext?.Instructions != null
                    ? context.AIContext.Instructions + "\n\n" + block
                    : block,
                Messages = context.AIContext?.Messages,
                Tools = context.AIContext?.Tools,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KbGraph query failed");
            return context.AIContext!;
        }
    }

    // ═══════════════════════════════════════════
    //  Ingestion
    // ═══════════════════════════════════════════

    public async Task<string> IngestDocument(string id, string title, string content,
        string source = "", string lang = "zh")
    {
        var nodeId = await _store.UpsertNode(
            extId: $"doc:{id}",
            kind: "document",
            name: title,
            ns: source,
            signature: $"len:{content.Length}",
            source: source).ConfigureAwait(false);

        await _store.AddDoc(nodeId, content, lang, source).ConfigureAwait(false);

        var concepts = ExtractConcepts(title, content);
        foreach (var concept in concepts.Take(15))
        {
            var cid = await _store.UpsertNode(
                extId: $"concept:{concept.ToLowerInvariant().Replace(" ", "_")}",
                kind: "concept",
                name: concept).ConfigureAwait(false);
            await _store.AddEdge(nodeId, cid, "contains").ConfigureAwait(false);
        }

        _logger.LogInformation("KbGraph: ingested '{Id}' ({T}) with {C} concepts",
            id, title, concepts.Count);
        return $"Ingested '{title}' with {concepts.Count} concepts";
    }

    public async Task<string> IngestFact(string id, string content,
        string category = "general", string? sourceId = null)
    {
        var props = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["category"] = category
        };
        var nodeId = await _store.UpsertNode(
            extId: $"fact:{id}",
            kind: "fact",
            name: content.Length > 100 ? content[..100] + "…" : content,
            ns: category,
            props: props).ConfigureAwait(false);

        await _store.AddDoc(nodeId, content, "zh", source: "").ConfigureAwait(false);

        if (sourceId != null)
        {
            var src = await _store.GetNodeByExtId(sourceId).ConfigureAwait(false);
            if (src != null) await _store.AddEdge(src.Id, nodeId, "has_fact").ConfigureAwait(false);
        }
        return $"Ingested fact '{id}'";
    }

    // ═══════════════════════════════════════════
    //  Office document indexing
    // ═══════════════════════════════════════════

    private static readonly HashSet<string> OfficeExts =
        new(StringComparer.OrdinalIgnoreCase) { ".docx", ".xlsx", ".pptx" };

    /// <summary>
    /// Ingest a single Office file (.docx / .xlsx / .pptx) into the KG store.
    /// Extracts text, chunks by logical sections (paragraphs / sheets / slides),
    /// stores as "document" nodes with concepts.
    /// </summary>
    public async Task<string> IngestOfficeFile(string filePath)
    {
        if (!File.Exists(filePath))
            return $"File not found: {filePath}";

        var ext = Path.GetExtension(filePath);
        if (!OfficeExts.Contains(ext))
            return $"Unsupported Office format: {ext}";

        string content;
        try
        {
            content = ext switch
            {
                ".docx" => OfficeDocumentReader.ExtractWordText(filePath),
                ".xlsx" => OfficeDocumentReader.ExtractExcelText(filePath),
                ".pptx" => OfficeDocumentReader.ExtractPptText(filePath),
                _ => "",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KbGraph: failed to read {File}", filePath);
            return $"Error: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(content))
            return "No text content found in " + Path.GetFileName(filePath);

        var fileName = Path.GetFileName(filePath);
        var relPath = filePath;

        // Chunk by logical sections (double-newline separation from extractors)
        var chunks = content.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);

        int ingested = 0;
        foreach (var chunk in chunks)
        {
            var trimmed = chunk.Trim();
            if (trimmed.Length < 20) continue;

            // Use section heading as title (first line or chunk prefix)
            var title = trimmed.Split('\n')[0];
            if (title.Length > 100) title = title[..100] + "…";
            var sourceLabel = $"{fileName}:{title}";

            await IngestDocument(
                id: $"office:{fileName}:{ingested}:{Guid.NewGuid().ToString("N")[..8]}",
                title: title,
                content: trimmed,
                source: sourceLabel,
                lang: "zh").ConfigureAwait(false);
            ingested++;
        }

        _logger.LogInformation("KbGraph: ingested '{F}' → {N} chunks", fileName, ingested);
        return $"Ingested '{fileName}' → {ingested} sections";
    }

    /// <summary>
    /// Batch-index all Office files under a directory.
    /// </summary>
    public async Task<string> BuildOfficeIndexAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return $"Directory not found: {directoryPath}";

        var files = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.AllDirectories)
            .Where(f => OfficeExts.Contains(Path.GetExtension(f)))
            .ToList();

        if (files.Count == 0)
            return "No Office files found in " + directoryPath;

        int ok = 0, fail = 0;
        foreach (var file in files)
        {
            var result = await IngestOfficeFile(file).ConfigureAwait(false);
            if (result.StartsWith("Error")) fail++;
            else ok++;
        }

        return $"Indexed {ok} / {ok + fail} Office documents";
    }

    // ═══════════════════════════════════════════
    //  Private
    // ═══════════════════════════════════════════

    /// <summary>
    /// LLM query expansion: generates 3 groups of search terms —
    /// core keywords, synonyms/related terms, and English equivalents (for Chinese queries).
    /// </summary>
    /// <summary>
    /// L0 短路判断：简单查询直接返回，不触发 LLM rewrite。
    /// 简单条件：≤4 个词、无特殊符号、无代码标记。
    /// </summary>
    private static bool IsSimpleQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 50) return false;
        var wordCount = query.Split([' ', '，', '。', '、'], StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 4) return false;
        // 包含代码特殊字符 → 走 LLM
        if (query.Any(c => c is '_' or '.' or '/' or '\\' or '(' or ')' or '[' or ']' or '<' or '>'))
            return false;
        return true;
    }

    private async Task<string> ExpandQueryAsync(string query, CancellationToken ct)
    {
        // L0 短路：简单查询不触发 LLM
        if (_rewriter == null || IsSimpleQuery(query)) return query;
        try
        {
            var prompt = $"""
                You are a search query expander. Given a query, produce expanded search terms.
                
                Rules:
                - Group 1: Core keywords from the original query (3-5 terms)
                - Group 2: Synonyms and related technical terms (2-4 terms)
                - Group 3: If the query is Chinese, add English equivalents (1-3 terms)
                
                Return ALL terms on a single line, space-separated.
                No explanations, no numbering.
                
                Examples:
                Query: 用户登录失败
                → login failure authentication UserService error 认证 失败 用户登录
                
                Query: 内存泄漏怎么排查
                → memory leak排查 GC dump heap allocation 内存 泄漏
                
                Query: {query}
                """;
            var resp = await _rewriter.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct).ConfigureAwait(false);
            var result = resp.Text?.Trim() ?? "";
            return string.IsNullOrWhiteSpace(result) ? query : result;
        }
        catch { return query; }
    }

    /// <summary>
    /// Centroid embeddings for knowledge-seeking vs casual chat intent classification.
    /// Uses FastEmb (zero API cost, pure math) to decide whether a query needs KG lookup.
    /// </summary>
    private static readonly string[] KnowledgeAnchors =
    [
        "查找资料 搜索文档 查询信息 寻找代码",
        "什么是 是什么 怎么用 如何使用 如何实现",
        "为什么 原因 区别 对比 分析 比较",
        "代码在哪里 函数定义 方法实现 类结构",
        "解释一下 说明 介绍 总结 概括",
        "错误 问题 故障 异常 解决 修复",
        "配置 安装 部署 设置 参数 选项",
        // ── C# /.NET ──
        "接口 API endpoint 路由 控制器",
        "类 结构体 枚举 接口 抽象类 继承 多态",
        "方法 函数 属性 字段 事件 委托 lambda",
        "配置 依赖注入 DI 中间件 服务注册 容器",
        "报错 异常 堆栈 日志 调试 断点 运行时 crash",
        "ORM 数据库 SQL 查询 事务 迁移 索引",
        "测试 单元测试 xUnit NUnit Moq 断言 mock",
        "async await Task Task.Run 异步 并行 线程",
        "LINQ 查询 表达式 IEnumerable IQueryable 集合",
        "HttpClient 请求 响应 REST API 认证 JWT",
        "内存 性能 优化 缓存 池化 GC 泄漏 分析",
        // ── Python ──
        "Python pip conda venv 虚拟环境 依赖",
        "pandas numpy matplotlib 数据分析 科学计算",
        "Django Flask FastAPI 框架 路由 中间件 视图",
        "async def await asyncio 协程 异步",
        // ── JavaScript / TypeScript / Node ──
        "JavaScript JS TypeScript TS Node.js 前端 后端",
        "React Vue Angular SPA 组件 状态管理 Redux Pinia",
        "npm yarn pnpm 包管理 依赖 构建 webpack vite",
        "async await Promise callback 回调 事件循环",
        "ESLint Prettier Babel TypeScript 类型 接口",
        // ── Rust ──
        "Rust cargo 所有权 借用 生命周期 lifetime",
        "unsafe trait impl 泛型 宏 模式匹配 match",
        "async await tokio 异步 运行时 并发",
        // ── Go ──
        "Go golang go mod 包管理 goroutine channel",
        "interface struct defer error 错误处理 并发",
        // ── DevOps & Cloud ──
        "Docker 容器 镜像 dockerfile compose 编排",
        "Kubernetes K8s pod service deployment ingress",
        "CI CD 流水线 持续集成 持续部署 GitHub Actions",
        "AWS Azure GCP 云服务 对象存储 S3 函数计算",
        "Linux 服务器 shell bash 命令 进程 文件系统",
        "Nginx 反向代理 负载均衡 SSL 证书 HTTPS",
        // ── 前端 / 样式 ──
        "HTML CSS 布局 flex grid 动画 响应式 移动端",
        "浏览器 DOM 事件 渲染 性能 缓存 跨域 CORS",
        // ── Shell / 工具链 ──
        "命令行 CLI 终端 terminal bash zsh 管道 重定向",
        "git 版本控制 commit branch merge rebase PR",
        "正则表达式 regex grep sed awk 文本处理",
        // ── 网络 / 协议 ──
        "网络 TCP IP HTTP WebSocket gRPC DNS 代理",
        "RESTful gRPC GraphQL 序列化 JSON Protobuf",
        "Socket 长连接 短连接 心跳 重连 超时",
        // ── 安全 ──
        "安全 加密 解密 SSL TLS HTTPS 证书 密钥",
        "XSS CSRF SQL注入 认证 授权 OAuth JWT SSO",
        "防火墙 入侵检测 审计 权限 沙箱 隔离",
        // ── 架构 / 设计 ──
        "架构 微服务 分布式 高可用 负载均衡 容错",
        "设计模式 单例 工厂 观察者 策略 依赖注入",
        "CQRS 事件驱动 消息队列 最终一致性 Saga",
        "数据库 关系型 NoSQL 缓存 Redis 分库分表",
        // ── 算法 / 数据结构 ──
        "算法 数据结构 排序 搜索 树 图 哈希表 栈 队列",
        "时间复杂度 空间复杂度 递归 动态规划 贪心",
        "机器学习 深度学习 神经网络 NLP CV 训练 推理",
    ];

    private static readonly string[] SkipAnchors =
    [
        "你好 您好 hi hello hey 嗨 嘿嘿",
        "谢谢 感谢 多谢 辛苦了 好的 ok 嗯 哈哈",
        "再见 拜拜 明天见 回头聊",
        "今天星期几 几点了 现在几点 今天几号",
        "1+1 一加一 算一下 计算",              // simple math
        "在吗 在不在 有空吗 测试 试一下",
        "你会做什么 你能做什么 你会写代码吗",   // capability questions
        "你会什么 你有什么功能 你能干嘛",
        "帮我个忙 帮我一下 我问你个问题",
        "你好聪明 你真厉害 你太棒了",           // compliments
        "不懂 不知道 不会 没听懂 再说一遍",
        "测试测试 只是测试 试试看",
    ];

    private static float[]? _knowledgeCentroid;
    private static float[]? _skipCentroid;
    private static readonly object _centroidLock = new();

    private static void EnsureCentroids()
    {
        if (_knowledgeCentroid != null) return;
        lock (_centroidLock)
        {
            if (_knowledgeCentroid != null) return;
            _knowledgeCentroid = ComputeCentroid(KnowledgeAnchors);
            _skipCentroid = ComputeCentroid(SkipAnchors);
        }
    }

    private static float[] ComputeCentroid(string[] anchors)
    {
        const int dim = 384;
        var sum = new float[dim];
        int count = 0;

        // 优先使用 ONNX LocalEmbedder（BGE 模型），不可用时回退 FastEmb
        var localEmb = GetSharedEmbedder();

        foreach (var anchor in anchors)
        {
            float[] emb;
            if (localEmb.Available)
            {
                emb = localEmb.Generate(anchor);
            }
            else
            {
                emb = LTAI.AI.EmbeddingClient.FastEmb(anchor, dim);
            }
            if (emb.Length == 0) continue;
            for (int i = 0; i < Math.Min(emb.Length, dim); i++) sum[i] += emb[i];
            count++;
        }
        if (count > 0)
            for (int i = 0; i < dim; i++) sum[i] /= count;
        return sum;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < len; i++)
        { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }

    /// <summary>代码模式启发式检测 — 含 C#/代码关键字则强制走 KG。</summary>
    private static bool ContainsCodePattern(string text)
    {
        // C# 语言关键字
        var codePatterns = new[]
        {
            "async", "await", "Task<", "Task.", "IEnumerable", "IQueryable",
            "namespace ", "class ", "interface ", "struct ", "enum ", "record ",
            "void ", "int ", "string ", "bool ", "var ", "new ", "null ",
            "=>", "::", "??", "?.", "??=",
            ".cs", ".csproj", ".sln",
            "HttpClient", "HttpResponse", "IActionResult",
            "ConfigureAwait", "GetAwaiter", "ValueTask",
            "List<", "Dictionary<", "HashSet<", "Concurrent",
            "public ", "private ", "protected ", "internal ", "static ",
            "readonly", "virtual", "override", "abstract", "sealed",
            "partial", "ref ", "out ", "in ", "params",
        };
        if (codePatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 包含成对的圆括号且长度 > 10（类函数调用语法）
        if (text.Length > 10)
        {
            int open = 0, close = 0;
            foreach (var c in text) { if (c == '(') open++; if (c == ')') close++; }
            if (open >= 2 && close >= 2) return true;
        }

        return false;
    }

    /// <summary>Intent-based KG gate. Uses FastEmb + cosine similarity.</summary>
    internal static bool IsKnowledgeQuery(string text)
    {
        // 代码模式 → 强制走 KG（跳过 centroid 分类）
        if (ContainsCodePattern(text))
            return true;

        EnsureCentroids();
        var emb = LTAI.AI.EmbeddingClient.FastEmb(text.Trim(), 384);
        var knowledgeScore = CosineSimilarity(emb, _knowledgeCentroid!);
        var skipScore = CosineSimilarity(emb, _skipCentroid!);
        return knowledgeScore > skipScore + 0.05f;
    }

    // 共享 LocalEmbedder 实例 — 避免每次查询都加载 90MB ONNX 模型
    private static readonly Lazy<LocalEmbedder> _sharedEmbedder = new(() => new LocalEmbedder(), true);

    private static LocalEmbedder GetSharedEmbedder() => _sharedEmbedder.Value;

    private static string FormatNode(NodeRow node)
    {
        var icon = node.Kind switch
        {
            "document" => "📄", "concept" => "🏷️", "fact" => "💡",
            _ => "▪️"
        };
        return $"{icon} [{node.Kind}] {node.Name}" +
               (string.IsNullOrEmpty(node.Namespace) ? "" : $" ({node.Namespace})");
    }

    private static List<string> ExtractConcepts(string title, string content)
    {
        return (title + " " + content)
            .Split([' ', '\n', '\r', ',', '.', '(', ')', '【', '】', '：', '，', '。'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }
}

