using LTAI.Core.System;
using LTAI.Vector.Knowledge;
using LTAI.Vector.Knowledge.Models;

namespace LTAI.TreeLLM.Prompting;

public sealed class ContextProviderRouter
{
    private readonly RetrievalFramework _retrievalFramework;
    private readonly AgenticRAG _agenticRAG;
    private readonly PromptBuilder _promptBuilder;

    public ContextProviderRouter(
        AgenticRAG agenticRAG,
        PromptBuilder promptBuilder)
    {
        _retrievalFramework = RetrievalFramework.Instance;
        _agenticRAG = agenticRAG;
        _promptBuilder = promptBuilder;
    }

    public RoutingDecision Route(string query, string? sessionId = null, RoutingOptions? options = null)
    {
        var opts = options ?? new RoutingOptions();
        var queryShape = _retrievalFramework.Classify(query);
        var strategy = _retrievalFramework.GetStrategy(query);

        var (mode, reason) = ClassifyMode(query, queryShape, opts);

        var docs = _agenticRAG.Search(query, RAGMode.Iterative,
            domain: opts.Domain ?? "general");

        var retrievalQuality = ComputeRetrievalQuality(docs);

        if (mode == ProviderMode.Enhance)
        {
            var enrichment = _promptBuilder.BuildContextSection(docs, query,
                new PromptBuildOptions { IncludeCitations = false });
            return new RoutingDecision(
                mode, reason, query,
                EnrichedContext: enrichment,
                SourceDocuments: docs,
                RetrievalQuality: retrievalQuality);
        }

        if (mode == ProviderMode.Critic)
        {
            var critiqueContext = BuildCritiqueContext(docs, query);
            return new RoutingDecision(
                mode, reason, query,
                EnrichedContext: critiqueContext,
                SourceDocuments: docs,
                RetrievalQuality: retrievalQuality);
        }

        var personalContext = sessionId != null
            ? $"Session: {sessionId}\n用户问题: {query}\n目标: 以用户的身份和立场回应"
            : query;

        return new RoutingDecision(
            mode, reason, personalContext,
            EnrichedContext: mode == ProviderMode.Self ? null : BuildExternalEnrichment(docs, query),
            SourceDocuments: docs,
            RetrievalQuality: retrievalQuality);
    }

    private (ProviderMode Mode, string Reason) ClassifyMode(
        string query, QueryShape shape, RoutingOptions opts)
    {
        if (opts.ForceMode.HasValue)
            return (opts.ForceMode.Value, "手动指定");

        var result = ClassificationRegistry.ProviderMode.Classify(query);
        if (result != "Self")
        {
            return result switch
            {
                "ThirdParty" => (ProviderMode.ThirdParty, "代表用户: 第三方模式"),
                "Enhance" => (ProviderMode.Enhance, "上下文增强请求"),
                "Critic" => (ProviderMode.Critic, "批判性评估"),
                _ => (ProviderMode.Self, "默认: 自我模式")
            };
        }

        if (shape == QueryShape.AggregationSummary || shape == QueryShape.ProceduralHowTo)
            return (ProviderMode.Enhance, "结构化查询: 上下文增强");

        if (shape == QueryShape.ComparativeAnalysis || shape == QueryShape.MultiHop)
            return (ProviderMode.Critic, "复杂分析: 批判模式");

        return (ProviderMode.Self, "默认: 自我模式");
    }

    private static double ComputeRetrievalQuality(List<KnowledgeSearchResult> docs)
    {
        if (docs.Count == 0) return 0;

        var avgScore = docs.Average(d => d.Score);
        var diversity = 1.0 - docs
            .Select(d => d.Content[..Math.Min(100, d.Content.Length)].ToLower())
            .Distinct()
            .Count() / (double)Math.Max(1, docs.Count);

        var sourceRichness = Math.Min(1.0, docs.Count / 15.0);

        return avgScore * 0.4 + diversity * 0.3 + sourceRichness * 0.3;
    }

    private static string BuildExternalEnrichment(
        List<KnowledgeSearchResult> docs, string query)
    {
        if (docs.Count == 0) return query;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Third-Party Context Enrichment]");
        sb.AppendLine("以下是在代表用户与第三方交互时提供的上下文背景：");
        sb.AppendLine();

        foreach (var doc in docs.Take(5))
        {
            var summary = PromptBuilder.HeuristicSummarize(doc.Content, 250);
            sb.AppendLine($"- {summary}");
        }

        sb.AppendLine();
        sb.AppendLine($"原始请求: {query}");
        sb.AppendLine("请基于以上背景，以用户的身份和立场进行回应。");

        return sb.ToString();
    }

    private static string BuildCritiqueContext(
        List<KnowledgeSearchResult> docs, string query)
    {
        if (docs.Count == 0)
            return $"## 批判性评估\n原始内容: {query}\n\n评估: 无足够上下文进行评估。";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 批判性评估 (Context Critic)");
        sb.AppendLine($"评估目标: {query}");
        sb.AppendLine();
        sb.AppendLine("### 参考依据");

        foreach (var doc in docs.Take(5))
        {
            var summary = PromptBuilder.HeuristicSummarize(doc.Content, 200);
            sb.AppendLine($"- [{doc.Domain ?? "general"}] (相关度: {doc.Score:F2}) {summary}");
        }

        sb.AppendLine();
        sb.AppendLine("### 评估维度");
        sb.AppendLine("1. 事实准确性 (基于检索证据)");
        sb.AppendLine("2. 逻辑一致性");
        sb.AppendLine("3. 完整性 (是否有遗漏信息)");
        sb.AppendLine("4. 上下文适用性");
        sb.AppendLine();
        sb.AppendLine("请提供结构化的批判性反馈和建议。");

        return sb.ToString();
    }
}

public sealed class RoutingOptions
{
    public ProviderMode? ForceMode { get; set; }
    public string? Domain { get; set; }
    public bool IncludePersonalContext { get; set; } = true;
}

public sealed record RoutingDecision(
    ProviderMode Mode,
    string Reason,
    string TargetContext,
    string? EnrichedContext = null,
    List<LTAI.Vector.Knowledge.Models.KnowledgeSearchResult>? SourceDocuments = null,
    double RetrievalQuality = 0)
{
    public bool ShouldEnrich => Mode is ProviderMode.Enhance or ProviderMode.ThirdParty;
    public bool ShouldCritique => Mode == ProviderMode.Critic;
    public override string ToString() => $"[{Mode}] {Reason} (quality={RetrievalQuality:F2})";
}
