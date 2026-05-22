using System.Text;
using LTAI.Core.System;
using LTAI.Knowledge.Core;
using Microsoft.Extensions.AI;

namespace LTAI.Agent.Prompting;

public enum CitationStyle { Inline, Footnote, MarkdownRef, None }
public enum TrimMode { DropOverflow, SummarizeOverflow, Tiered }

public sealed class PromptBuildOptions
{
    public string? Role { get; set; }
    public string? Domain { get; set; }
    public CitationStyle CitationStyle { get; set; } = CitationStyle.Inline;
    public int MaxContextTokens { get; set; } = 16000;
    public bool IncludeGlossary { get; set; } = true;
    public bool IncludeFusion { get; set; } = true;
    public bool IncludeCitations { get; set; } = true;
    public bool IncludeStrategyHint { get; set; } = true;
    public string? OutputFormat { get; set; }
    public Dictionary<string, string>? ExtraContext { get; set; }
    public TrimMode TrimMode { get; set; } = TrimMode.Tiered;
    public double TopTierThreshold { get; set; } = 0.7;
    public double MidTierThreshold { get; set; } = 0.3;
    public int MaxFullDocs { get; set; } = 5;
    public int MaxSummarizedDocs { get; set; } = 10;
    public string? SessionContext { get; set; }

    public static PromptBuildOptions Default => new();
}

public sealed class PromptBuilder
{
    public async Task<(string SystemPrompt, string UserPrompt)> BuildPrompt(
        string question,
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        PromptBuildOptions? options = null)
    {
        var opts = options ?? PromptBuildOptions.Default;
        var sysPrompt = await BuildSystemPrompt(question, docs, opts);
        var userPrompt = BuildUserPrompt(question, docs, opts);
        return (sysPrompt, userPrompt);
    }

