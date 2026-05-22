using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LTAI.CLI.Improve;

/// <summary>
/// 创新匹配结果
/// </summary>
public sealed record InnovationMatch
{
    public PaperInsight Paper { get; init; } = new();
    public string LTAIModule { get; init; } = "";
    public string MatchReason { get; init; } = "";
    public double MatchScore { get; init; }
    public string SuggestedIntegration { get; init; } = "";
    public string ExpectedBenefit { get; init; } = "";
}

/// <summary>
/// 创新匹配器
/// 将论文思路与当前架构匹配，生成可落地的改进方案
/// </summary>
public sealed class InnovationMatcher
{
    private readonly ArchitectureAuditReport _auditReport;

    public InnovationMatcher(ArchitectureAuditReport auditReport)
    {
        _auditReport = auditReport;
    }

    /// <summary>
    /// 匹配论文创新点与当前架构
    /// </summary>
    public List<InnovationMatch> Match(List<PaperInsight> papers)
    {
        var matches = new List<InnovationMatch>();

        foreach (var paper in papers)
        {
            foreach (var applicable in paper.ApplicableToLTAI)
            {
                var module = IdentifyTargetModule(applicable);
                var match = new InnovationMatch
                {
                    Paper = paper,
                    LTAIModule = module,
                    MatchReason = applicable,
                    MatchScore = CalculateMatchScore(paper, module),
                    SuggestedIntegration = GenerateIntegrationSuggestion(paper, module),
                    ExpectedBenefit = GenerateExpectedBenefit(paper, module)
                };
                matches.Add(match);
            }
        }

        return matches.OrderByDescending(m => m.MatchScore).ToList();
    }

    private string IdentifyTargetModule(string applicableText)
    {
        if (applicableText.Contains("RecursiveMAS") || applicableText.Contains("RecursiveLatentPipeline"))
            return "RecursiveLatentPipeline";
        if (applicableText.Contains("LearningProgressTracker") || applicableText.Contains("PACE"))
            return "LearningProgressTracker";
        if (applicableText.Contains("FailureAttribution") || applicableText.Contains("SelfEvolution"))
            return "SelfEvolutionLoop";
        if (applicableText.Contains("SePT") || applicableText.Contains("self-improvement"))
            return "SePTMemoryBank";
        if (applicableText.Contains("BinaryVector") || applicableText.Contains("CodeGraph"))
            return "CodeGraphEnhanced";
        if (applicableText.Contains("VerifyState") || applicableText.Contains("TokenHardness"))
            return "SelectiveThinkingPipeline";
        if (applicableText.Contains("L1") || applicableText.Contains("weight"))
            return "LlamaSharpEngine";
        return "System";
    }

    private static double CalculateMatchScore(PaperInsight paper, string module)
    {
        var score = paper.RelevanceScore;
        
        // 如果论文直接提到该模块已实现，加分
        if (paper.ApplicableToLTAI.Any(a => a.Contains("already implemented")))
            score += 0.1;
        
        // 如果审计报告显示该模块有问题，加分 (说明需要改进)
        // (简化实现)
        
        return Math.Min(1.0, score);
    }

    private static string GenerateIntegrationSuggestion(PaperInsight paper, string module)
    {
        return paper.KeyInnovations.FirstOrDefault() switch
        {
            var s when s.Contains("Self-evolving") => $"Integrate self-training loop into {module}. Collect high-quality samples and use for in-context learning.",
            var s when s.Contains("Latent-space") => $"Optimize {module} latent space transfers. Consider training RecursiveLink for better cross-model alignment.",
            var s when s.Contains("Parameter Change") => $"Use ||Δθ||² signals in {module} for dynamic decision making (routing, caching, early stopping).",
            var s when s.Contains("Failure attribution") => $"Enhance {module} with causal attribution. Link failures to specific structural changes.",
            var s when s.Contains("Closed-loop") => $"Add verification step to {module}. Implement self-correction loop based on verification results.",
            var s when s.Contains("1-Bit") => $"Use binary vectors in {module} for fast similarity search and compression.",
            _ => $"Explore integration of {paper.Title} concepts into {module}."
        };
    }

    private static string GenerateExpectedBenefit(PaperInsight paper, string module)
    {
        return paper.RelevanceScore switch
        {
            > 0.9 => "High impact: Significant improvement in system capability and efficiency",
            > 0.8 => "Medium-high impact: Noticeable improvement in specific scenarios",
            > 0.7 => "Medium impact: Incremental improvement with low implementation cost",
            _ => "Low impact: Nice-to-have optimization"
        };
    }
}
