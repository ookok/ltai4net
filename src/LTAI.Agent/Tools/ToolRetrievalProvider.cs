// Tool RAG: 动态工具召回 AIContextProvider
// 运行时拦截工具列表，按用户消息语义检索 Top-K 个相关工具，
// 替代全量 80+ 工具注入。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LTAI.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Shared.DiagnosticIds;

namespace LTAI.Agent.Tools;

/// <summary>
/// MAF AIContextProvider：在每次 agent 调用前按用户意图动态召回工具。
/// 工作流：
///   1. 首次调用时使用 ONNX（优先）对全部已注册工具建索引
///   2. 每次请求取用户最后一条消息，做 ONNX 语义检索取 Top-K（默认 8）
///   3. 保留兜底工具: ReadFileContent, ListTools, WebFetch, RunCommand
///   4. 替换 context.Tools 为召回的子集
///
/// 注册位置：AIContextProviders 列表的第一个（最先执行）。
/// </summary>
public sealed class ToolRetrievalProvider : AIContextProvider
{
    // 兜底工具：无论用户说什么，这些工具始终可用
    private static readonly HashSet<string> PinnedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ReadFileContent", "ListTools", "ListFiles", "WebFetch", "WebSearch",
        "RunCommand", "ListDirectory", "DirectoryTree", "Glob",
        "GetCurrentDateTime",
    };
    // 始终排除的元工具（不暴露给普通对话）
    private static readonly HashSet<string> ExcludedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        // ToolRegistry 内部使用，不暴露给 LLM
    };

    private const int DefaultTopK = 8;
    private static bool _initialized;
    private readonly EmbeddingClient _embedder;
    private readonly ToolEmbeddingCache? _cache;
    private readonly string? _domain;
    private readonly HashSet<string>? _domainFilter;

    /// <param name="embedder">Embedding 客户端。</param>
    /// <param name="domain">可选领域过滤。当指定时，Tool RAG 对同 domain 工具加分优先召回。</param>
    /// <param name="domainFilter">可选的域名白名单。当指定时，只考虑标记了这些领域的工具参与检索。</param>
    /// <param name="cache">P12.2: 可选 embedding 缓存。注入后工具描述嵌入跨进程重启复用, 冷启动 0 ONNX 调用。</param>
    public ToolRetrievalProvider(EmbeddingClient embedder, string? domain = null,
        HashSet<string>? domainFilter = null, ToolEmbeddingCache? cache = null) : base(null, null, null)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _domain = domain;
        _domainFilter = domainFilter;
        _cache = cache;
    }

    // ── Override InvokingCoreAsync to REPLACE tools instead of concatenating ──
    // MAF base class merges via a.Concat(b), which defeats ToolRetrievalProvider's
    // filtering: every provider that returns non-null Tools doubles the list.
    // We keep messages/instructions merge the same but substitute tools.
#pragma warning disable MAAI001 // Experimental
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        var inputContext = context.AIContext;

        var filteredContext = new InvokingContext(
            context.Agent,
            context.Session,
            new AIContext
            {
                Instructions = inputContext.Instructions,
                Messages = inputContext.Messages is not null
                    ? ProvideInputMessageFilter(inputContext.Messages)
                    : null,
                Tools = inputContext.Tools
            });

        var provided = await ProvideAIContextAsync(filteredContext, cancellationToken).ConfigureAwait(false);

        var mergedInstructions = (inputContext.Instructions, provided.Instructions) switch
        {
            (null, null) => null,
            (string a, null) => a,
            (null, string b) => b,
            (string a, string b) => a + "\n" + b
        };

        var providedMessages = provided.Messages is not null
            ? provided.Messages.Select(m => m.WithAgentRequestMessageSource(
                AgentRequestMessageSourceType.AIContextProvider, GetType().FullName!))
            : null;

        var mergedMessages = (inputContext.Messages, providedMessages) switch
        {
            (null, null) => null,
            (var a, null) => a,
            (null, var b) => b,
            (var a, var b) => a.Concat(b)
        };

        // KEY FIX: Replace tools instead of concatenating.
        // ToolRetrievalProvider selects a relevant subset for the current query.
        // Downstream providers may add their own tools via the standard merge.
        var mergedTools = provided.Tools ?? inputContext.Tools;

        return new AIContext
        {
            Instructions = mergedInstructions,
            Messages = mergedMessages,
            Tools = mergedTools
        };
    }