    public async Task<List<ChatMessage>> BuildChatMessages(
        string question,
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        PromptBuildOptions? options = null)
    {
        var (sys, user) = await BuildPrompt(question, docs, options);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, sys),
            new(ChatRole.User, user)
        };

        return messages;
    }

    public async Task<string> BuildSinglePrompt(
        string question,
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        PromptBuildOptions? options = null)
    {
        var (sys, user) = await BuildPrompt(question, docs, options);
        return sys + "\n\n---\n\n" + user;
    }

    private async Task<string> BuildSystemPrompt(
        string question,
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        PromptBuildOptions opts)
    {
        var parts = new List<string>();

        var rolePrompt = ResolveRolePrompt(question, opts);
        if (!string.IsNullOrEmpty(rolePrompt))
            parts.Add(rolePrompt);

        if (opts.IncludeStrategyHint)
        {
            var strategyHint = ResolveStrategyHint(question);
            if (!string.IsNullOrEmpty(strategyHint))
                parts.Add(strategyHint);
        }

        if (!string.IsNullOrEmpty(opts.SessionContext))
            parts.Add(opts.SessionContext);

        if (opts.IncludeGlossary)
        {
            var glossarySection = EnrichWithGlossary(question);
            if (!string.IsNullOrEmpty(glossarySection))
                parts.Add(glossarySection);
        }

        if (docs.Count > 0)
        {
            var contextSection = await BuildContextSection(docs, question, opts);
            if (!string.IsNullOrEmpty(contextSection))
                parts.Add(contextSection);
        }

        if (!string.IsNullOrEmpty(opts.OutputFormat))
        {
            var formatSection = ResolveOutputFormat(opts);
            if (!string.IsNullOrEmpty(formatSection))
                parts.Add(formatSection);
        }

        return string.Join("\n\n", parts);
    }

    private string BuildUserPrompt(
        string question,
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        PromptBuildOptions opts)
    {
        var parts = new List<string>();

        if (docs.Count > 0)
            parts.Add("## 检索结果摘要");

        parts.Add("## 用户问题");
        parts.Add(question);

        return string.Join("\n\n", parts);
    }

    public async Task<string> BuildContextSection(
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        string question,
        PromptBuildOptions opts)
    {
        if (docs.Count == 0) return "";

        return opts.TrimMode switch
        {
            TrimMode.Tiered => await BuildTieredContextSection(docs, question, opts),
            TrimMode.SummarizeOverflow => await BuildOverflowSummarySection(docs, question, opts),
            _ => await BuildSimpleTrimSection(docs, question, opts)
        };
    }

    private async Task<string> BuildSimpleTrimSection(
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        string question,
        PromptBuildOptions opts)
    {
        var context = RankAndTrimContext(docs, opts.MaxContextTokens);
        return await RenderDocsSection(context, question, opts);
    }

    private async Task<string> BuildTieredContextSection(
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        string question,
        PromptBuildOptions opts)
    {
        var sorted = docs
            .OrderByDescending(d => d.Score)
            .ToList();

        var topTier = new List<LTAI.Knowledge.Core.Models.KnowledgeSearchResult>();
        var midTier = new List<LTAI.Knowledge.Core.Models.KnowledgeSearchResult>();
        var lowTier = new List<LTAI.Knowledge.Core.Models.KnowledgeSearchResult>();

        foreach (var doc in sorted)
        {
            if (doc.Score >= opts.TopTierThreshold && topTier.Count < opts.MaxFullDocs)
                topTier.Add(doc);
            else if (doc.Score >= opts.MidTierThreshold && midTier.Count < opts.MaxSummarizedDocs)
                midTier.Add(doc);
            else
                lowTier.Add(doc);
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 参考资料");

        if (topTier.Count > 0)
        {
            sb.AppendLine("### 高相关文档");
            for (int i = 0; i < topTier.Count; i++)
            {
                var source = FormatSource(topTier[i], i, opts.CitationStyle);
                sb.AppendLine($"#### {source}");
                sb.AppendLine(topTier[i].Content);
                sb.AppendLine();
            }
        }

        if (midTier.Count > 0)
        {
            sb.AppendLine("### 中相关文档 (摘要)");
            for (int i = 0; i < midTier.Count; i++)
            {
                var doc = midTier[i];
                var source = FormatSource(doc, i + topTier.Count, opts.CitationStyle);
                var summary = HeuristicSummarize(doc.Content, 300);
                sb.AppendLine($"#### {source}");
                sb.AppendLine(summary);
                sb.AppendLine();
            }
        }

        if (lowTier.Count > 0)
        {
            sb.AppendLine("### 低相关文档 (引用)");
            foreach (var doc in lowTier.Take(opts.MaxSummarizedDocs))
            {
                var sourceTitle = !string.IsNullOrEmpty(doc.Title) ? doc.Title : "(untitled)";
                sb.AppendLine($"- {sourceTitle}: {HeuristicSummarize(doc.Content, 100)}");
            }
            if (lowTier.Count > opts.MaxSummarizedDocs)
                sb.AppendLine($"- ... 及其他 {lowTier.Count - opts.MaxSummarizedDocs} 篇文档");
            sb.AppendLine();
        }

        if (opts.IncludeFusion && sorted.Count >= 2)
        {
            var fusionNote = await BuildFusionNote(sorted.Take(10).ToList(), question);
            if (!string.IsNullOrEmpty(fusionNote))
            {
                sb.AppendLine("### 文档关系");
                sb.AppendLine(fusionNote);
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> BuildOverflowSummarySection(
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        string question,
        PromptBuildOptions opts)
    {
        var sorted = docs.OrderByDescending(d => d.Score).ToList();
        var budget = opts.MaxContextTokens;
        var used = 0;
        var fullDocs = new List<LTAI.Knowledge.Core.Models.KnowledgeSearchResult>();
        var overflowDocs = new List<LTAI.Knowledge.Core.Models.KnowledgeSearchResult>();

        foreach (var doc in sorted)
        {
            var estimated = EstimateTokens(doc.Content);
            if (used + estimated <= budget && fullDocs.Count < opts.MaxFullDocs)
            {
                fullDocs.Add(doc);
                used += estimated;
            }
            else
            {
                overflowDocs.Add(doc);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 参考资料");

        for (int i = 0; i < fullDocs.Count; i++)
        {
            var source = FormatSource(fullDocs[i], i, opts.CitationStyle);
            sb.AppendLine($"### {source}");
            sb.AppendLine(fullDocs[i].Content);
            sb.AppendLine();
        }

        if (overflowDocs.Count > 0)
        {
            sb.AppendLine("### 其他相关文档 (摘要)");
            var summaryBudget = Math.Max(200, (opts.MaxContextTokens - used) * 4);
            var perDoc = Math.Max(80, summaryBudget / Math.Max(1, overflowDocs.Count));
            foreach (var doc in overflowDocs.Take(opts.MaxSummarizedDocs))
            {
                var sourceTitle = !string.IsNullOrEmpty(doc.Title) ? doc.Title : "(untitled)";
                sb.AppendLine($"- **{sourceTitle}** (相关度: {doc.Score:F2}): {HeuristicSummarize(doc.Content, perDoc)}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> RenderDocsSection(
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        string question,
        PromptBuildOptions opts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 参考资料");

        for (int i = 0; i < docs.Count; i++)
        {
            var doc = docs[i];
            var source = FormatSource(doc, i, opts.CitationStyle);
            sb.AppendLine($"### {source}");
            sb.AppendLine(doc.Content);
            sb.AppendLine();
        }

        if (opts.IncludeFusion && docs.Count >= 2)
        {
            var fusionNote = await BuildFusionNote(docs.ToList(), question);
            if (!string.IsNullOrEmpty(fusionNote))
            {
                sb.AppendLine("### 文档关系");
                sb.AppendLine(fusionNote);
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string HeuristicSummarize(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[。.!！?？\n])");
        var sb = new StringBuilder();
        foreach (var sent in sentences)
        {
            var trimmed = sent.Trim();
            if (trimmed.Length < 3) continue;
            if (sb.Length + trimmed.Length > maxChars) break;
            sb.Append(trimmed);
            if (!trimmed.EndsWith('\n')) sb.Append(' ');
        }

        var result = sb.ToString().TrimEnd();
        return result.Length > 0 ? result + "..." : text[..Math.Min(maxChars, text.Length)] + "...";
    }

    public string EnrichWithGlossary(string question)
    {
        var terms = ContextGlossary.Instance.Search(question);
        if (terms.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("## 术语表");

        foreach (var t in terms.Take(10))
        {
            sb.Append($"- **{t.Term}** [{t.Category}]: {t.Definition}");
            if (t.Aliases.Count > 0)
                sb.Append($" (别名: {string.Join(", ", t.Aliases)})");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> BuildFusionNote(
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        string question)
    {
        var fusionDocs = docs.Select(d => (d.Id, d.Content, (DateTime?)null)).Take(10).ToList();
        var fusionResult = await MultiDocFusionEngine.Instance.FuseAsync(fusionDocs, question);

        var notes = new List<string>();

        if (fusionResult.CrossReferences.Count > 0)
            notes.Add($"- {fusionResult.CrossReferences.Count} 处交叉引用");
        if (fusionResult.Conflicts.Count > 0)
            notes.Add($"- {fusionResult.Conflicts.Count} 处潜在矛盾 (请交叉验证)");
        if (fusionResult.ComplementaryPairs.Count > 0)
            notes.Add($"- {fusionResult.ComplementaryPairs.Count} 对互补内容");

        return notes.Count > 0 ? string.Join("\n", notes) : "";
    }

    private static string FormatSource(
        LTAI.Knowledge.Core.Models.KnowledgeSearchResult doc,
        int index,
        CitationStyle style)
    {
        var title = !string.IsNullOrEmpty(doc.Title) ? doc.Title : "(untitled)";
        var domain = !string.IsNullOrEmpty(doc.Domain) ? $" [{doc.Domain}]" : "";
        var scoreSuffix = doc.Score > 0 ? $" (相关度: {doc.Score:F2})" : "";

        return style switch
        {
            CitationStyle.Footnote => $"{title}{domain}",
            CitationStyle.MarkdownRef => $"[{index + 1}] {title}{domain}{scoreSuffix}",
            CitationStyle.None => $"{title}{domain}",
            _ => $"[{index + 1}] {title}{domain}{scoreSuffix}"
        };
    }

    private static List<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> RankAndTrimContext(
        IReadOnlyList<LTAI.Knowledge.Core.Models.KnowledgeSearchResult> docs,
        int maxTokens)
    {
        var sorted = docs
            .OrderByDescending(d => d.Score)
            .ThenByDescending(d => d.Content.Length)
            .ToList();

        var result = new List<LTAI.Knowledge.Core.Models.KnowledgeSearchResult>();
        var tokenBudget = 0;

        foreach (var doc in sorted)
        {
            var estimatedTokens = EstimateTokens(doc.Content);
            if (tokenBudget + estimatedTokens > maxTokens)
                break;
            result.Add(doc);
            tokenBudget += estimatedTokens;
        }

        return result;
    }

    private static int EstimateTokens(string text) =>
        TokenCounter.Estimate(text);

    private string ResolveRolePrompt(string question, PromptBuildOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.Role))
        {
            var systemPrompt = PromptOptimizer.Instance.BuildSystemPrompt(opts.Role);
            if (!string.IsNullOrEmpty(systemPrompt))
                return systemPrompt;
        }

        if (!string.IsNullOrEmpty(opts.Domain))
        {
            if (PromptCoach.Instance.DomainTemplates.TryGetValue(opts.Domain, out var template))
                return $"You are an expert in the '{opts.Domain}' domain.\nGuideline: {template}";
        }

        var autoRole = PromptOptimizer.Instance.PreprocessPrompt(question);
        if (autoRole != "code_reviewer" || HasExplicitDomainSignal(question))
            return PromptOptimizer.Instance.BuildSystemPrompt(autoRole);

        return PromptOptimizer.Instance.BuildSystemPrompt("code_reviewer");
    }

    private static string? ResolveStrategyHint(string question)
    {
        var strategy = RetrievalFramework.Instance.GetStrategy(question);
        return strategy.SystemPromptHint;
    }

    private static string ResolveOutputFormat(PromptBuildOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.OutputFormat))
            return $"## 输出格式\n{opts.OutputFormat}";

        if (!string.IsNullOrEmpty(opts.Role))
        {
            var tmpl = PromptOptimizer.Instance.GetRole(opts.Role);
            if (tmpl != null && !string.IsNullOrEmpty(tmpl.OutputFormat))
                return $"## 输出格式\n{tmpl.OutputFormat}";
        }

        return "";
    }

    private static bool HasExplicitDomainSignal(string question)
    {
        return ClassificationRegistry.DomainSignal.Classify(question) != "general";
    }
}
