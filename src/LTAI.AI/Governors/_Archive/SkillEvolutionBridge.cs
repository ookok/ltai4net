using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LTAI.AI.Governors;

// ============================================================================
// ASI-Evolve inspired: Skill Evolution Bridge
// Closes the loop: ExperimentAnalyzer → distilled lesson → auto-create/upgrade Skill
// Connects ExperimentAnalyzer output to SkillExtractor and SkillTree.
// ============================================================================

public sealed record SkillEvolutionProposal
{
    public string LessonId { get; init; } = "";
    public string Domain { get; init; } = "";
    public string ProposedSkillName { get; init; } = "";
    public string ProposedSkillContent { get; init; } = "";
    public int Layer { get; init; }
    public float Confidence { get; init; }
    public int EvidenceCount { get; init; }
    public List<string> EvidenceIds { get; init; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed class SkillEvolutionBridge
{
    private readonly ExperimentAnalyzer _analyzer;
    private readonly ILogger<SkillEvolutionBridge> _logger;
    private readonly ConcurrentDictionary<string, SkillEvolutionProposal> _proposals = new();
    private readonly ConcurrentDictionary<string, int> _lessonSkillLinks = new();
    private int _totalProposals;
    private int _totalPromoted;
    private const int MinEvidenceThreshold = 3;

    public SkillEvolutionBridge(
        ExperimentAnalyzer analyzer,
        ILogger<SkillEvolutionBridge>? logger = null)
    {
        _analyzer = analyzer;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SkillEvolutionBridge>.Instance;
    }

    // ========================================================================
    // 1. Propose a new skill from analyzed lessons
    // ========================================================================

    public SkillEvolutionProposal? ProposeSkill(string lessonId, string domain, string skillRootDir)
    {
        var relevantLessons = _analyzer.RetrieveRelevantLessons(
            $"domain:{domain}", domain, topK: 10);

        var successLessons = relevantLessons
            .Where(l => l.Impact > 0.5f && l.Generalizability > 0.4f)
            .ToList();

        if (successLessons.Count < MinEvidenceThreshold)
        {
            _logger.LogDebug("SkillEvolution: insufficient evidence for domain={Domain} (need={Need}, have={Have})",
                domain, MinEvidenceThreshold, successLessons.Count);
            return null;
        }

        var insights = successLessons.Select(l => l.Insight).ToList();
        var recommendations = successLessons.Select(l => l.Recommendation).ToList();

        var layer = DetermineLayer(successLessons.Count, successLessons.Average(l => l.Generalizability));
        var skillName = $"evolved_{domain}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        var skillContent = BuildSkillContent(domain, insights, recommendations, layer);

        var proposal = new SkillEvolutionProposal
        {
            LessonId = lessonId,
            Domain = domain,
            ProposedSkillName = skillName,
            ProposedSkillContent = skillContent,
            Layer = layer,
            Confidence = (float)successLessons.Average(l => l.Impact),
            EvidenceCount = successLessons.Count,
            EvidenceIds = successLessons.Select(l => l.Id).ToList()
        };

        _proposals[skillName] = proposal;
        Interlocked.Increment(ref _totalProposals);

        SaveProposalToFile(proposal, skillRootDir);

        _logger.LogInformation(
            "SkillEvolution: proposed skill '{Name}' (layer=L{Layer}, evidence={Evidence}, confidence={Conf:F2})",
            skillName, layer, successLessons.Count, proposal.Confidence);

        return proposal;
    }

    // ========================================================================
    // 2. Evaluate and promote an existing skill (auto-promotion)
    // ========================================================================

    public bool ShouldPromote(SkillEvolutionProposal proposal)
    {
        if (proposal.EvidenceCount < 5 || proposal.Confidence < 0.7f)
            return false;

        var existing = _proposals.Values
            .Where(p => p.Domain == proposal.Domain && p.Layer == proposal.Layer)
            .Count();

        return existing >= 2;
    }

    public void PromoteSkill(SkillEvolutionProposal proposal, string skillRootDir)
    {
        if (!ShouldPromote(proposal))
        {
            _logger.LogDebug("SkillEvolution: promotion deferred for '{Name}' (confidence={Conf:F2})",
                proposal.ProposedSkillName, proposal.Confidence);
            return;
        }

        var targetDir = proposal.Layer switch
        {
            0 => "l0_atomic",
            1 => "l1_task",
            2 => "l2_workflow",
            3 => "l3_domain",
            _ => "l1_task"
        };

        var dir = Path.Combine(skillRootDir, targetDir);
        Directory.CreateDirectory(dir);

        var filePath = Path.Combine(dir, $"{proposal.ProposedSkillName}.md");
        File.WriteAllText(filePath, proposal.ProposedSkillContent);

        Interlocked.Increment(ref _totalPromoted);

        _logger.LogInformation("SkillEvolution: promoted '{Name}' to L{Layer} ({Dir})",
            proposal.ProposedSkillName, proposal.Layer, targetDir);
    }

    // ========================================================================
    // 3. Batch propose from all domain analyses
    // ========================================================================

    public List<SkillEvolutionProposal> ProposeAllEligibleSkills(string skillRootDir)
    {
        var proposals = new List<SkillEvolutionProposal>();
        var domainRates = _analyzer.GetDomainSuccessRates();

        foreach (var (domain, rate) in domainRates)
        {
            if (rate < 0.4) continue;

            var relevant = _analyzer.RetrieveRelevantLessons($"domain:{domain}", domain, topK: 10);
            var successCount = relevant.Count(l => l.Impact > 0.5f);

            if (successCount >= MinEvidenceThreshold)
            {
                var proposal = ProposeSkill(
                    $"batch_{domain}_{DateTime.UtcNow.Ticks}",
                    domain, skillRootDir);

                if (proposal != null)
                {
                    proposals.Add(proposal);
                    if (ShouldPromote(proposal))
                        PromoteSkill(proposal, skillRootDir);
                }
            }
        }

        return proposals;
    }

    // ========================================================================
    // 4. Stats
    // ========================================================================

    public Dictionary<string, object> GetStats() => new()
    {
        ["total_proposals"] = _totalProposals,
        ["total_promoted"] = _totalPromoted,
        ["pending_proposals"] = _proposals.Count,
        ["domains"] = _proposals.Values.Select(p => p.Domain).Distinct().ToList()
    };

    // ========================================================================
    // Private helpers
    // ========================================================================

    private static int DetermineLayer(int evidenceCount, double generalizability)
    {
        if (evidenceCount >= 50 && generalizability >= 0.85) return 3;
        if (evidenceCount >= 10 && generalizability >= 0.7) return 2;
        if (evidenceCount >= 5 && generalizability >= 0.5) return 1;
        return 0;
    }

    private static string BuildSkillContent(
        string domain, List<string> insights, List<string> recommendations, int layer)
    {
        var content = $"# {domain} — Evolved Skill (L{layer})\n\n";
        content += $"layer: {layer}\n";
        content += $"domain: {domain}\n";
        content += $"created: {DateTime.UtcNow:O}\n";
        content += $"---\n\n";
        content += $"## Trigger\n";
        content += $"- When user query involves {domain}\n";
        content += $"- When routing decision needs domain-specific optimization\n\n";
        content += $"## Steps\n";
        content += $"1. Retrieve relevant prior lessons from ExperimentAnalyzer\n";
        content += $"2. Apply highest-impact recommendation based on query pattern\n";
        content += $"3. Route to optimal provider based on learned patterns\n\n";
        content += $"## Verification\n";
        content += $"- Success rate > 60% on {domain} domain tasks\n";
        content += $"- Latency within budget for selected route\n";
        content += $"- Grounding verification passes\n\n";
        content += $"## Accumulated Insights\n";

        foreach (var insight in insights.Take(5))
            content += $"- {insight}\n";

        content += $"\n## Recommendations\n";
        foreach (var rec in recommendations.Take(5))
            content += $"- {rec}\n";

        return content;
    }

    private static void SaveProposalToFile(SkillEvolutionProposal proposal, string skillRootDir)
    {
        try
        {
            var dir = Path.Combine(skillRootDir, "pending");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{proposal.ProposedSkillName}.md");
            File.WriteAllText(path, proposal.ProposedSkillContent);
        }
        catch { }
    }
}
