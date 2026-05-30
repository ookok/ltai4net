// Tool RAG: 动态工具召回替代全量注入
// 每个工具的 (name + description) 用 FastEmb 向量化，
// 运行时按用户意图检索 Top-K 个相关工具注入到 LLM。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.AI;

namespace LTAI.AI;

/// <summary>
/// 工具注册表。
/// 离线收集所有工具的 (name + description + embedding)，
/// 在线按用户消息语义检索最相关的 Top-K 工具。
/// </summary>
public static class ToolRegistry
{
    /// <summary>单个工具的定义 + embedding。</summary>
    public sealed record ToolDef(string Name, string Description, float[] Embedding);

    private static readonly List<ToolDef> _tools = new();
    private static bool _initialized;
    private static readonly object _lock = new();

    /// <summary>初始化工具注册表：计算所有工具的 embedding。</summary>
    public static void Initialize(IEnumerable<AITool> tools)
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            foreach (var tool in tools)
            {
                var desc = tool.Description ?? "";
                var name = tool.Name ?? "unknown";
                // 拼接 name + description 作为 embedding 文本
                var text = $"{name}: {desc}";
                var emb = EmbeddingClient.FastEmb(text);
                _tools.Add(new ToolDef(name, desc, emb));
            }
            _initialized = true;
        }
    }

    /// <summary>按用户查询检索 Top-K 个最相关的工具。</summary>
    public static List<ToolDef> SearchTopK(string query, int k = 8)
    {
        if (!_initialized || _tools.Count == 0) return new List<ToolDef>();

        var qEmb = EmbeddingClient.FastEmb(query);
        var scored = _tools
            .Select(t => (tool: t, score: CosineSimilarity(qEmb, t.Embedding)))
            .OrderByDescending(x => x.score)
            .Take(k)
            .Select(x => x.tool)
            .ToList();

        return scored;
    }

    /// <summary>获取所有已注册的工具。</summary>
    public static IReadOnlyList<ToolDef> AllTools => _tools;

    /// <summary>清空注册表（用于测试或重新加载）。</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _tools.Clear();
            _initialized = false;
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < len; i++)
        { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? dot / (MathF.Sqrt(na) * MathF.Sqrt(nb)) : 0;
    }
}