#pragma warning restore MAAI001

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct = default)
    {
        var existing = context.AIContext;
        if (existing?.Tools is null || !existing.Tools.Any())
            return existing!;

        // [Fix 3] 按 domainFilter 过滤工具候选集
        var candidates = existing.Tools;
        if (_domainFilter != null && _domainFilter.Count > 0)
        {
            candidates = existing.Tools.Where(t =>
            {
                var d = GetToolDomain(t);
                return string.IsNullOrEmpty(d) || _domainFilter.Contains(d);
            }).ToList();
            if (!candidates.Any())
                candidates = existing.Tools;
        }

        // 首次调用：使用 ONNX（优先）初始化 ToolRegistry
        if (!_initialized)
        {
            await ToolRegistry.InitializeAsync(candidates.ToList(), _embedder, _cache, ct).ConfigureAwait(false);
            _initialized = true;
// Tool registration logging removed — use LTAI debug tracing if needed
        }

        // 取用户最后一条消息作为查询
        var query = GetUserQuery(context);
        var selectedTools = new List<AITool>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            // ONNX 语义检索 Top-K（支持 domain 加权）
            var hits = await ToolRegistry.SearchTopKAsync(query, _embedder, _domain, DefaultTopK, ct)
                .ConfigureAwait(false);
            var hitNames = new HashSet<string>(hits.Select(h => h.Name), StringComparer.OrdinalIgnoreCase);

            // 从候选工具列表中选出：命中的 + 兜底的
            foreach (var tool in candidates)
            {
                var name = tool.Name ?? "";
                if (ExcludedTools.Contains(name)) continue;
                if (hitNames.Contains(name) || PinnedTools.Contains(name))
                    selectedTools.Add(tool);
            }
        }

        // 如果检索结果太少（不足 3 个），回退策略：保留兜底工具 + 按原顺序填充到 DefaultTopK
        // 不降级到 PinnedTools-only（否则领域 agent 如 LTAI-Math 会清空 shell/container 工具）
        if (selectedTools.Count < 3)
        {
            selectedTools.Clear();
            var pinned = new List<AITool>();
            var rest = new List<AITool>();
            foreach (var tool in candidates)
            {
                var name = tool.Name ?? "";
                if (ExcludedTools.Contains(name)) continue;
                if (PinnedTools.Contains(name)) pinned.Add(tool);
                else rest.Add(tool);
            }
            selectedTools.AddRange(pinned);
            selectedTools.AddRange(rest.Take(Math.Max(0, DefaultTopK - pinned.Count)));
        }

        return new AIContext
        {
            Tools = selectedTools,
        };
    }

    private static string GetToolDomain(AITool tool)
    {
        try
        {
            if (tool is AIFunction func && func.UnderlyingMethod != null)
                return func.UnderlyingMethod.GetCustomAttribute<ToolDomainAttribute>(false)?.Domain ?? "";
        }
        catch { }
        return "";
    }

    private static string GetUserQuery(InvokingContext context)
    {
        // 取会话中最后一条用户消息
        var msgs = context.AIContext?.Messages;
        if (msgs == null) return "";

        // 从后往前找用户消息，可取最近 1-3 轮拼接
        var parts = new List<string>();
        foreach (var m in msgs.Reverse())
        {
            if (m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
            {
                parts.Add(m.Text.Trim());
                if (parts.Count >= 2) break; // 取最近 2 轮
            }
        }
        parts.Reverse();
        return string.Join(" ", parts);
    }
}
