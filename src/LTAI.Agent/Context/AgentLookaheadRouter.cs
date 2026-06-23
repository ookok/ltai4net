// Copyright (c) LTAI. All rights reserved.
// ═══════════════════════════════════════════════════════
//  AgentLookaheadRouter — predicts which of the 19 agents
//  should handle a query, enabling early routing before
//  the full agent resolution chain.
//
//  Inspired by FlashMemory-DeepSeek-V4's Lookahead Sparse
//  Attention: predict future context demands proactively.
//  Extended from provider-level routing to agent-level.
// ═══════════════════════════════════════════════════════

using LTAI.AI;

namespace LTAI.Agent.Context;

/// <summary>
/// Maps query domains to the most capable agent(s).
/// Provides a fast keyword-based pre-router so the system
/// can skip irrelevant agents before invoking the full
/// agent resolution chain.
/// </summary>
public sealed class AgentLookaheadRouter
{
    private readonly Glove50Embedder? _glove;

    private static readonly Dictionary<string, string[]> DomainAgents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = ["LTAI-Dev", "LTAI-QA"],
        ["knowledge"] = ["LTAI-Arch", "LTAI-Chat"],
        ["memory"] = ["LTAI-Chat"],
        ["diary"] = ["LTAI-Chat"],
        ["system"] = ["LTAI-System", "LTAI-Ops"],
        ["document"] = ["LTAI-Office"],
        ["test"] = ["LTAI-QA"],
        ["security"] = ["LTAI-Ops"],
        ["database"] = ["LTAI-Data"],
        ["general"] = ["LTAI-Chat"],
        ["math"] = ["LTAI-Math"],
        ["writing"] = ["LTAI-Writer"],
        ["api"] = ["LTAI-Dev"],
        ["frontend"] = ["LTAI-Dev"],
        ["llm"] = ["LTAI-Dev"],
    };

    public AgentLookaheadRouter(Glove50Embedder? glove = null)
    {
        _glove = glove;
    }

    /// <summary>
    /// Predict the most relevant agent(s) for a query.
    /// Returns agent names ordered by relevance.
    /// </summary>
    public string[] Predict(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ["LTAI-Chat"];

        var domains = ClassifyDomains(query);
        var agents = domains
            .SelectMany(d => DomainAgents.TryGetValue(d, out var ags) ? ags : [])
            .Distinct()
            .ToArray();

        return agents.Length > 0 ? agents : ["LTAI-Chat"];
    }

    private string[] ClassifyDomains(string query)
    {
        var result = new List<string>();
        var lower = query.ToLowerInvariant();

        // ── Keyword matching ──
        if (ContainsAny(lower, ["code", "function", "class", "method", "refactor", "implement", "bug", "fix",
            "compile", "syntax", "api", "interface", "async", "await", "linq", "generic",
            "代码", "函数", "类", "方法", "重构", "实现", "编译"]))
            result.Add("code");

        if (ContainsAny(lower, ["what is", "explain", "concept", "architecture", "design pattern",
            "principle", "架构", "概念", "解释", "知识"]))
            result.Add("knowledge");

        if (ContainsAny(lower, ["shell", "terminal", "command", "process", "install", "config",
            "环境", "系统", "命令", "进程"]))
            result.Add("system");

        if (ContainsAny(lower, ["test", "unit test", "integration", "benchmark", "coverage", "测试"]))
            result.Add("test");

        if (ContainsAny(lower, ["security", "vulnerability", "exploit", "permission", "安全", "漏洞"]))
            result.Add("security");

        if (ContainsAny(lower, ["database", "sql", "query", "table", "index", "migration", "数据库"]))
            result.Add("database");

        if (ContainsAny(lower, ["document", "doc", "word", "excel", "ppt", "pdf", "office", "文档"]))
            result.Add("document");

        if (ContainsAny(lower, ["math", "calculate", "equation", "formula", "数学", "计算"]))
            result.Add("math");

        if (ContainsAny(lower, ["frontend", "css", "html", "react", "vue", "angular", "ui", "前端", "llm", "model", "prompt", "token", "embedding", "gpt", "transformer"]))
            result.Add("code");

        if (ContainsAny(lower, ["write", "draft", "article", "blog", "文档编写", "写作"]))
            result.Add("writing");

        if (result.Count == 0)
            result.Add("general");

        return result.ToArray();
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var kw in keywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
